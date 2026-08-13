using System;
using System.Collections.Generic;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

internal enum DirectiveKind
{
    /// <summary>A <c>#</c> followed by a name we do not recognise.</summary>
    Unknown,

    Define,
    Undef,
    If,
    Ifdef,
    Ifndef,
    Elif,
    Else,
    Endif,
    Include,
    Warn,
    Error,
    Pragma,
}

/// <summary>
/// One preprocessor directive located in a lexed file.
/// </summary>
/// <remarks>
/// <see cref="ArgumentStart"/> and <see cref="ArgumentEnd"/> bound the directive's payload as a
/// half-open range of token indices, so a caller reads the argument without re-scanning text.
/// Layout tokens are excluded: the range stops at the newline that ends the logical line, with line
/// continuations already folded away by the lexer.
/// </remarks>
internal readonly struct Directive
{
    public Directive(
        DirectiveKind kind,
        string name,
        int hashIndex,
        int argumentStart,
        int argumentEnd,
        TextSpan span)
    {
        Kind = kind;
        Name = name;
        HashIndex = hashIndex;
        ArgumentStart = argumentStart;
        ArgumentEnd = argumentEnd;
        Span = span;
    }

    public DirectiveKind Kind { get; }

    /// <summary>The directive name as written, e.g. <c>define</c>.</summary>
    public string Name { get; }

    /// <summary>Token index of the <c>#</c>.</summary>
    public int HashIndex { get; }

    /// <summary>First token index of the payload.</summary>
    public int ArgumentStart { get; }

    /// <summary>One past the last payload token.</summary>
    public int ArgumentEnd { get; }

    /// <summary>Span covering the whole directive line, for diagnostics.</summary>
    public TextSpan Span { get; }

    public bool HasArguments => ArgumentEnd > ArgumentStart;

    public override string ToString() => $"#{Name}[{ArgumentStart}..{ArgumentEnd})";
}

/// <summary>
/// Finds preprocessor directives in a lexed file.
/// </summary>
/// <remarks>
/// Driven off the token stream rather than text, so a <c>#include</c> inside a comment or a string
/// is not mistaken for a directive. Both occur in real code — a library documenting its own usage in
/// a block comment, and commented-out includes left in <c>.dme</c> files.
/// </remarks>
internal static class DirectiveScanner
{
    public static IReadOnlyList<Directive> Scan(LexResult lex)
    {
        ArgumentNullException.ThrowIfNull(lex);

        List<Directive> directives = new();
        IReadOnlyList<Token> tokens = lex.Tokens;

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.Hash)
                continue;

            // A `#` with no directive name is stringification inside a macro body, not a directive.
            if (i + 1 >= tokens.Count || tokens[i + 1].Kind != TokenKind.DirectiveName)
                continue;

            string name = lex.GetText(tokens[i + 1]);

            int start = i + 2;
            int end = start;
            while (end < tokens.Count && !EndsDirective(tokens[end].Kind))
                end++;

            directives.Add(new Directive(
                Classify(name),
                name,
                i,
                start,
                end,
                TextSpan.FromBounds(tokens[i].Span.Start, tokens[end > start ? end - 1 : i + 1].Span.End)));

            i = end - 1;
        }

        return directives;
    }

    /// <summary>
    /// A directive runs to the end of its logical line. Indent and Dedent are excluded too: a
    /// column-0 <c>#endif</c> inside an indented block would otherwise absorb the dedents that
    /// belong to the code around it.
    /// </summary>
    private static bool EndsDirective(TokenKind kind)
        => kind is TokenKind.Newline or TokenKind.EndOfFile or TokenKind.Indent or TokenKind.Dedent;

    private static DirectiveKind Classify(string name) => name switch
    {
        "define" => DirectiveKind.Define,
        "undef" => DirectiveKind.Undef,
        "if" => DirectiveKind.If,
        "ifdef" => DirectiveKind.Ifdef,
        "ifndef" => DirectiveKind.Ifndef,
        "elif" => DirectiveKind.Elif,
        "else" => DirectiveKind.Else,
        "endif" => DirectiveKind.Endif,
        "include" => DirectiveKind.Include,
        // Both spellings, compiler-verified: warklan writes `#warning` and dm.exe echoes it as a
        // warning rather than rejecting an unknown directive. Only `warn` was mapped until
        // 2026-08-12, so `#warning` fell through as Unknown and its echo could never be reported.
        "warn" or "warning" => DirectiveKind.Warn,
        "error" => DirectiveKind.Error,
        "pragma" => DirectiveKind.Pragma,
        _ => DirectiveKind.Unknown,
    };
}
