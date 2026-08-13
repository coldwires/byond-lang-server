using System.Collections.Generic;
using System.Text;
using Dm.Core.Diagnostics;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>
/// Output of <see cref="Lexer.Lex"/>: the token stream plus anything that went wrong.
/// </summary>
public sealed class LexResult
{
    internal LexResult(SourceText text, IReadOnlyList<Token> tokens, IReadOnlyList<Diagnostic> diagnostics)
    {
        Text = text;
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    /// <summary>The source the tokens were lexed from; every token span indexes into it.</summary>
    public SourceText Text { get; }

    /// <summary>All tokens, ending with <see cref="TokenKind.EndOfFile"/>. Comments included.</summary>
    internal IReadOnlyList<Token> Tokens { get; }

    /// <summary>What went wrong while lexing; empty on a clean file.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    internal string GetText(Token token) => Text.ToString(token.Span);

    /// <summary>
    /// Stable, diff-readable dump used by snapshot fixtures and <c>Dm.Cli dump-tokens</c>.
    /// </summary>
    /// <remarks>
    /// Positions are line:column so a fixture stays readable, and stay valid when surrounding
    /// content shifts by a few characters. Token text is escaped so whitespace and newlines cannot
    /// break the one-token-per-line format.
    /// </remarks>
    public string ToDebugString()
    {
        StringBuilder builder = new();

        foreach (Token token in Tokens)
        {
            LinePosition position = Text.GetLinePosition(token.Span.Start, PositionEncoding.Utf16);

            builder.Append(position.Line.ToString().PadLeft(4));
            builder.Append(':');
            builder.Append(position.Character.ToString().PadRight(4));
            builder.Append("  ");
            builder.Append(token.Kind.ToString().PadRight(20));

            if (!token.Span.IsEmpty)
            {
                builder.Append('"');
                Escape(builder, Text.AsSpan(token.Span));
                builder.Append('"');
            }

            builder.AppendLine();
        }

        if (Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("diagnostics:");

            foreach (Diagnostic diagnostic in Diagnostics)
            {
                LinePosition position = Text.GetLinePosition(diagnostic.Span.Start, PositionEncoding.Utf16);
                builder.AppendLine($"  {position.Line}:{position.Character}  {diagnostic.Id}  {diagnostic.Message}");
            }
        }

        return builder.ToString();
    }

    private static void Escape(StringBuilder builder, System.ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            switch (c)
            {
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default: builder.Append(c); break;
            }
        }
    }
}
