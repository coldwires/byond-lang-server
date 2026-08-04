using System;
using System.Collections.Generic;
using System.Text;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>
/// Reads the <c>///</c> comment attached to a declaration.
/// </summary>
/// <remarks>
/// Shared by hover and completion so a symbol documents itself the same way wherever it appears.
///
/// Scanned as whole lines rather than from comment tokens: a doc comment is a run of lines, and the
/// run ends at the first line that is not one. A blank line or a plain <c>//</c> therefore separates
/// a comment from the declaration below it, which is what a reader assumes.
/// </remarks>
public static class DocComments
{
    /// <summary>
    /// The doc comment directly above a line, markers stripped. Both DM forms are recognised.
    /// </summary>
    /// <remarks>
    /// Real DM uses both, and by a wide margin in both cases: on /tg/station, 4,870 files carry
    /// <c>///</c> runs and 1,784 carry <c>/** ... */</c> blocks. Supporting only the first returned
    /// nothing for the second, silently.
    /// </remarks>
    public static string Above(SourceText source, int declarationLine)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (declarationLine <= 0)
            return string.Empty;

        string previous = source.GetLineText(declarationLine - 1).Trim();

        List<string> lines = previous.EndsWith("*/", StringComparison.Ordinal)
            ? BlockAbove(source, declarationLine)
            : SlashRunAbove(source, declarationLine);

        if (lines.Count == 0)
            return string.Empty;

        StringBuilder documentation = new();

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                documentation.Append('\n');

            documentation.Append(lines[i]);
        }

        return documentation.ToString();
    }

    /// <summary>A run of <c>///</c> lines, nearest first, ending at anything else.</summary>
    private static List<string> SlashRunAbove(SourceText source, int declarationLine)
    {
        List<string> lines = new();

        for (int line = declarationLine - 1; line >= 0; line--)
        {
            string text = source.GetLineText(line).Trim();

            if (!text.StartsWith("///", StringComparison.Ordinal))
                break;

            lines.Add(text[3..].Trim());
        }

        lines.Reverse();
        return lines;
    }

    /// <summary>
    /// A <c>/** ... */</c> block whose <c>*/</c> sits on the line above the declaration.
    /// </summary>
    /// <remarks>
    /// Only a block opened with <c>/**</c> counts. A plain <c>/*</c> is an ordinary comment, and
    /// treating one as documentation would attach commented-out code to the declaration below it.
    /// Continuation asterisks are stripped, which is how the form is always written.
    /// </remarks>
    private static List<string> BlockAbove(SourceText source, int declarationLine)
    {
        List<string> lines = new();

        for (int line = declarationLine - 1; line >= 0; line--)
        {
            string text = source.GetLineText(line).Trim();

            bool opens = text.StartsWith("/**", StringComparison.Ordinal);

            if (opens)
            {
                lines.Add(Strip(text));
                lines.Reverse();

                // A one-line `/** text */` leaves nothing once both delimiters are gone.
                lines.RemoveAll(string.IsNullOrEmpty);
                return lines;
            }

            // A line that is neither the opener nor part of the block means there is no block.
            if (line < declarationLine - 1 && text.Contains("*/", StringComparison.Ordinal))
                return new List<string>();

            lines.Add(Strip(text));
        }

        // Ran off the top without finding an opener, so this was not a doc block.
        return new List<string>();
    }

    /// <summary>Removes the block delimiters and any leading continuation asterisk.</summary>
    private static string Strip(string line)
    {
        string text = line;

        if (text.StartsWith("/**", StringComparison.Ordinal))
            text = text[3..];

        if (text.EndsWith("*/", StringComparison.Ordinal))
            text = text[..^2];

        text = text.Trim();

        if (text.StartsWith("*", StringComparison.Ordinal))
            text = text[1..];

        return text.Trim();
    }

    /// <summary>The run above an offset, for a caller holding a span rather than a line.</summary>
    public static string AboveOffset(SourceText source, int offset)
    {
        ArgumentNullException.ThrowIfNull(source);

        return offset > source.Length ? string.Empty : Above(source, source.GetLineIndex(offset));
    }
}
