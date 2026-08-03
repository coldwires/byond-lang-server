using System.Collections.Generic;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Includes;

/// <summary>
/// One <c>#include</c> found in a file.
/// </summary>
public readonly struct IncludeDirective
{
    public IncludeDirective(string target, bool isLibrary, TextSpan span)
    {
        Target = target;
        IsLibrary = isLibrary;
        Span = span;
    }

    /// <summary>The path as written, before any normalisation.</summary>
    public string Target { get; }

    /// <summary>True for <c>&lt;vendor/name&gt;</c>, which resolves outside the project.</summary>
    public bool IsLibrary { get; }

    /// <summary>Span of the whole directive, for diagnostics.</summary>
    public TextSpan Span { get; }

    /// <summary>
    /// Extracts every <c>#include</c> from a lexed file, in source order.
    /// </summary>
    /// <remarks>
    /// Driven off the token stream rather than a regex so that a directive inside a comment or a
    /// string is not mistaken for a real one — both appear in practice, including commented-out
    /// includes left in <c>.dme</c> files.
    /// </remarks>
    public static IEnumerable<IncludeDirective> FindAll(LexResult lex)
    {
        IReadOnlyList<Token> tokens = lex.Tokens;

        for (int i = 0; i < tokens.Count - 1; i++)
        {
            if (tokens[i].Kind != TokenKind.Hash || tokens[i + 1].Kind != TokenKind.DirectiveName)
                continue;

            if (lex.GetText(tokens[i + 1]) != "include")
                continue;

            int argument = i + 2;
            if (argument >= tokens.Count)
                break;

            if (tokens[argument].Kind == TokenKind.StringStart)
            {
                int end = argument;
                while (end < tokens.Count && tokens[end].Kind != TokenKind.StringEnd)
                    end++;

                if (end >= tokens.Count)
                    continue;

                TextSpan inner = TextSpan.FromBounds(tokens[argument].Span.End, tokens[end].Span.Start);
                yield return new IncludeDirective(
                    lex.Text.ToString(inner),
                    isLibrary: false,
                    TextSpan.FromBounds(tokens[i].Span.Start, tokens[end].Span.End));

                i = end;
                continue;
            }

            if (tokens[argument].Kind == TokenKind.Less)
            {
                int end = argument + 1;
                while (end < tokens.Count
                       && tokens[end].Kind != TokenKind.Greater
                       && tokens[end].Kind != TokenKind.Newline
                       && tokens[end].Kind != TokenKind.EndOfFile)
                {
                    end++;
                }

                if (end >= tokens.Count || tokens[end].Kind != TokenKind.Greater)
                    continue;

                TextSpan inner = TextSpan.FromBounds(tokens[argument].Span.End, tokens[end].Span.Start);
                yield return new IncludeDirective(
                    lex.Text.ToString(inner).Trim(),
                    isLibrary: true,
                    TextSpan.FromBounds(tokens[i].Span.Start, tokens[end].Span.End));

                i = end;
            }
        }
    }
}
