using System;
using System.Collections.Generic;
using System.Globalization;
using Dm.Core.Text;

namespace Dm.Core.Includes;

/// <summary>Why an edit could not be produced, so a caller can say which.</summary>
public enum DmeEditRefusal
{
    /// <summary>Not refused; the edit exists.</summary>
    None = 0,

    /// <summary>No <c>// BEGIN_INCLUDE</c> … <c>// END_INCLUDE</c> pair.</summary>
    NoBlock = 1,

    /// <summary>
    /// The block holds a preprocessor conditional.
    /// </summary>
    /// <remarks>
    /// A line's presence inside <c>#if</c> does not mean the file is in the build, so neither
    /// ticking nor unticking has a correct answer. Refusing beats guessing at the project file.
    /// </remarks>
    Conditional = 2,

    /// <summary>Already in the state asked for; nothing to do.</summary>
    NoChange = 3,
}

/// <summary>A replacement to apply to the <c>.dme</c>'s text: a span, and what goes there.</summary>
/// <remarks>
/// An edit rather than a written file, because the <c>.dme</c> is usually open — and often dirty —
/// in the editor that asked. A tick is a zero-length insert, which applies cleanly to a buffer the
/// caller owns; writing the file underneath them would lose their unsaved changes.
/// </remarks>
public sealed class DmeEdit
{
    /// <summary>The span to replace and the text that goes there.</summary>
    public DmeEdit(TextSpan span, string replacement)
    {
        Span = span;
        Replacement = replacement;
    }

    /// <summary>What to replace in the <c>.dme</c>'s text; zero-length for a pure insert.</summary>
    public TextSpan Span { get; }

    /// <summary>Text that goes in place of the span; empty for a pure delete.</summary>
    public string Replacement { get; }

    /// <summary>Debug rendering.</summary>
    public override string ToString()
        => Replacement.Length == 0 ? $"delete {Span}" : $"insert at {Span.Start}";
}

/// <summary>
/// Reads and edits DreamMaker's own <c>#include</c> block — the file tickmarks.
/// </summary>
/// <remarks>
/// <para>
/// DreamMaker owns the region between <c>// BEGIN_INCLUDE</c> and <c>// END_INCLUDE</c> and
/// rewrites it wholesale; everything else in the file is the author's and must survive untouched.
/// Real <c>.dme</c> files carry manual includes above the block — mlaas has
/// <c>src\_constants.dm</c> both manually and inside it — and an untick must remove only the one
/// inside.
/// </para>
/// <para>
/// The sort order was verified against DreamMaker's own output rather than assumed, because two of
/// its three rules are counter-intuitive and a small corpus confirms the wrong one:
/// </para>
/// <list type="bullet">
/// <item><description><b>Files sort before directories.</b> warklan writes <c>Interface.dmf</c>
/// above <c>Code\Admin.dm</c>.</description></item>
/// <item><description><b>Extension before filename.</b> madridspy writes <c>skiner.dmf</c> below
/// <c>test_lighting.dm</c>, and tgstation writes <c>skin.dmf</c> below <c>stylesheet.dm</c> —
/// both despite the name sorting earlier.</description></item>
/// <item><description><b>Lowercase ordinal, not alphabetical.</b> warklan's <c>Code\</c> holds
/// <c>NPC's.dm</c>, <c>NPC-Shop.dm</c>, <c>NPCItemAdd.dm</c> in that order — byte order on the
/// lowercased name, where <c>'</c> is 0x27 and <c>-</c> is 0x2D. And <c>Admin.dm</c> precedes
/// <c>AI.dm</c>, which is only correct lowercased first.</description></item>
/// </list>
/// </remarks>
internal static class DmeIncludeBlock
{
    private const string BeginMarker = "// BEGIN_INCLUDE";
    private const string EndMarker = "// END_INCLUDE";

    /// <summary>One entry inside the block.</summary>
    internal readonly struct Entry
    {
        public Entry(string path, TextSpan lineSpan)
        {
            Path = path;
            LineSpan = lineSpan;
        }

        /// <summary>The path as written, backslashes included.</summary>
        public string Path { get; }

        /// <summary>The whole line, terminator included, so deleting it leaves no blank.</summary>
        public TextSpan LineSpan { get; }
    }

    /// <summary>Whether the block lists this file.</summary>
    public static bool IsTicked(SourceText dme, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(dme);

        foreach (Entry entry in Entries(dme))
        {
            if (SamePath(entry.Path, relativePath))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The edit that adds this file, or null with a reason.
    /// </summary>
    /// <remarks>
    /// Inserted at its sorted position, using the terminator the file already uses — a lone LF in
    /// a CRLF file makes DreamMaker rewrite the whole thing on its next save, which destroys the
    /// diff for everyone else on the team.
    /// </remarks>
    public static DmeEdit? Tick(SourceText dme, string relativePath, out DmeEditRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(dme);

        if (!FindBlock(dme, out int contentStart, out int contentEnd))
        {
            refusal = DmeEditRefusal.NoBlock;
            return null;
        }

        if (HasConditional(dme, contentStart, contentEnd))
        {
            refusal = DmeEditRefusal.Conditional;
            return null;
        }

        string normalised = Normalise(relativePath);

        if (IsTicked(dme, normalised))
        {
            refusal = DmeEditRefusal.NoChange;
            return null;
        }

        refusal = DmeEditRefusal.None;

        string terminator = Terminator(dme);
        string line = $"#include \"{normalised}\"{terminator}";

        // The first entry that sorts after the new path; if none does, the end of the block.
        foreach (Entry entry in Entries(dme))
        {
            if (Compare(normalised, entry.Path) < 0)
                return new DmeEdit(new TextSpan(entry.LineSpan.Start, 0), line);
        }

        return new DmeEdit(new TextSpan(contentEnd, 0), line);
    }

    /// <summary>
    /// The edit that removes this file, or null with a reason.
    /// </summary>
    /// <remarks>
    /// Removes <b>every</b> matching line in the block, not just the first: the block can carry the
    /// same path twice, which real <c>.dme</c> files do when DreamMaker's generated block re-adds
    /// an entry the author had written manually. Returned as one span covering the run when the
    /// duplicates are adjacent, and as the first line otherwise — a caller applying edits one at a
    /// time re-asks after each.
    /// </remarks>
    public static DmeEdit? Untick(SourceText dme, string relativePath, out DmeEditRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(dme);

        if (!FindBlock(dme, out int contentStart, out int contentEnd))
        {
            refusal = DmeEditRefusal.NoBlock;
            return null;
        }

        if (HasConditional(dme, contentStart, contentEnd))
        {
            refusal = DmeEditRefusal.Conditional;
            return null;
        }

        foreach (Entry entry in Entries(dme))
        {
            if (SamePath(entry.Path, relativePath))
            {
                refusal = DmeEditRefusal.None;
                return new DmeEdit(entry.LineSpan, string.Empty);
            }
        }

        refusal = DmeEditRefusal.NoChange;
        return null;
    }

    /// <summary>Every parseable <c>#include "..."</c> line inside the block, in file order.</summary>
    /// <remarks>
    /// A line that does not parse is <b>skipped</b>, not treated as position zero: a stray comment
    /// inside the block would otherwise send every insert to the top. Angle-bracket includes are
    /// skipped too — a BYOND library is not a project file and DreamMaker does not list one.
    /// </remarks>
    public static IEnumerable<Entry> Entries(SourceText dme)
    {
        ArgumentNullException.ThrowIfNull(dme);

        if (!FindBlock(dme, out int contentStart, out int contentEnd))
            yield break;

        int line = dme.GetLineIndex(contentStart);

        while (line < dme.LineCount)
        {
            TextSpan span = dme.GetLineSpanIncludingTerminator(line);

            if (span.Start >= contentEnd)
                yield break;

            string text = dme.ToString(span);

            if (ReadInclude(text) is { } path)
                yield return new Entry(path, span);

            line++;
        }
    }

    /// <summary>The path inside <c>#include "..."</c>, or null for anything else.</summary>
    private static string? ReadInclude(string line)
    {
        string trimmed = line.Trim();

        if (!trimmed.StartsWith("#include", StringComparison.OrdinalIgnoreCase))
            return null;

        int open = trimmed.IndexOf('"');
        if (open < 0)
            return null;

        int close = trimmed.IndexOf('"', open + 1);
        return close > open ? trimmed[(open + 1)..close] : null;
    }

    /// <summary>Locates the block's content, exclusive of both marker lines.</summary>
    private static bool FindBlock(SourceText dme, out int contentStart, out int contentEnd)
    {
        contentStart = 0;
        contentEnd = 0;

        string text = dme.ToString();

        int begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
            return false;

        int end = text.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0)
            return false;

        // Content starts after the BEGIN line's terminator and runs to the start of the END line.
        int afterBegin = text.IndexOf('\n', begin);
        if (afterBegin < 0 || afterBegin > end)
            return false;

        contentStart = afterBegin + 1;

        // Back up to the start of the END marker's own line, so its indentation is not consumed.
        int lineStart = text.LastIndexOf('\n', end - 1);
        contentEnd = lineStart < 0 ? end : lineStart + 1;

        return contentEnd >= contentStart;
    }

    private static bool HasConditional(SourceText dme, int start, int end)
    {
        string block = dme.ToString(TextSpan.FromBounds(start, end));

        foreach (string line in block.Split('\n'))
        {
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("#if", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("#else", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("#elif", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("#endif", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The terminator the file already uses, read off its first line ending.
    /// </summary>
    /// <remarks>
    /// A <c>.dme</c> is usually CRLF. Writing one LF line makes DreamMaker rewrite the whole file
    /// on its next save, which turns one tick into a whole-file diff.
    /// </remarks>
    private static string Terminator(SourceText dme)
    {
        string text = dme.ToString();
        int newline = text.IndexOf('\n');

        if (newline <= 0)
            return "\r\n";

        return text[newline - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>Backslashes, always — what DreamMaker writes and what it expects to read back.</summary>
    private static string Normalise(string path) => path.Replace('/', '\\').Trim();

    private static bool SamePath(string a, string b)
        => string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// DreamMaker's sort order. Negative when <paramref name="left"/> comes first.
    /// </summary>
    /// <remarks>
    /// Directory segments pairwise first; a file sorts before a directory; and two files in one
    /// directory compare by <b>extension first</b>, then filename. All comparisons are ordinal on
    /// the lowercased string — byte order, not culture-aware and not alphabetical.
    /// </remarks>
    internal static int Compare(string left, string right)
    {
        string[] a = Normalise(left).Split('\\');
        string[] b = Normalise(right).Split('\\');

        int directories = Math.Min(a.Length, b.Length) - 1;

        for (int i = 0; i < directories; i++)
        {
            int byDirectory = Ordinal(a[i], b[i]);
            if (byDirectory != 0)
                return byDirectory;
        }

        // One ran out of directories: it is a file at this level, and files sort above directories.
        if (a.Length != b.Length)
            return a.Length < b.Length ? -1 : 1;

        string leftName = a[^1];
        string rightName = b[^1];

        int byExtension = Ordinal(Extension(leftName), Extension(rightName));
        return byExtension != 0 ? byExtension : Ordinal(leftName, rightName);
    }

    /// <summary>The extension without its dot, or empty.</summary>
    private static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fileName[(dot + 1)..];
    }

    private static int Ordinal(string a, string b)
        => string.CompareOrdinal(a.ToLower(CultureInfo.InvariantCulture), b.ToLower(CultureInfo.InvariantCulture));
}
