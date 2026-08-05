using System;
using System.Collections.Generic;
using System.Text;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Substitutes macros into a token stream.
/// </summary>
/// <remarks>
/// <para>
/// Semantics established by compiling against dm.exe 516.1666:
/// </para>
/// <list type="bullet">
/// <item><description><b>No blue paint.</b> Unlike C, DM does not suppress a macro while expanding
/// it. A self-referential or mutually recursive macro is not silently stopped — the compiler
/// expands until it gives up with <c>macro recursion level too deep</c>. We cap at the same point
/// and report it, because a language server cannot spin.</description></item>
/// <item><description>Recursion is only an error <b>at the use site</b>. Defining a recursive macro
/// and never using it compiles cleanly.</description></item>
/// <item><description><b>Arguments are expanded before substitution.</b>
/// <c>ID(INNER)</c> where <c>INNER</c> is itself a macro yields the expanded value.</description></item>
/// <item><description>A function-like macro is only expanded when followed by <c>(</c>. A bare
/// mention is left alone — the compiler reports it as an undefined var, not as a macro.</description></item>
/// </list>
/// </remarks>
internal sealed class MacroExpander
{
    /// <summary>Mirrors the compiler's own limit; the exact number is not documented.</summary>
    private const int MaxDepth = 64;

    private readonly MacroTable _macros;
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<ExpandedToken> _output = new();

    private MacroExpander(MacroTable macros, List<Diagnostic> diagnostics)
    {
        _macros = macros;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Expands every macro in <paramref name="tokens"/>, a slice of <paramref name="source"/>.
    /// </summary>
    public static IReadOnlyList<ExpandedToken> Expand(
        SourceText source,
        IReadOnlyList<Token> tokens,
        MacroTable macros,
        List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(macros);
        ArgumentNullException.ThrowIfNull(diagnostics);

        MacroExpander expander = new(macros, diagnostics);

        List<ExpandedToken> input = new(tokens.Count);
        foreach (Token token in tokens)
            input.Add(new ExpandedToken(token.Kind, source, token.Span, expansion: null));

        expander.Run(input, parent: null, depth: 0);
        return expander._output;
    }

    private void Run(IReadOnlyList<ExpandedToken> input, MacroExpansion? parent, int depth)
    {
        int i = 0;
        while (i < input.Count)
        {
            ExpandedToken token = input[i];

            if (!IsNameLike(token.Kind) || !_macros.TryGet(token.Text, out MacroDefinition macro))
            {
                _output.Add(token);
                i++;
                continue;
            }

            if (macro.IsFunctionLike)
            {
                // Only an invocation expands. A bare mention is left verbatim.
                if (i + 1 >= input.Count || input[i + 1].Kind != TokenKind.OpenParen)
                {
                    _output.Add(token);
                    i++;
                    continue;
                }

                if (!TryReadArguments(input, i + 1, out List<List<ExpandedToken>> arguments, out int after))
                {
                    _diagnostics.Add(Diagnostic.Error(
                        "DM0130", token.Span, $"unterminated argument list for macro '{macro.Name}'"));
                    _output.Add(token);
                    i++;
                    continue;
                }

                if (!Guard(macro, token, depth))
                {
                    i = after;
                    continue;
                }

                MacroExpansion expansion = new(macro, token.Source, token.Span, parent);
                List<ExpandedToken> body = Substitute(macro, arguments, expansion, depth);
                Run(body, expansion, depth + 1);

                i = after;
                continue;
            }

            if (!Guard(macro, token, depth))
            {
                i++;
                continue;
            }

            MacroExpansion objectExpansion = new(macro, token.Source, token.Span, parent);
            Run(BodyTokens(macro, objectExpansion), objectExpansion, depth + 1);
            i++;
        }
    }

    private bool Guard(MacroDefinition macro, ExpandedToken use, int depth)
    {
        if (depth < MaxDepth)
            return true;

        _diagnostics.Add(Diagnostic.Error(
            "DM0131",
            use.Span,
            $"macro recursion level too deep expanding '{macro.Name}'; the compiler rejects this too"));

        return false;
    }

    private static List<ExpandedToken> BodyTokens(MacroDefinition macro, MacroExpansion expansion)
    {
        List<ExpandedToken> body = new(macro.Body.Count);
        foreach (Token token in macro.Body)
            body.Add(new ExpandedToken(token.Kind, macro.Source, token.Span, expansion));

        return body;
    }

    /// <summary>
    /// Reads a parenthesised argument list, splitting on commas that are not nested.
    /// </summary>
    /// <remarks>
    /// Depth counting covers parentheses and brackets so that <c>F(list[a,b])</c> and
    /// <c>F(g(x,y))</c> pass one argument, not two.
    /// </remarks>
    private static bool TryReadArguments(
        IReadOnlyList<ExpandedToken> input,
        int openParen,
        out List<List<ExpandedToken>> arguments,
        out int after)
    {
        arguments = new List<List<ExpandedToken>>();
        after = openParen;

        int depth = 0;
        List<ExpandedToken> current = new();

        for (int i = openParen; i < input.Count; i++)
        {
            ExpandedToken token = input[i];

            switch (token.Kind)
            {
                // `?[` is a single token and still opens a bracket the matching `]` closes. Left
                // uncounted, the depth goes negative inside an argument and the scanner takes that
                // `]` as the end of the invocation — /tg/station's
                // `OUTER(rt, blacklist?["[rt]"] ? 0 : off)` lost everything from the `]` onward,
                // silently, so the parse failed on a stream that was simply missing its tail.
                case TokenKind.OpenParen or TokenKind.OpenBracket or TokenKind.QuestionOpenBracket:
                    depth++;
                    if (depth == 1 && token.Kind == TokenKind.OpenParen)
                        continue;
                    break;

                case TokenKind.CloseParen or TokenKind.CloseBracket:
                    depth--;
                    if (depth == 0)
                    {
                        if (current.Count > 0 || arguments.Count > 0)
                            arguments.Add(current);

                        after = i + 1;
                        return true;
                    }

                    break;

                case TokenKind.Comma when depth == 1:
                    arguments.Add(current);
                    current = new List<ExpandedToken>();
                    continue;
            }

            current.Add(token);
        }

        return false;
    }

    /// <summary>
    /// Replaces parameters in a macro body with the supplied arguments, applying <c>#</c>,
    /// <c>##</c> and <c>###</c>.
    /// </summary>
    private List<ExpandedToken> Substitute(
        MacroDefinition macro,
        List<List<ExpandedToken>> arguments,
        MacroExpansion expansion,
        int depth)
    {
        IReadOnlyList<string> parameters = macro.Parameters!;
        Dictionary<string, List<ExpandedToken>> bound = new(StringComparer.Ordinal);

        for (int p = 0; p < parameters.Count; p++)
        {
            // Only a NAMED rest parameter absorbs the remainder. With the anonymous `M(a, ...)`
            // form there is nowhere to put the extras and they are discarded, so `MIXED(7, 8, 9)`
            // on `#define MIXED(a, ...) (a)` is `(7)`. Treating the two alike made `a` swallow all
            // three.
            bool isVariadicTail = macro.HasNamedRest && p == parameters.Count - 1;

            if (isVariadicTail)
            {
                // The trailing parameter absorbs every remaining argument, commas included.
                List<ExpandedToken> rest = new();
                for (int a = p; a < arguments.Count; a++)
                {
                    if (a > p)
                        rest.Add(SyntheticToken(TokenKind.Comma, ",", expansion));

                    rest.AddRange(arguments[a]);
                }

                bound[parameters[p]] = rest;
            }
            else
            {
                bound[parameters[p]] = p < arguments.Count ? arguments[p] : new List<ExpandedToken>();
            }
        }

        if (!macro.IsVariadic && arguments.Count > parameters.Count)
        {
            _diagnostics.Add(Diagnostic.Error(
                "DM0132",
                expansion.UseSpan,
                $"macro '{macro.Name}' takes {parameters.Count} argument(s) but {arguments.Count} were given"));
        }

        List<ExpandedToken> result = new();
        IReadOnlyList<Token> body = macro.Body;

        for (int i = 0; i < body.Count; i++)
        {
            Token token = body[i];
            string text = macro.Source.ToString(token.Span);

            // `##param` pastes with no separating space; `N###param` repeats then pastes. Checked
            // before the single `#` case, since a run of hashes starts with one.
            if (token.Kind == TokenKind.Hash && TryReadHashRun(body, i, out int hashes, out int nameIndex))
            {
                string name = macro.Source.ToString(body[nameIndex].Span);

                if (bound.TryGetValue(name, out List<ExpandedToken>? pasted))
                {
                    int repeat = 1;

                    if (hashes >= 3 && result.Count > 0 && result[^1].Kind == TokenKind.Number
                        && int.TryParse(result[^1].Text, out int count) && count >= 0)
                    {
                        repeat = count;
                        result.RemoveAt(result.Count - 1);
                    }

                    AppendPasted(result, RawText(pasted), repeat, expansion);
                    i = nameIndex;
                    continue;
                }
            }

            // `#param` stringifies. The argument is used *unexpanded* and with its original
            // spacing: dm.exe turns `a + b` into "a + b" and `f(1,2)` into "f(1,2)", and
            // `#SAYTWICE(hi)` yields the literal text rather than the expansion.
            if (token.Kind == TokenKind.Hash && i + 1 < body.Count)
            {
                string next = macro.Source.ToString(body[i + 1].Span);
                if (bound.TryGetValue(next, out List<ExpandedToken>? toStringify))
                {
                    result.Add(SyntheticToken(TokenKind.StringStart, "\"", expansion));

                    string literal = RawText(toStringify);
                    if (literal.Length > 0)
                        result.Add(SyntheticToken(TokenKind.StringText, literal, expansion));

                    result.Add(SyntheticToken(TokenKind.StringEnd, "\"", expansion));
                    i++;
                    continue;
                }
            }

            if (bound.TryGetValue(text, out List<ExpandedToken>? argument))
            {
                // Arguments are expanded before substitution.
                MacroExpander inner = new(_macros, _diagnostics);
                inner.Run(argument, expansion, depth + 1);
                result.AddRange(inner._output);
                continue;
            }

            result.Add(new ExpandedToken(token.Kind, macro.Source, token.Span, expansion));
        }

        return result;
    }

    /// <summary>Counts consecutive <c>#</c> tokens and reports the name that follows.</summary>
    private static bool TryReadHashRun(IReadOnlyList<Token> body, int start, out int hashes, out int nameIndex)
    {
        hashes = 0;
        nameIndex = -1;

        int i = start;
        while (i < body.Count && body[i].Kind == TokenKind.Hash)
        {
            hashes++;
            i++;
        }

        if (hashes < 2 || i >= body.Count || !IsNameLike(body[i].Kind))
            return false;

        nameIndex = i;
        return true;
    }

    /// <summary>
    /// Appends a pasted replacement, repeated <paramref name="repeat"/> times.
    /// </summary>
    /// <remarks>
    /// Pasting really does produce one token, not two adjacent ones. Verified:
    /// <c>#define MACROVAR(k) var/macro_state_##k</c> then <c>MACROVAR(right)</c> declares a var
    /// named <c>macro_state_right</c>, and referencing <c>macro_state_</c> alone fails. Likewise
    /// <c>2###t</c> with <c>t = hi</c> produces the single identifier <c>hihi</c>, and <c>3###t</c>
    /// produces <c>hihihi</c>.
    ///
    /// The empty-replacement case is the reference's documented variadic behaviour: a preceding
    /// comma is dropped so <c>list(x, src, ##y)</c> with no trailing arguments does not leave a
    /// dangling separator.
    /// </remarks>
    private static void AppendPasted(
        List<ExpandedToken> result,
        string replacement,
        int repeat,
        MacroExpansion expansion)
    {
        if (repeat <= 0 || replacement.Length == 0)
        {
            if (result.Count > 0 && result[^1].Kind == TokenKind.Comma)
                result.RemoveAt(result.Count - 1);

            return;
        }

        StringBuilder builder = new();
        for (int i = 0; i < repeat; i++)
            builder.Append(replacement);

        string glued = builder.ToString();

        // Glue onto the preceding token when the join could form a single name or number.
        if (result.Count > 0 && CanPasteOnto(result[^1].Kind) && CanPasteFrom(glued))
        {
            string merged = result[^1].Text + glued;
            result[^1] = SyntheticToken(ClassifySingle(merged), merged, expansion);
            return;
        }

        foreach (Token token in Relex(glued))
            result.Add(SyntheticToken(token.Kind, glued.Substring(token.Span.Start, token.Span.Length), expansion));
    }

    private static bool CanPasteOnto(TokenKind kind) => IsNameLike(kind) || kind == TokenKind.Number;

    private static bool CanPasteFrom(string text)
        => text.Length > 0 && (char.IsLetterOrDigit(text[0]) || text[0] == '_');

    /// <summary>
    /// Determines the kind of a pasted result by re-lexing it. The text never existed in any file,
    /// so its category cannot be inherited from either half.
    /// </summary>
    private static TokenKind ClassifySingle(string text)
    {
        IReadOnlyList<Token> tokens = Relex(text);
        return tokens.Count == 1 ? tokens[0].Kind : TokenKind.Unknown;
    }

    private static IReadOnlyList<Token> Relex(string text)
    {
        List<Token> significant = new();

        foreach (Token token in Lexer.Lex(SourceText.From(text)).Tokens)
        {
            if (token.Kind is not (TokenKind.EndOfFile or TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent))
                significant.Add(token);
        }

        return significant;
    }

    /// <summary>
    /// Reconstructs an argument's text exactly as written.
    /// </summary>
    /// <remarks>
    /// Stringification preserves the original spacing — dm.exe turns <c>a + b</c> into
    /// <c>"a + b"</c>, not <c>"a+b"</c> — so the span is taken from the source rather than joining
    /// token texts.
    /// </remarks>
    private static string RawText(IReadOnlyList<ExpandedToken> tokens)
    {
        if (tokens.Count == 0)
            return string.Empty;

        // Slicing the original text is only meaningful when this run really is one stretch of one
        // file. Two things break that, and both occur in real code: tokens from different sources
        // once an argument has itself been expanded, and tokens that revisit the same source
        // backwards, which is what a body naming a parameter twice (`#define X(a) a a`) or the
        // `###` repeat operator produces. Slicing a backwards run threw rather than mis-stringified,
        // so this only ever showed up as a crash on a project large enough to contain one.
        SourceText source = tokens[0].Source;
        bool contiguous = true;
        int previousStart = tokens[0].Span.Start;

        foreach (ExpandedToken token in tokens)
        {
            if (!ReferenceEquals(token.Source, source) || token.Span.Start < previousStart)
            {
                contiguous = false;
                break;
            }

            previousStart = token.Span.Start;
        }

        if (!contiguous)
        {
            StringBuilder fallback = new();
            foreach (ExpandedToken part in tokens)
                fallback.Append(part.Text);

            return fallback.ToString();
        }

        return source.ToString(TextSpan.FromBounds(tokens[0].Span.Start, tokens[^1].Span.End));
    }

    /// <summary>
    /// Manufactures a token whose text exists in no source file.
    /// </summary>
    /// <remarks>
    /// Stringification and pasting produce text that was never written anywhere, so it needs its own
    /// backing buffer. The expansion chain still points at the invocation, which is where a
    /// diagnostic belongs.
    /// </remarks>
    private static ExpandedToken SyntheticToken(TokenKind kind, string text, MacroExpansion expansion)
        => new(kind, SourceText.From(text, $"<macro:{expansion.Macro.Name}>"), new TextSpan(0, text.Length), expansion);

    private static bool IsNameLike(TokenKind kind)
        => kind == TokenKind.Identifier || (kind >= TokenKind.KeywordVar && kind <= TokenKind.KeywordGlobal);
}
