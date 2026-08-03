using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>Output of <see cref="DeclarationParser"/>.</summary>
public sealed class ParseResult
{
    internal ParseResult(SourceText text, FileSyntax root, IReadOnlyList<Diagnostic> diagnostics)
    {
        Text = text;
        Root = root;
        Diagnostics = diagnostics;
    }

    public SourceText Text { get; }

    public FileSyntax Root { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }
}

/// <summary>What kind of member an indented block is expected to contain.</summary>
internal enum BlockContext
{
    /// <summary>Types, vars and procs, decided per line.</summary>
    Any,

    /// <summary>Beneath a bare <c>var</c>, so every child is a variable.</summary>
    Var,

    /// <summary>Beneath a bare <c>proc</c> or <c>verb</c>, so every child is a proc.</summary>
    Proc,
}

/// <summary>
/// Parses the declaration structure of one file: types, vars and proc signatures.
/// </summary>
/// <remarks>
/// <para>
/// Proc <b>bodies</b> are skipped rather than parsed. Everything needed for a file outline and for
/// the object tree lives in declarations, and statement parsing is a much larger job that can be
/// added without changing anything here.
/// </para>
/// <para>
/// Structure comes from the lexer's Indent and Dedent tokens, so no tab width is assumed. Three DM
/// shapes drive most of the complexity:
/// </para>
/// <list type="bullet">
/// <item><description><c>var</c> and <c>proc</c> are ordinary path segments, so they can appear
/// anywhere in a path — <c>mob/var/hp</c>, <c>mob/proc/attack()</c> — and are not keywords in the
/// grammatical sense.</description></item>
/// <item><description>Either can also head a bare indented block, where every child inherits that
/// kind.</description></item>
/// <item><description>One <c>var/</c> can declare several names separated by commas.</description></item>
/// </list>
/// <para>
/// Recovery is line-oriented: on an unexpected token the rest of the logical line is discarded and
/// parsing resumes at the next one. An editor buffer is malformed on every keystroke, so bailing is
/// not an option.
/// </para>
/// </remarks>
public sealed class DeclarationParser
{
    private static readonly HashSet<string> Modifiers =
        new(StringComparer.Ordinal) { "const", "tmp", "global", "static", "final" };

    private readonly LexResult _lex;
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly List<Token> _tokens = new();

    /// <summary>
    /// <c>#pragma syntax</c> state, owned here because the pragma sits at file level while the
    /// grammar it changes is used inside proc bodies.
    /// </summary>
    private readonly SyntaxModes _modes = new();

    private int _position;

    private DeclarationParser(LexResult lex)
    {
        _lex = lex;

        // Comments carry no structure. Layout tokens are kept: they are the structure.
        foreach (Token token in lex.Tokens)
        {
            if (token.Kind != TokenKind.Comment)
                _tokens.Add(token);
        }
    }

    public static ParseResult Parse(LexResult lex)
    {
        ArgumentNullException.ThrowIfNull(lex);

        DeclarationParser parser = new(lex);
        List<DeclarationSyntax> declarations = parser.ParseBlock(BlockContext.Any);

        TextSpan span = new(0, lex.Text.Length);
        return new ParseResult(lex.Text, new FileSyntax(declarations, span), parser._diagnostics);
    }

    // -- token access ------------------------------------------------------

    private bool AtEnd => _position >= _tokens.Count || Current == TokenKind.EndOfFile;

    private TokenKind Current => _position < _tokens.Count ? _tokens[_position].Kind : TokenKind.EndOfFile;

    private TokenKind Peek(int offset = 1)
        => _position + offset < _tokens.Count ? _tokens[_position + offset].Kind : TokenKind.EndOfFile;

    private TextSpan CurrentSpan
        => _position < _tokens.Count ? _tokens[_position].Span : new TextSpan(_lex.Text.Length, 0);

    private string TextOf(int index) => _lex.Text.ToString(_tokens[index].Span);

    private void SkipNewlines()
    {
        while (Current == TokenKind.Newline)
            _position++;
    }

    /// <summary>
    /// Skips blank lines and directive lines while looking for the token that opens a block.
    /// </summary>
    /// <remarks>
    /// Directives carry no indentation of their own — verified against dm.exe 516.1666, which
    /// accepts one at column 0, at the body's level, and indented past it, all in the same body.
    /// So a directive sitting between a header and its body emits no Indent, and looking only at
    /// the next token would miss the Indent that the following code line does emit.
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

    // -- blocks ------------------------------------------------------------

    private List<DeclarationSyntax> ParseBlock(BlockContext context)
    {
        List<DeclarationSyntax> declarations = new();

        while (true)
        {
            SkipNewlines();

            if (AtEnd || Current == TokenKind.Dedent)
                break;

            // Directive lines are not declarations. When parsing a preprocessed stream they are
            // already gone, but the outline runs per file on raw tokens, where they are still here.
            if (Current == TokenKind.Hash)
            {
                ConsumeDirective();
                continue;
            }

            // A `;` in declaration position is an empty declaration. dm.exe 516.1666 accepts one at
            // file scope, indented inside a type block, and doubled. Real code leaves them behind
            // when a line is commented out.
            if (Current == TokenKind.Semicolon)
            {
                _position++;
                continue;
            }

            int before = _position;

            if (ParseDeclaration(context) is { } declaration)
                declarations.Add(declaration);

            // Guarantee progress even when recovery cannot make sense of a line.
            if (_position == before)
                _position++;
        }

        return declarations;
    }

    /// <summary>Consumes an indented block and returns its declarations.</summary>
    private List<DeclarationSyntax> ParseIndentedBlock(BlockContext context)
    {
        // Blank, comment-only and directive lines still emit their newline, so the Indent that
        // opens a block is not necessarily the very next token.
        SkipNewlinesAndDirectives();

        if (Current != TokenKind.Indent)
            return new List<DeclarationSyntax>();

        _position++;
        List<DeclarationSyntax> members = ParseBlock(context);

        if (Current == TokenKind.Dedent)
            _position++;

        return members;
    }

    /// <summary>Skips an indented block without parsing it, used for proc bodies.</summary>
    private void SkipIndentedBlock()
    {
        SkipNewlinesAndDirectives();

        if (Current != TokenKind.Indent)
            return;

        int depth = 0;
        do
        {
            if (Current == TokenKind.Indent)
                depth++;
            else if (Current == TokenKind.Dedent)
                depth--;

            _position++;
        }
        while (depth > 0 && !AtEnd);
    }

    // -- declarations ------------------------------------------------------

    private DeclarationSyntax? ParseDeclaration(BlockContext context)
    {
        int start = _position;
        PathSyntax path = ParsePath();

        if (path.IsEmpty)
        {
            Report("DM0200", CurrentSpan, "expected a declaration");
            RecoverToNextLine();
            return null;
        }

        // A signature makes it a proc regardless of where `proc` sits in the path.
        if (Current == TokenKind.OpenParen)
            return ParseProc(path, context, start);

        int varIndex = IndexOfSegment(path, "var");
        bool endsWithVar = path.Segments[^1] == "var";
        bool endsWithProc = path.Segments[^1] is "proc" or "verb";

        // `var/const` and friends head a block too: the trailing segment is a modifier, not a name.
        // stddef.dm is full of these, declaring whole groups of constants at once.
        bool modifierBlock = varIndex >= 0
                             && path.Segments.Count > varIndex + 1
                             && Modifiers.Contains(path.Segments[^1]);

        // `var` or `proc` heading a bare block: every child inherits that kind.
        if (endsWithVar || endsWithProc || modifierBlock || context != BlockContext.Any)
        {
            if (endsWithVar || endsWithProc || modifierBlock)
            {
                ConsumeLineEnd();
                BlockContext childContext = endsWithProc ? BlockContext.Proc : BlockContext.Var;
                List<DeclarationSyntax> members = ParseIndentedBlock(childContext);

                return new TypeDeclarationSyntax(path, members, SpanFrom(start), isGroupHeader: true);
            }

            if (context == BlockContext.Var)
                return ParseVar(path, varIndex: -1, start);
        }

        if (varIndex >= 0)
            return ParseVar(path, varIndex, start);

        // A bare assignment at type level overrides an inherited var and needs no `var/` keyword —
        // `maxx = 3` on `world`, or stddef.dm's `_dm_interface = _DM_datum|_DM_sound`. It declares a
        // value, not a type, so modelling it as a type node would put `maxx` in the object tree.
        if (Current == TokenKind.Assign)
            return ParseVar(path, varIndex: -1, start, inVarContext: false);

        ConsumeLineEnd();
        List<DeclarationSyntax> children = ParseIndentedBlock(BlockContext.Any);
        return new TypeDeclarationSyntax(path, children, SpanFrom(start));
    }

    /// <summary>
    /// Parses a proc or verb signature and skips its body.
    /// </summary>
    /// <remarks>
    /// A <c>proc</c> or <c>verb</c> segment in the path means this declares a new proc; without one
    /// the declaration overrides an inherited proc. Getting that backwards is a duplicate-definition
    /// error in DM, so the distinction is recorded rather than discarded.
    /// </remarks>
    private ProcDeclarationSyntax ParseProc(PathSyntax path, BlockContext context, int start)
    {
        List<ParameterSyntax> parameters = ParseParameters();

        bool isVerb = ContainsSegment(path, "verb") || context == BlockContext.Proc && false;
        bool declaresNew = ContainsSegment(path, "proc") || ContainsSegment(path, "verb")
                           || context == BlockContext.Proc;

        // A proc may declare a return type: `parent() as /hud_obj`. It belongs to the signature, so
        // it is consumed before the body — without it the clause reads as an inline body.
        if (Current == TokenKind.KeywordAs)
        {
            _position++;

            while (Current is TokenKind.Slash or TokenKind.Dot || IsNameLike(Current))
                _position++;

            while (Current == TokenKind.Pipe)
            {
                _position++;

                while (Current is TokenKind.Slash or TokenKind.Dot || IsNameLike(Current))
                    _position++;
            }
        }

        (BlockStatementSyntax? body, int next) =
            StatementParser.ParseProcBody(_tokens, _lex.Text, _diagnostics, _position, _modes);

        _position = next > _position ? next : _position + 1;

        return new ProcDeclarationSyntax(path, parameters, isVerb, declaresNew, body, SpanFrom(start));
    }

    /// <summary>
    /// Parses a variable declaration, including further names sharing the same <c>var/</c>.
    /// </summary>
    /// <remarks>
    /// The segments after <c>var</c> are modifiers, then the declared type, then the name. So
    /// <c>var/mob/test/t</c> declares <c>t</c> of type <c>/mob/test</c>, and <c>var/const/X</c>
    /// declares <c>X</c> with no type. Splitting these correctly is what makes <c>t.</c> completion
    /// possible at M6.
    /// </remarks>
    private VarDeclarationSyntax ParseVar(PathSyntax path, int varIndex, int start, bool inVarContext = true)
    {
        List<string> modifiers = new();
        List<string> typeSegments = new();
        List<TextSpan> typeSpans = new();

        for (int i = varIndex + 1; i < path.Segments.Count - 1; i++)
        {
            if (Modifiers.Contains(path.Segments[i]))
                modifiers.Add(path.Segments[i]);
            else
            {
                typeSegments.Add(path.Segments[i]);
                typeSpans.Add(path.SegmentSpans[i]);
            }
        }

        PathSyntax? declaredType = typeSegments.Count == 0
            ? null
            : new PathSyntax(
                PathAnchor.Absolute,
                typeSegments,
                TextSpan.FromBounds(typeSpans[0].Start, typeSpans[^1].End),
                typeSpans);

        bool hasInitializer = false;
        ExpressionSyntax? initializer = null;
        List<VarDeclarationSyntax> siblings = new();

        // The bracket declaration forms: `var/L[]` is a list, `var/M[10]` presizes it, and
        // `var/grid[10][5]` nests. The brackets belong to the declaration, not to an expression.
        SkipDeclarationBrackets();

        if (Current == TokenKind.Assign)
        {
            hasInitializer = true;
            initializer = ParseInitializer();
        }

        // Several names can share one `var/`, separated by commas — `var/a = 1, b = 2` — or by
        // semicolons when written on one line, as in stddef.dm's `x = 0; y = 0; z = 0`.
        while (Current is TokenKind.Comma or TokenKind.Semicolon)
        {
            _position++;

            if (Current != TokenKind.Identifier)
                break;

            int siblingStart = _position;
            PathSyntax siblingPath = ParsePath();
            SkipDeclarationBrackets();

            bool siblingInitializer = false;
            ExpressionSyntax? siblingValue = null;
            if (Current == TokenKind.Assign)
            {
                siblingInitializer = true;
                siblingValue = ParseInitializer();
            }

            siblings.Add(new VarDeclarationSyntax(
                siblingPath,
                modifiers,
                declaredType,
                siblingInitializer,
                siblingValue,
                Array.Empty<VarDeclarationSyntax>(),
                SpanFrom(siblingStart),
                inVarContext));
        }

        ConsumeLineEnd();

        // A var may still open a block, as in a type with initialised members beneath it.
        SkipIndentedBlock();

        return new VarDeclarationSyntax(
            path, modifiers, declaredType, hasInitializer, initializer, siblings, SpanFrom(start), inVarContext);
    }

    // -- paths -------------------------------------------------------------

    /// <summary>
    /// Reads a path. Mid-path <c>/</c> and <c>.</c> are equivalent; only the leading separator
    /// carries meaning.
    /// </summary>
    private PathSyntax ParsePath()
    {
        int start = _position;
        PathAnchor anchor = PathAnchor.Relative;

        if (Current == TokenKind.Slash)
        {
            anchor = PathAnchor.Absolute;
            _position++;
        }
        else if (Current == TokenKind.Dot && IsNameLike(Peek()))
        {
            anchor = PathAnchor.UpwardSearch;
            _position++;
        }

        List<string> segments = new();
        List<TextSpan> spans = new();

        while (IsNameLike(Current))
        {
            segments.Add(TextOf(_position));
            spans.Add(CurrentSpan);
            _position++;

            if ((Current == TokenKind.Slash || Current == TokenKind.Dot) && IsNameLike(Peek()))
            {
                _position++;
                continue;
            }

            break;
        }

        // An overloaded operator's name is `operator` glued to the operator itself — `operator+`,
        // `operator:=`, `operator[]`, `operator""`. The lexer cannot know that, so it emits them as
        // separate tokens and the name is reassembled here. Without this, `proc/operator:=(v)`
        // parses as a type named `operator` and its body is read as declarations.
        if (segments.Count > 0 && segments[^1] == "operator" && IsOperatorNamePart(Current))
        {
            int nameStart = spans[^1].Start;

            while (IsOperatorNamePart(Current))
                _position++;

            TextSpan combined = TextSpan.FromBounds(nameStart, _tokens[_position - 1].Span.End);
            segments[^1] = _lex.Text.ToString(combined);
            spans[^1] = combined;
        }

        TextSpan span = segments.Count == 0
            ? CurrentSpan
            : TextSpan.FromBounds(_tokens[start].Span.Start, spans[^1].End);

        return new PathSyntax(anchor, segments, span, spans);
    }

    // -- parameters --------------------------------------------------------

    private List<ParameterSyntax> ParseParameters()
    {
        List<ParameterSyntax> parameters = new();

        if (Current != TokenKind.OpenParen)
            return parameters;

        _position++;
        int depth = 1;
        int segmentStart = _position;

        while (!AtEnd && depth > 0)
        {
            if (Current is TokenKind.OpenParen or TokenKind.OpenBracket)
            {
                depth++;
            }
            else if (Current is TokenKind.CloseParen or TokenKind.CloseBracket)
            {
                depth--;
                if (depth == 0)
                {
                    if (_position > segmentStart)
                        parameters.Add(ReadParameter(segmentStart, _position));

                    _position++;
                    break;
                }
            }
            else if (Current == TokenKind.Comma && depth == 1)
            {
                parameters.Add(ReadParameter(segmentStart, _position));
                _position++;
                segmentStart = _position;
                continue;
            }

            _position++;
        }

        return parameters;
    }

    /// <summary>
    /// Extracts a parameter from its token range.
    /// </summary>
    /// <remarks>
    /// The name is the last path segment before any <c>as</c> clause or default value, so
    /// <c>mob/M as mob in view()</c> yields the name <c>M</c> with type <c>/mob</c>.
    /// </remarks>
    private ParameterSyntax ReadParameter(int start, int end)
    {
        List<string> segments = new();
        List<TextSpan> spans = new();
        string? inputType = null;
        bool hasDefault = false;

        for (int i = start; i < end; i++)
        {
            TokenKind kind = _tokens[i].Kind;

            if (kind == TokenKind.KeywordAs)
            {
                if (i + 1 < end && IsNameLike(_tokens[i + 1].Kind))
                    inputType = TextOf(i + 1);

                break;
            }

            if (kind == TokenKind.Assign)
            {
                hasDefault = true;
                break;
            }

            if (kind == TokenKind.KeywordIn)
                break;

            if (IsNameLike(kind))
            {
                segments.Add(TextOf(i));
                spans.Add(_tokens[i].Span);
            }
        }

        TextSpan span = start < end
            ? TextSpan.FromBounds(_tokens[start].Span.Start, _tokens[end - 1].Span.End)
            : CurrentSpan;

        if (segments.Count == 0)
            return new ParameterSyntax(string.Empty, null, inputType, hasDefault, span);

        string name = segments[^1];
        PathSyntax? type = segments.Count > 1
            ? new PathSyntax(
                PathAnchor.Absolute,
                segments.GetRange(0, segments.Count - 1),
                TextSpan.FromBounds(spans[0].Start, spans[^2].End),
                spans.GetRange(0, spans.Count - 1))
            : null;

        return new ParameterSyntax(name, type, inputType, hasDefault, span);
    }

    // -- helpers -----------------------------------------------------------

    /// <summary>Consumes the <c>[]</c> / <c>[10]</c> suffixes of a bracket list declaration.</summary>
    private void SkipDeclarationBrackets()
    {
        while (Current == TokenKind.OpenBracket)
        {
            int depth = 0;

            do
            {
                if (Current == TokenKind.OpenBracket)
                    depth++;
                else if (Current == TokenKind.CloseBracket)
                    depth--;

                _position++;
            }
            while (depth > 0 && !AtEnd);
        }
    }

    /// <summary>Parses the expression after an <c>=</c>, then resynchronises to the element's end.</summary>
    /// <remarks>
    /// The expression parser stops at the first token it cannot continue from, which for a construct
    /// it does not yet cover can be short of the real end. Discarding the remainder keeps the
    /// enclosing declaration parseable, so one odd initialiser cannot cost the rest of a var list.
    /// </remarks>
    private ExpressionSyntax? ParseInitializer()
    {
        _position++;

        (ExpressionSyntax expression, int next) =
            ExpressionParser.Parse(_tokens, _lex.Text, _diagnostics, _position);

        // Guarantee progress even when the expression consumed nothing.
        _position = next > _position ? next : _position + 1;

        SkipToElementEnd();

        return expression is ErrorExpressionSyntax ? null : expression;
    }

    /// <summary>Discards what is left of a var-list element, stopping before its separator.</summary>
    private void SkipToElementEnd()
    {
        int depth = 0;

        while (!AtEnd)
        {
            switch (Current)
            {
                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.OpenBrace:
                    depth++;
                    break;

                case TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace:
                    depth--;
                    break;

                case TokenKind.Comma when depth <= 0:
                case TokenKind.Semicolon when depth <= 0:
                case TokenKind.Newline when depth <= 0:
                case TokenKind.Indent when depth <= 0:
                case TokenKind.Dedent when depth <= 0:
                    return;
            }

            _position++;
        }
    }

    private void ConsumeLineEnd()
    {
        while (Current is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent) && !AtEnd)
            _position++;

        if (Current == TokenKind.Newline)
            _position++;
    }

    /// <summary>
    /// Consumes a directive line, applying it when it is a <c>#pragma</c> that changes the grammar.
    /// </summary>
    /// <remarks>
    /// A <c>#pragma syntax C for|switch</c> written between two procs changes how the second one
    /// parses, so the state has to be tracked at file level and handed to the statement parser.
    /// </remarks>
    private void ConsumeDirective()
    {
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
    }

    private void RecoverToNextLine()
    {
        while (!AtEnd && Current is not (TokenKind.Newline or TokenKind.Dedent))
            _position++;

        if (Current == TokenKind.Newline)
            _position++;
    }

    private TextSpan SpanFrom(int startToken)
    {
        int endToken = Math.Max(startToken, _position - 1);
        endToken = Math.Min(endToken, _tokens.Count - 1);

        if (startToken >= _tokens.Count)
            return CurrentSpan;

        return TextSpan.FromBounds(_tokens[startToken].Span.Start, _tokens[endToken].Span.End);
    }

    private void Report(string id, TextSpan span, string message)
        => _diagnostics.Add(Diagnostic.Error(id, span, message));

    private static int IndexOfSegment(PathSyntax path, string name)
    {
        for (int i = 0; i < path.Segments.Count; i++)
        {
            if (path.Segments[i] == name)
                return i;
        }

        return -1;
    }

    private static bool ContainsSegment(PathSyntax path, string name) => IndexOfSegment(path, name) >= 0;

    /// <summary>
    /// Keywords are legal path segments — <c>var</c> is one, and DM reserves very little — so a
    /// keyword token is name-like here.
    /// </summary>
    private static bool IsNameLike(TokenKind kind)
        => kind == TokenKind.Identifier || (kind >= TokenKind.KeywordVar && kind <= TokenKind.KeywordGlobal);

    /// <summary>
    /// Tokens that can form part of an overloaded operator's name. Stops at <c>(</c>, which begins
    /// the parameter list, and at layout.
    /// </summary>
    private static bool IsOperatorNamePart(TokenKind kind) => kind
        is not (TokenKind.OpenParen or TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent
            or TokenKind.EndOfFile or TokenKind.Identifier or TokenKind.Comment)
        && !(kind >= TokenKind.KeywordVar && kind <= TokenKind.KeywordGlobal);
}
