using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>
/// Parses the statements of a proc body.
/// </summary>
/// <remarks>
/// <para>
/// Two of DM's statement grammars are <b>position-dependent</b>, not per-file constants.
/// <c>#pragma syntax C for</c> and <c>#pragma syntax C switch</c> change the grammar from the line
/// they appear on, and <c>#pragma push</c> / <c>#pragma pop</c> scope them. So the mode is tracked
/// as a stack while walking, and a body parsed before the pragma uses the older grammar.
/// </para>
/// <para>
/// What the <c>for</c> pragma does is <b>swap what the comma means</b>: by default a comma separates
/// clauses, and with the pragma it chains statements within one clause. Semicolons separate clauses
/// in both modes, which is why the pragma cannot be described as "enabling semicolons" —
/// compiler-verified, PLAN.md §8.
/// </para>
/// <para>
/// Recovery is line-oriented, matching <see cref="DeclarationParser"/>: an unparseable statement
/// costs its line and nothing more.
/// </para>
/// </remarks>
internal sealed class StatementParser
{
    private static readonly HashSet<string> VarModifiers =
        new(StringComparer.Ordinal) { "const", "tmp", "global", "static", "final" };

    private readonly IReadOnlyList<Token> _tokens;
    private readonly TokenSource _source;
    private readonly List<Diagnostic> _diagnostics;

    /// <summary>Shared with the declaration parser, since the pragma lives outside proc bodies.</summary>
    private readonly SyntaxModes _modes;

    private int _position;

    private StatementParser(
        IReadOnlyList<Token> tokens,
        TokenSource source,
        List<Diagnostic> diagnostics,
        int position,
        SyntaxModes modes)
    {
        _tokens = tokens;
        _source = source;
        _diagnostics = diagnostics;
        _position = position;
        _modes = modes;
    }

    /// <summary>
    /// Parses a proc body, starting immediately after the signature's closing parenthesis.
    /// </summary>
    /// <remarks>
    /// Handles both shapes: an indented block on the following lines, and a body written on the
    /// signature line itself, as <c>stddef.dm</c>'s <c>Multiply(m) return matrix(src, m)</c> does.
    /// </remarks>
    public static (BlockStatementSyntax? Body, int Position) ParseProcBody(
        IReadOnlyList<Token> tokens,
        TokenSource source,
        List<Diagnostic> diagnostics,
        int position,
        SyntaxModes modes)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(modes);

        StatementParser parser = new(tokens, source, diagnostics, position, modes);
        BlockStatementSyntax? body = parser.ParseBody();
        return (body, parser._position);
    }

    // -- token access ------------------------------------------------------

    private TokenKind Current => _position < _tokens.Count ? _tokens[_position].Kind : TokenKind.EndOfFile;

    private TokenKind Peek(int offset = 1)
        => _position + offset < _tokens.Count ? _tokens[_position + offset].Kind : TokenKind.EndOfFile;

    private bool AtEnd => _position >= _tokens.Count || Current == TokenKind.EndOfFile;

    private TextSpan CurrentSpan
        => _position < _tokens.Count ? _tokens[_position].Span : new TextSpan(_source.Text.Length, 0);

    private string TextOf(int index) => _source.TextOf(index);

    private string CurrentText => _position < _tokens.Count ? TextOf(_position) : string.Empty;

    private TextSpan SpanFrom(int startToken)
    {
        if (startToken >= _tokens.Count)
            return CurrentSpan;

        int endToken = Math.Min(Math.Max(startToken, _position - 1), _tokens.Count - 1);
        return TextSpan.FromBounds(_tokens[startToken].Span.Start, _tokens[endToken].Span.End);
    }

    private static bool IsNameLike(TokenKind kind) => kind
        is TokenKind.Identifier
        or TokenKind.KeywordSrc
        or TokenKind.KeywordUsr
        or TokenKind.KeywordWorld
        or TokenKind.KeywordGlobal;

    /// <summary>
    /// Tokens that may be a declared variable's name or a segment of its type.
    /// </summary>
    /// <remarks>
    /// Wider than <see cref="IsNameLike"/> by exactly one word. /tg/station writes
    /// <c>for(var/step in 1 to steps)</c>, which we rejected with "expected a variable name" —
    /// <c>step</c> lexes as a keyword only because <c>step()</c> is also a builtin proc.
    ///
    /// <b><c>step</c> is the only one, and getting that right needed the variable to be USED.</b>
    /// A first pass declared each contextual keyword and read it back, which compiled for
    /// <c>step</c>, <c>in</c>, <c>as</c> and <c>set</c> alike — so all four went in. Adding a single
    /// <c>name += 1</c> rejects three of them:
    /// <code>
    /// var/step = 40   step += 1   compiles, and runs: 41
    /// var/in = 40     in += 1     error, "missing left-hand argument to in."
    /// var/as = 40     as += 1     error
    /// var/set = 40    set += 1    error
    /// var/to = 40                 error at the declaration itself
    /// </code>
    /// The declaration compiling says only that the parser allowed it (PLAN.md §8). <c>step</c> is
    /// confirmed at runtime, as a local and as a loop variable.
    ///
    /// Deliberately separate rather than folded into <see cref="IsNameLike"/>: that predicate also
    /// decides labels, <c>set</c> statements and switch cases, where a contextual keyword means
    /// something.
    /// </remarks>
    private static bool IsDeclarationName(TokenKind kind)
        => IsNameLike(kind) || kind is TokenKind.KeywordStep;

    /// <summary>
    /// A path-segment keyword in a local declaration counts only as a TYPE segment, never the
    /// name: `var/datum/throw/x` compiles and `var/throw = 1` is dm.exe's "missing left-hand
    /// argument to =". So the keyword qualifies only when a separator and another segment follow.
    /// </summary>
    private bool IsKeywordTypeSegment(int index)
        => index + 2 < _tokens.Count
           && SyntaxFacts.IsPathSegmentKeyword(_tokens[index].Kind)
           && _tokens[index + 1].Kind is TokenKind.Slash or TokenKind.Dot
           && (IsDeclarationName(_tokens[index + 2].Kind)
               || SyntaxFacts.IsPathSegmentKeyword(_tokens[index + 2].Kind));

    private void Report(TextSpan span, string message)
        => _diagnostics.Add(Diagnostic.Error("DM0202", span, message));

    private ExpressionSyntax ParseExpression(bool colonTerminates = false, bool stopAtIn = false)
    {
        (ExpressionSyntax expression, int next) =
            ExpressionParser.Parse(_tokens, _source, _diagnostics, _position, colonTerminates, stopAtIn);

        _position = next > _position ? next : _position + 1;
        return expression;
    }

    // -- layout ------------------------------------------------------------

    private void SkipNewlines()
    {
        while (Current == TokenKind.Newline)
            _position++;
    }

    /// <summary>
    /// Skips the <c>;</c> and newline run that may sit between a body and its continuation keyword.
    /// </summary>
    /// <remarks>
    /// <c>dm.exe</c> tolerates any run of semicolons and blank lines between an if-body and its
    /// <c>else</c>, a do-body and its <c>while</c>, and a try-body and its <c>catch</c> —
    /// <c>if(a) { b; }; else { c; };</c> is the idiom a <c>\</c>-continued macro body forces, since
    /// it has no lines to separate statements with. Compiler-verified on 516.1666 across
    /// <c>};;</c>, <c>};</c> before a line break, and a bare <c>;</c> line between indented blocks.
    /// Callers save their position first: eating this run is only right when the keyword follows.
    /// </remarks>
    private void SkipContinuationSeparators()
    {
        while (Current is TokenKind.Newline or TokenKind.Semicolon)
            _position++;
    }

    /// <summary>
    /// Skips blank lines and directive lines while looking for the token that opens a block.
    /// </summary>
    /// <remarks>
    /// A directive emits no Indent, so one between a header and its body would otherwise hide the
    /// Indent the next code line does emit, and the body would be lost. Consuming it here also keeps
    /// the <c>#pragma</c> state current, which is what makes the mode position-dependent.
    /// </remarks>
    private void SkipNewlinesAndDirectives()
    {
        while (true)
        {
            if (Current == TokenKind.Newline)
                _position++;
            else if (Current == TokenKind.Hash)
                ConsumeDirective();
            else
                return;
        }
    }

    /// <summary>
    /// Consumes a directive line and applies it if it is a <c>#pragma</c> that changes the grammar.
    /// </summary>
    /// <remarks>
    /// Directives carry no indentation of their own, so one inside a body is simply skipped rather
    /// than treated as a line of the block — compiler-verified, PLAN.md §8.
    /// </remarks>
    private void ConsumeDirective()
    {
        int start = _position;
        _position++;

        List<string> words = new();

        while (!AtEnd && Current is not (TokenKind.Newline or TokenKind.Dedent))
        {
            words.Add(TextOf(_position));
            _position++;
        }

        if (Current == TokenKind.Newline)
            _position++;

        _modes.Apply(words);

        if (_modes.PopWithoutPush)
            Report(SpanFrom(start), "'#pragma pop' without a matching '#pragma push'");
    }

    // -- blocks ------------------------------------------------------------

    /// <summary>Parses a body in any of its three shapes: inline, brace block, or indented block.</summary>
    /// <param name="closer">
    /// The keyword that continues the enclosing statement — <c>else</c> for an if-body, <c>while</c>
    /// for a do-body, <c>catch</c> for a try-body. An inline body must hand it back rather than
    /// parse it: in <c>do r += 1; while(r &lt; a)</c> the <c>while</c> closes the <c>do</c>, and
    /// without the closer it would be read as a fresh loop over whatever follows.
    /// </param>
    private BlockStatementSyntax? ParseBody(TokenKind? closer = null)
    {
        int start = _position;

        // A body written on the header's own line, as in `Multiply(m) return matrix(src, m)`.
        // A directive is not an inline body, so it is excluded here and handled below.
        if (Current is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent
            or TokenKind.EndOfFile or TokenKind.OpenBrace or TokenKind.Hash))
        {
            List<StatementSyntax> inline = ParseInlineStatements(closer);

            if (Current == TokenKind.Newline)
                _position++;

            BlockStatementSyntax inlineBlock = new(inline, SpanFrom(start));

            // An inline body can still be followed by an indented block, as `if(x) return` never is
            // but a type header with members is.
            return inlineBlock;
        }

        if (Current == TokenKind.Newline)
            _position++;

        SkipNewlinesAndDirectives();

        if (Current == TokenKind.OpenBrace)
            return ParseBraceBlock();

        if (Current != TokenKind.Indent)
            return null;

        _position++;
        List<StatementSyntax> statements = ParseStatementsUntilDedent();

        if (Current == TokenKind.Dedent)
            _position++;

        return new BlockStatementSyntax(statements, SpanFrom(start));
    }

    private BlockStatementSyntax ParseBraceBlock()
    {
        int start = _position;
        _position++;

        List<StatementSyntax> statements = new();

        while (!AtEnd && Current != TokenKind.CloseBrace)
        {
            if (Current is TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent or TokenKind.Semicolon)
            {
                _position++;
                continue;
            }

            if (Current == TokenKind.Hash)
            {
                ConsumeDirective();
                continue;
            }

            int before = _position;
            statements.Add(ParseStatement());

            if (_position == before)
                _position++;
        }

        if (Current == TokenKind.CloseBrace)
            _position++;
        else
            Report(CurrentSpan, "expected '}'");

        return new BlockStatementSyntax(statements, SpanFrom(start));
    }

    private List<StatementSyntax> ParseStatementsUntilDedent()
    {
        List<StatementSyntax> statements = new();

        while (true)
        {
            SkipNewlines();

            if (AtEnd || Current == TokenKind.Dedent)
                break;

            if (Current == TokenKind.Hash)
            {
                ConsumeDirective();
                continue;
            }

            if (Current == TokenKind.Semicolon)
            {
                _position++;
                continue;
            }

            // A stray Indent means the previous line opened a block we did not consume. Take it as a
            // nested block rather than losing everything under it.
            if (Current == TokenKind.Indent)
            {
                _position++;
                statements.AddRange(ParseStatementsUntilDedent());

                if (Current == TokenKind.Dedent)
                    _position++;

                continue;
            }

            int before = _position;
            statements.AddRange(ParseInlineStatements());

            if (Current == TokenKind.Newline)
                _position++;

            if (_position == before)
                _position++;
        }

        return statements;
    }

    /// <summary>Parses the statements on one line, which <c>;</c> may separate.</summary>
    private List<StatementSyntax> ParseInlineStatements(TokenKind? closer = null)
    {
        List<StatementSyntax> statements = new();

        // Whether a `;` (or the header itself) separates the previous statement from here. The
        // closer binds only across a separator: `if(a) r = 1; else r = 2` compiles and
        // `if(a) r = 1 else r = 2` is dm.exe's "expected end of statement" — falling through to
        // the statement parser reports on the same line, which is where the compiler reports.
        bool separated = true;

        while (!AtEnd && Current is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent))
        {
            if (Current == TokenKind.Semicolon)
            {
                _position++;
                separated = true;
                continue;
            }

            if (Current == TokenKind.CloseBrace)
                break;

            if (closer is not null && Current == closer && separated)
                break;

            int before = _position;
            statements.Add(ParseStatement());
            separated = false;

            if (_position == before)
            {
                _position++;
                break;
            }
        }

        return statements;
    }

    // -- statements --------------------------------------------------------

    private StatementSyntax ParseStatement()
    {
        int start = _position;

        switch (Current)
        {
            case TokenKind.KeywordVar:
                return ParseLocalVar();

            case TokenKind.KeywordIf:
                return ParseIf();

            case TokenKind.KeywordFor:
                return ParseFor();

            case TokenKind.KeywordWhile:
                return ParseWhile();

            case TokenKind.KeywordDo:
                return ParseDoWhile();

            case TokenKind.KeywordSwitch:
                return ParseSwitch();

            case TokenKind.KeywordSpawn:
                return ParseSpawn();

            case TokenKind.KeywordTry:
                return ParseTry();

            case TokenKind.KeywordSet:
                return ParseSet();

            case TokenKind.KeywordReturn:
            {
                _position++;
                ExpressionSyntax? value = IsStatementEnd() ? null : ParseExpression();
                return new ReturnStatementSyntax(value, SpanFrom(start));
            }

            case TokenKind.KeywordBreak:
            case TokenKind.KeywordContinue:
            {
                bool isContinue = Current == TokenKind.KeywordContinue;
                _position++;

                // Both take an optional loop label.
                string? label = null;
                if (IsNameLike(Current))
                {
                    label = CurrentText;
                    _position++;
                }

                return new BreakStatementSyntax(isContinue, label, SpanFrom(start));
            }

            case TokenKind.KeywordGoto:
            {
                _position++;
                string? label = null;

                if (IsNameLike(Current))
                {
                    label = CurrentText;
                    _position++;
                }

                return new GotoStatementSyntax(label, SpanFrom(start));
            }

            // `del x` and `throw x` take a bare operand with no parentheses.
            case TokenKind.KeywordDel:
            case TokenKind.KeywordThrow:
            {
                TokenKind keyword = Current;
                _position++;
                ExpressionSyntax? operand = IsStatementEnd() ? null : ParseExpression();
                return new UnaryStatementSyntax(keyword, operand, SpanFrom(start));
            }

            case TokenKind.OpenBrace:
                return ParseBraceBlock();

            default:
            {
                // A loop label is `name:` alone on its line — or immediately followed by a brace
                // block, which is how a label reaches macro-generated code: a `\`-continued body
                // has no lines to put it on. /tg/station's SEARCH_ADJ_IN_DIR writes
                // `set_adj_in_dir: { ... }` and breaks out of it by name.
                //
                // A `:` followed by `{` is unambiguous. Member access needs a name after the colon,
                // so there is no reading of `x: {` where the colon is an operator — which is what
                // we were doing, reporting "expected a member name" on the brace and then failing
                // to find an expression for every line of the block. 973 of them on /tg/station.
                if (IsNameLike(Current)
                    && Peek() == TokenKind.Colon
                    && (IsLineEnd(_position + 2) || Peek(2) == TokenKind.OpenBrace))
                {
                    string name = CurrentText;
                    _position += 2;
                    StatementSyntax? body = ParseBody();
                    return new LabelStatementSyntax(name, body, SpanFrom(start));
                }

                ExpressionSyntax expression = ParseExpression();
                return new ExpressionStatementSyntax(expression, SpanFrom(start));
            }
        }
    }

    private bool IsStatementEnd() => AtEnd || Current is TokenKind.Newline or TokenKind.Indent
        or TokenKind.Dedent or TokenKind.Semicolon or TokenKind.CloseBrace or TokenKind.CloseParen;

    private bool IsLineEnd(int index)
    {
        TokenKind kind = index < _tokens.Count ? _tokens[index].Kind : TokenKind.EndOfFile;
        return kind is TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent or TokenKind.EndOfFile;
    }

    /// <summary>Consumes a parenthesised expression, as every control-flow header has.</summary>
    private ExpressionSyntax ParseParenthesised()
    {
        if (Current != TokenKind.OpenParen)
        {
            Report(CurrentSpan, "expected '('");
            return new ErrorExpressionSyntax(CurrentSpan);
        }

        _position++;
        ExpressionSyntax expression = ParseExpression();

        if (Current == TokenKind.CloseParen)
            _position++;
        else
            Report(CurrentSpan, "expected ')'");

        return expression;
    }

    private StatementSyntax ParseIf()
    {
        int start = _position;
        _position++;

        ExpressionSyntax condition = ParseParenthesised();
        StatementSyntax? then = ParseBody(TokenKind.KeywordElse);

        StatementSyntax? otherwise = null;
        int save = _position;
        SkipContinuationSeparators();

        if (Current == TokenKind.KeywordElse)
        {
            _position++;

            // `else if` continues the chain on the same line.
            otherwise = Current == TokenKind.KeywordIf ? ParseIf() : ParseBody();
        }
        else
        {
            _position = save;
        }

        return new IfStatementSyntax(condition, then, otherwise, SpanFrom(start));
    }

    private StatementSyntax ParseWhile()
    {
        int start = _position;
        _position++;

        ExpressionSyntax condition = ParseParenthesised();
        StatementSyntax? body = ParseBody();

        return new WhileStatementSyntax(condition, body, SpanFrom(start));
    }

    private StatementSyntax ParseDoWhile()
    {
        int start = _position;
        _position++;

        StatementSyntax? body = ParseBody(TokenKind.KeywordWhile);

        int save = _position;
        SkipContinuationSeparators();

        ExpressionSyntax? condition = null;
        if (Current == TokenKind.KeywordWhile)
        {
            _position++;
            condition = ParseParenthesised();
        }
        else
        {
            _position = save;
            Report(CurrentSpan, "expected 'while' to close a 'do' loop");
        }

        return new DoWhileStatementSyntax(body, condition, SpanFrom(start));
    }

    private StatementSyntax ParseSpawn()
    {
        int start = _position;
        _position++;

        // `spawn()` with empty parentheses is common and means no delay.
        ExpressionSyntax? delay = null;

        if (Current == TokenKind.OpenParen && Peek() == TokenKind.CloseParen)
            _position += 2;
        else if (Current == TokenKind.OpenParen)
            delay = ParseParenthesised();

        StatementSyntax? body = ParseBody();

        return new SpawnStatementSyntax(delay, body, SpanFrom(start));
    }

    private StatementSyntax ParseTry()
    {
        int start = _position;
        _position++;

        StatementSyntax? body = ParseBody(TokenKind.KeywordCatch);

        int save = _position;
        SkipContinuationSeparators();

        LocalVarStatementSyntax? exception = null;
        StatementSyntax? catchBody = null;

        if (Current == TokenKind.KeywordCatch)
        {
            _position++;

            if (Current == TokenKind.OpenParen)
            {
                _position++;

                if (Current == TokenKind.KeywordVar)
                    exception = ParseLocalVar() as LocalVarStatementSyntax;

                while (!AtEnd && Current != TokenKind.CloseParen)
                    _position++;

                if (Current == TokenKind.CloseParen)
                    _position++;
            }

            catchBody = ParseBody();
        }
        else
        {
            _position = save;
        }

        return new TryStatementSyntax(body, exception, catchBody, SpanFrom(start));
    }

    /// <summary><c>set name = value</c>, or the <c>set src in view()</c> form.</summary>
    private StatementSyntax ParseSet()
    {
        int start = _position;
        _position++;

        // `set` alone on a line heads an indented block of settings.
        if (IsLineEnd(_position))
        {
            StatementSyntax? block = ParseBody();
            return block ?? new BlockStatementSyntax(Array.Empty<StatementSyntax>(), SpanFrom(start));
        }

        string name = IsNameLike(Current) ? CurrentText : string.Empty;

        if (IsNameLike(Current))
            _position++;
        else
            Report(CurrentSpan, "expected a setting name after 'set'");

        ExpressionSyntax? value = null;

        if (Current == TokenKind.Assign || Current == TokenKind.KeywordIn)
        {
            _position++;
            value = ParseExpression();
        }

        return new SetStatementSyntax(name, value, SpanFrom(start));
    }

    // -- local variables ---------------------------------------------------

    private StatementSyntax ParseLocalVar()
    {
        int start = _position;
        _position++;

        // A `var` line carrying nothing but modifiers heads an indented block, and every child
        // inherits them:  var/tmp
        //                     mystats = new/list(10)
        //                     myabilities[]
        int probe = _position;
        List<string> blockModifiers = new();

        while (probe + 1 < _tokens.Count
               && _tokens[probe].Kind is TokenKind.Slash or TokenKind.Dot
               && IsNameLike(_tokens[probe + 1].Kind)
               && VarModifiers.Contains(TextOf(probe + 1)))
        {
            blockModifiers.Add(TextOf(probe + 1));
            probe += 2;
        }

        if (IsLineEnd(probe))
        {
            _position = probe;
            return ParseVarBlock(start, blockModifiers);
        }

        // The brace-group form, `var{html = X; extra = Y}`.
        if (_tokens[probe].Kind == TokenKind.OpenBrace)
        {
            _position = probe;
            return ParseBraceBlock();
        }

        if (Current is TokenKind.Slash or TokenKind.Dot)
            _position++;

        return ParseLocalVarNames(start);
    }

    /// <summary>Parses the indented children of a bare <c>var</c> block, each one a declaration.</summary>
    private StatementSyntax ParseVarBlock(int start, List<string> modifiers)
    {
        if (Current == TokenKind.Newline)
            _position++;

        SkipNewlinesAndDirectives();

        if (Current != TokenKind.Indent)
            return new BlockStatementSyntax(Array.Empty<StatementSyntax>(), SpanFrom(start));

        _position++;
        List<StatementSyntax> children = ParseVarBlockChildren(modifiers);

        if (Current == TokenKind.Dedent)
            _position++;

        return new BlockStatementSyntax(children, SpanFrom(start));
    }

    private StatementSyntax ParseLocalVarNames(
        int start, List<string>? inherited = null, bool allowSiblings = true, bool inForHeader = false)
    {
        List<string> modifiers = inherited is null ? new() : new(inherited);
        List<string> segments = new();
        List<TextSpan> spans = new();

        // `!` is a legal type-name segment (warklan's /obj/! quest marker) and lexes as the Not
        // operator, so it counts only AFTER a separator — `var/datum/parsing/!/B` names a type,
        // while a leading `!` is the unary operator it is everywhere else.
        while (IsDeclarationName(Current) || IsKeywordTypeSegment(_position)
            || (Current == TokenKind.Not && segments.Count > 0))
        {
            string word = CurrentText;

            // A modifier word is a modifier only when a separator follows it: `var/final/x`
            // declares `x` with 516's final modifier, while `var/final = ""` declares a var NAMED
            // final — /tg/station writes both. All five modifier words are legal names,
            // compiler-verified with uses, at proc level and type level alike.
            if (VarModifiers.Contains(word) && segments.Count == 0
                && Peek() is TokenKind.Slash or TokenKind.Dot)
            {
                modifiers.Add(word);
                _position++;
            }
            else
            {
                segments.Add(word);
                spans.Add(CurrentSpan);
                _position++;
            }

            if (Current is TokenKind.Slash or TokenKind.Dot
                && (IsDeclarationName(Peek()) || IsKeywordTypeSegment(_position + 1)
                    || (Peek() == TokenKind.Not && segments.Count > 0)))
            {
                _position++;
                continue;
            }

            break;
        }

        if (segments.Count == 0)
        {
            Report(CurrentSpan, "expected a variable name");
            return new ExpressionStatementSyntax(new ErrorExpressionSyntax(CurrentSpan), SpanFrom(start));
        }

        // The last segment is the name; anything before it is the declared type.
        string name = segments[^1];
        TextSpan nameSpan = spans[^1];

        PathSyntax? declaredType = segments.Count == 1
            ? null
            : new PathSyntax(
                PathAnchor.Absolute,
                segments.GetRange(0, segments.Count - 1),
                TextSpan.FromBounds(spans[0].Start, spans[^2].End),
                spans.GetRange(0, spans.Count - 1));

        // `var/L[]` and `var/M[10]` are declaration brackets, not indexing. A size is an ordinary
        // expression and often reads a variable — `var/list/tier_list[max_tier]` — so it is kept
        // rather than skipped; discarding it hid those reads from every consumer of the AST.
        List<ExpressionSyntax>? dimensions = null;

        while (Current == TokenKind.OpenBracket)
        {
            _position++;

            if (Current != TokenKind.CloseBracket)
            {
                ExpressionSyntax size = ParseExpression();
                (dimensions ??= new List<ExpressionSyntax>()).Add(size);
            }

            // An unterminated or unreadable bracket must not eat the rest of the declaration, so
            // recovery walks to the matching close rather than trusting the expression to land on
            // one. A buffer mid-keystroke reaches here constantly.
            int depth = 1;

            while (depth > 0 && !AtEnd)
            {
                if (Current == TokenKind.OpenBracket)
                    depth++;
                else if (Current == TokenKind.CloseBracket)
                    depth--;

                _position++;
            }
        }

        ExpressionSyntax? initializer = null;

        if (Current == TokenKind.Assign)
        {
            _position++;
            int initializerStart = _position;
            initializer = ParseExpression();

            // A LOCAL var's initializer cannot end in a top-level relational `in` — dm.exe rejects
            // `var/r = y in L` with "unexpected 'in' expression" whatever the left side is, bare,
            // parenthesized or a ternary, while accepting the same text as a statement, a global,
            // or a type-level var. Parenthesizing the whole test (`var/r = (y in L)`) and the
            // `locate(X) in L` unit are both accepted, so only the relational form at the top of
            // the tree is the error. Compiler-verified against 516.1666; a `for` header owns its
            // `in` and is exempt.
            //
            // The AST alone cannot make the parenthesised distinction — parentheses leave no node —
            // so the token scan settles it: `(y in L)` holds its `in` at bracket depth 1 and is
            // fine, `(y) in L` at depth 0 and is the error.
            //
            // `input(...) in choices` is exempt alongside locate: it is the reference's documented
            // choice-restricting idiom, and mlaas writes it as a local initializer eight times in
            // a project dm.exe compiles clean — the diagdiff gate caught the first version of this
            // check inventing on every one. The `as` clause peels because the idiom's full form is
            // `input(...) as null|anything in choices`, which mlaas also writes.
            if (!inForHeader
                && initializer is BinaryExpressionSyntax { OperatorToken: TokenKind.KeywordIn } relational
                && !IsChoiceIdiom(relational.Left)
                && HasTopLevelIn(initializerStart, _position))
            {
                // A literal `list(...)` on the right is the one RHS dm.exe accepts — the
                // declaration's value-restriction clause, the same grammar as a verb argument's
                // `as num in list(...)`. It is NOT the membership operator: runtime-verified,
                // `var/r = 2 in list(4,5)` leaves r holding 2, the left value, member or not. A
                // local written this way almost always meant the test, so it earns the DM03xx
                // treatment: match the compiler, then warn. tgstation ships exactly one.
                if (relational.Right is InvocationExpressionSyntax
                    {
                        Target: IdentifierExpressionSyntax { Name: "list" },
                    })
                {
                    _diagnostics.Add(Diagnostic.Warning(
                        "DM0301",
                        relational.Right.Span,
                        "this `in list(...)` is a value restriction, not a membership test — the "
                        + "var takes the left value; write `(x in list(...))` to test membership"));
                }
                else
                {
                    Report(relational.Right.Span, "unexpected 'in' expression");
                }
            }
        }

        // `as` constrains a declaration too, as in `var/t as text`.
        if (Current == TokenKind.KeywordAs)
        {
            _position++;

            while (IsNameLike(Current) || Current == TokenKind.KeywordNull)
            {
                _position++;

                if (Current != TokenKind.Pipe)
                    break;

                _position++;
            }
        }

        List<LocalVarStatementSyntax> siblings = new();

        while (allowSiblings && Current == TokenKind.Comma && IsNameLike(Peek()))
        {
            _position++;
            int siblingStart = _position;

            if (ParseLocalVarNames(siblingStart, inherited, inForHeader: inForHeader)
                is LocalVarStatementSyntax sibling)
            {
                siblings.Add(sibling);
            }
            else
            {
                break;
            }
        }

        return new LocalVarStatementSyntax(
            name, nameSpan, declaredType, modifiers, initializer, siblings, SpanFrom(start), dimensions);
    }

    /// <summary>
    /// True for the two call forms whose <c>in</c> is a grammatical unit rather than the
    /// relational operator: <c>locate(X) in container</c> and
    /// <c>input(...) [as types] in choices</c>.
    /// </summary>
    private static bool IsChoiceIdiom(ExpressionSyntax left)
    {
        while (left is AsExpressionSyntax asClause)
            left = asClause.Expression;

        return left is InvocationExpressionSyntax
        {
            Target: IdentifierExpressionSyntax { Name: "locate" or "input" },
        };
    }

    /// <summary>True when a <c>KeywordIn</c> sits at bracket depth zero in the token range.</summary>
    private bool HasTopLevelIn(int start, int end)
    {
        int depth = 0;

        for (int i = start; i < end && i < _tokens.Count; i++)
        {
            switch (_tokens[i].Kind)
            {
                case TokenKind.OpenParen:
                case TokenKind.OpenBracket:
                case TokenKind.QuestionOpenBracket:
                case TokenKind.OpenBrace:
                    depth++;
                    break;

                case TokenKind.CloseParen:
                case TokenKind.CloseBracket:
                case TokenKind.CloseBrace:
                    depth--;
                    break;

                case TokenKind.KeywordIn when depth == 0:
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the names in a <c>var</c> block, including nested groups.
    /// </summary>
    /// <remarks>
    /// A child can head its own indented group, where it acts as a type prefix for the names beneath
    /// it — <c>var</c> / <c>obj/small/egg</c> / <c>E</c> declares <c>E</c> of that type. Consuming
    /// the Indent without recursing leaves the block structure unbalanced, which costs every
    /// declaration after the enclosing proc rather than just this one.
    /// </remarks>
    private List<StatementSyntax> ParseVarBlockChildren(List<string> modifiers)
    {
        List<StatementSyntax> children = new();

        while (true)
        {
            SkipNewlines();

            if (AtEnd || Current == TokenKind.Dedent)
                break;

            if (Current == TokenKind.Hash)
            {
                ConsumeDirective();
                continue;
            }

            // `mask_chance; see_chance` declares two names on one line.
            if (Current == TokenKind.Semicolon)
            {
                _position++;
                continue;
            }

            if (Current == TokenKind.Indent)
            {
                _position++;
                children.AddRange(ParseVarBlockChildren(modifiers));

                if (Current == TokenKind.Dedent)
                    _position++;

                continue;
            }

            int before = _position;
            children.Add(ParseLocalVarNames(_position, modifiers));

            if (Current == TokenKind.Newline)
                _position++;

            if (_position == before)
                _position++;
        }

        return children;
    }

    // -- for ---------------------------------------------------------------

    private StatementSyntax ParseFor()
    {
        int start = _position;
        _position++;

        if (Current != TokenKind.OpenParen)
        {
            Report(CurrentSpan, "expected '(' after 'for'");
            return new ExpressionStatementSyntax(new ErrorExpressionSyntax(CurrentSpan), SpanFrom(start));
        }

        _position++;

        List<StatementSyntax> initializers = new();
        List<StatementSyntax> increments = new();
        ExpressionSyntax? condition = null;
        ExpressionSyntax? sequence = null;
        ExpressionSyntax? rangeEnd = null;
        ExpressionSyntax? step = null;
        ForKind kind = ForKind.Bare;

        if (Current != TokenKind.CloseParen)
        {
            initializers.Add(ParseForClause());

            switch (Current)
            {
                // `for(var/x in L)`, and the 516 `for(var/k, v in assoc)` form.
                case TokenKind.KeywordIn:
                    kind = ForKind.In;
                    _position++;
                    sequence = ParseExpression();

                    // `for(var/j in 1 to L.len)` combines the two forms.
                    if (Current == TokenKind.KeywordTo)
                    {
                        _position++;
                        rangeEnd = ParseExpression();

                        if (Current == TokenKind.KeywordStep)
                        {
                            _position++;
                            step = ParseExpression();
                        }
                    }

                    break;

                // `for(var/i = 1 to 10 step 2)`.
                case TokenKind.KeywordTo:
                    kind = ForKind.Range;
                    _position++;
                    rangeEnd = ParseExpression();

                    if (Current == TokenKind.KeywordStep)
                    {
                        _position++;
                        step = ParseExpression();
                    }

                    break;

                case TokenKind.Comma:
                case TokenKind.Semicolon:
                    kind = ForKind.Clauses;
                    ParseForClauses(initializers, ref condition, increments);
                    break;
            }
        }

        if (Current == TokenKind.CloseParen)
            _position++;
        else
            Report(CurrentSpan, "expected ')'");

        StatementSyntax? body = ParseBody();

        return new ForStatementSyntax(
            kind, initializers, condition, increments, sequence, rangeEnd, step, body, SpanFrom(start));
    }

    /// <summary>
    /// Reads the clause list of a C-shaped <c>for</c>, honouring the current comma mode.
    /// </summary>
    /// <remarks>
    /// By default a comma separates clauses, so <c>for(i=0, i&lt;3, i++)</c> has three of them.
    /// Under <c>#pragma syntax C for</c> the comma instead chains statements inside one clause and
    /// only <c>;</c> separates, so <c>for(i=0, j=0; i&lt;3; i++, j+=1)</c> has two initialisers.
    /// Semicolons work in both modes.
    /// </remarks>
    private void ParseForClauses(
        List<StatementSyntax> initializers,
        ref ExpressionSyntax? condition,
        List<StatementSyntax> increments)
    {
        int clause = 0;

        while (!AtEnd && Current != TokenKind.CloseParen)
        {
            bool chaining = Current == TokenKind.Comma && _modes.CFor;
            bool separating = Current == TokenKind.Semicolon || (Current == TokenKind.Comma && !_modes.CFor);

            if (!chaining && !separating)
                break;

            _position++;

            if (separating)
                clause++;

            if (Current is TokenKind.CloseParen or TokenKind.Comma or TokenKind.Semicolon)
                continue;

            switch (clause)
            {
                case 0:
                    initializers.Add(ParseForClause());
                    break;

                case 1:
                    condition = ParseExpression();
                    break;

                default:
                    increments.Add(ParseForClause());
                    break;
            }
        }
    }

    private StatementSyntax ParseForClause()
    {
        int start = _position;

        if (Current == TokenKind.KeywordVar)
        {
            _position++;

            // The space-separated form is legal as a statement but rejected in a for header, so only
            // `/` and `.` are accepted here — PLAN.md §4a.
            if (Current is TokenKind.Slash or TokenKind.Dot)
                _position++;

            // In a header a comma separates clauses, so `for(var/i = 1, i < n, i++)` must not read
            // `i < n` as a second declaration. The one exception is 516's `for(var/k, v in assoc)`.
            return ParseLocalVarNames(start, null, IsAssociativeForHeader(), inForHeader: true);
        }

        // The header's `in` belongs to the loop, not to this clause. `for(x in L)` with an already
        // declared x would otherwise parse as the single expression `x in L`, leaving the loop
        // modelled as a bare `for` over a nonsense initializer - which parsed silently, so nothing
        // caught it until `for(x in a to b step c)` turned it into a visible error.
        return new ExpressionStatementSyntax(ParseExpression(stopAtIn: true), SpanFrom(start));
    }

    /// <summary>
    /// True for 516's <c>for(var/key, value in assoc)</c>, where the comma introduces a second name
    /// rather than the next clause.
    /// </summary>
    private bool IsAssociativeForHeader()
    {
        int depth = 0;

        for (int i = _position; i < _tokens.Count; i++)
        {
            switch (_tokens[i].Kind)
            {
                case TokenKind.OpenParen or TokenKind.OpenBracket:
                    depth++;
                    break;

                case TokenKind.CloseBracket:
                    depth--;
                    break;

                case TokenKind.CloseParen when depth > 0:
                    depth--;
                    break;

                case TokenKind.Comma when depth == 0:
                    return i + 2 < _tokens.Count
                           && IsNameLike(_tokens[i + 1].Kind)
                           && _tokens[i + 2].Kind == TokenKind.KeywordIn;

                case TokenKind.CloseParen:
                case TokenKind.Semicolon when depth == 0:
                case TokenKind.Newline:
                case TokenKind.EndOfFile:
                    return false;
            }
        }

        return false;
    }

    // -- switch ------------------------------------------------------------

    private StatementSyntax ParseSwitch()
    {
        int start = _position;
        _position++;

        // The grammar is decided where the `switch` appears. A `#pragma pop` further down the body
        // must not retroactively change how this statement was read.
        bool cStyle = _modes.CSwitch;

        ExpressionSyntax value = ParseParenthesised();
        List<SwitchCaseSyntax> cases = new();

        if (Current == TokenKind.Newline)
            _position++;

        SkipNewlinesAndDirectives();

        // A DM-style switch with no arms is dm.exe's "empty switch statement" warning plus an
        // "expected if or else" error, both on the switch's own line — even when nothing follows
        // the header at all.
        TextSpan headerSpan = _tokens[start].Span;

        // The arm list may be a brace block instead of an indented one — on the header line or
        // after it — which is what a `\`-continued macro body has to write, having no lines to
        // indent: `switch(pH) { if(7 to 10) { ... } if(2 to 7) { ... } }`. Compiler- and
        // runtime-verified, including an `else` arm and indented arms inside the braces.
        if (Current == TokenKind.OpenBrace)
        {
            _position++;

            while (!AtEnd && Current != TokenKind.CloseBrace)
            {
                if (Current is TokenKind.Newline or TokenKind.Indent
                    or TokenKind.Dedent or TokenKind.Semicolon)
                {
                    _position++;
                    continue;
                }

                if (Current == TokenKind.Hash)
                {
                    ConsumeDirective();
                    continue;
                }

                int before = _position;
                SwitchCaseSyntax? arm = cStyle ? ParseCStyleCase() : ParseDmCase();

                if (arm is not null)
                    cases.Add(arm);

                if (_position == before)
                    _position++;
            }

            if (Current == TokenKind.CloseBrace)
                _position++;
            else
                Report(CurrentSpan, "expected '}'");

            WarnIfEmpty(cases, cStyle, headerSpan);
            return new SwitchStatementSyntax(value, cases, cStyle, SpanFrom(start));
        }

        if (Current != TokenKind.Indent)
        {
            // Nothing follows the header at all. dm.exe reports the pair here too.
            if (!cStyle)
                Report(headerSpan, "expected 'if' or 'else' in a switch");

            WarnIfEmpty(cases, cStyle, headerSpan);
            return new SwitchStatementSyntax(value, cases, cStyle, SpanFrom(start));
        }

        _position++;

        while (true)
        {
            SkipNewlines();

            if (AtEnd || Current == TokenKind.Dedent)
                break;

            if (Current == TokenKind.Hash)
            {
                ConsumeDirective();
                continue;
            }

            int before = _position;
            SwitchCaseSyntax? arm = cStyle ? ParseCStyleCase() : ParseDmCase();

            if (arm is not null)
                cases.Add(arm);

            if (_position == before)
                _position++;
        }

        if (Current == TokenKind.Dedent)
            _position++;

        WarnIfEmpty(cases, cStyle, headerSpan);
        return new SwitchStatementSyntax(value, cases, cStyle, SpanFrom(start));
    }

    /// <summary>
    /// A DM-style switch that ends with no arms is dm.exe's "empty switch statement", a WARNING on
    /// the switch's own line beside whatever error the non-arm content already drew. Probed from
    /// the mined corpus: it fires with no body, with a statement for a body, and with a body that
    /// opens but holds no `if`/`else`.
    /// </summary>
    private void WarnIfEmpty(List<SwitchCaseSyntax> cases, bool cStyle, TextSpan headerSpan)
    {
        if (!cStyle && cases.Count == 0)
            _diagnostics.Add(Diagnostic.Warning("DM0203", headerSpan, "empty switch statement"));
    }

    /// <summary>DM's own arms: <c>if(1)</c>, <c>if(2,3)</c>, <c>if(a to b)</c> and <c>else</c>.</summary>
    private SwitchCaseSyntax? ParseDmCase()
    {
        int start = _position;

        if (Current == TokenKind.KeywordElse)
        {
            _position++;
            StatementSyntax? elseBody = ParseBody();

            return new SwitchCaseSyntax(
                Array.Empty<ExpressionSyntax>(), Array.Empty<ExpressionSyntax>(), true, elseBody, SpanFrom(start));
        }

        if (Current != TokenKind.KeywordIf)
        {
            Report(CurrentSpan, "expected 'if' or 'else' in a switch");
            return null;
        }

        _position++;

        List<ExpressionSyntax> values = new();
        List<ExpressionSyntax> rangeEnds = new();

        if (Current == TokenKind.OpenParen)
        {
            _position++;

            while (!AtEnd && Current != TokenKind.CloseParen)
            {
                if (Current == TokenKind.Comma)
                {
                    _position++;
                    continue;
                }

                int before = _position;
                values.Add(ParseExpression());

                // `if(1 to 5)` matches a range rather than a value.
                if (Current == TokenKind.KeywordTo)
                {
                    _position++;
                    rangeEnds.Add(ParseExpression());
                }
                else
                {
                    rangeEnds.Add(null!);
                }

                if (_position == before)
                    _position++;
            }

            if (Current == TokenKind.CloseParen)
                _position++;
        }

        StatementSyntax? body = ParseBody();
        return new SwitchCaseSyntax(values, rangeEnds, false, body, SpanFrom(start));
    }

    /// <summary>
    /// The <c>#pragma syntax C switch</c> arms, <c>case v:</c> and <c>default:</c>.
    /// </summary>
    /// <remarks>
    /// <c>case</c> and <c>default</c> are ordinary identifiers to the lexer. Without the pragma
    /// <c>case 1:</c> fails with "expected var or proc name after : operator", because the compiler
    /// reads <c>case</c> as a name and <c>:</c> as member access.
    /// </remarks>
    private SwitchCaseSyntax? ParseCStyleCase()
    {
        int start = _position;

        if (!IsNameLike(Current))
        {
            Report(CurrentSpan, "expected 'case' or 'default'");
            return null;
        }

        bool isDefault = string.Equals(CurrentText, "default", StringComparison.Ordinal);
        bool isCase = string.Equals(CurrentText, "case", StringComparison.Ordinal);

        if (!isDefault && !isCase)
        {
            Report(CurrentSpan, "expected 'case' or 'default'");
            return null;
        }

        _position++;

        List<ExpressionSyntax> values = new();
        List<ExpressionSyntax> rangeEnds = new();

        if (isCase)
        {
            // The label's `:` terminates it rather than starting a member access.
            values.Add(ParseExpression(colonTerminates: true));

            if (Current == TokenKind.KeywordTo)
            {
                _position++;
                rangeEnds.Add(ParseExpression(colonTerminates: true));
            }
            else
            {
                rangeEnds.Add(null!);
            }
        }

        if (Current == TokenKind.Colon)
            _position++;
        else
            Report(CurrentSpan, "expected ':'");

        // Fall-through is real here, so an arm's body runs on to the next case unless `break` stops
        // it. The statements still belong to this arm.
        StatementSyntax? body = ParseBody();
        return new SwitchCaseSyntax(values, rangeEnds, isDefault, body, SpanFrom(start));
    }
}
