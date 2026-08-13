using System;
using System.Collections.Generic;
using System.Text;

namespace Dm.Core.Text;

/// <summary>
/// Immutable text of one source file, with a line index and position conversion.
/// </summary>
/// <remarks>
/// <para>
/// Content is stored <b>exactly as supplied</b>. It is deliberately not normalised to LF: offsets
/// we hand back have to index the same text a client pushed via <c>dm_set_buffer</c>, and
/// normalising would silently shift every span by one per preceding line. The lexer skips
/// <c>\r</c> when it forms part of a terminator, and multiline string values are CR-stripped when
/// the value is computed rather than when the file is read — which also matches BYOND, since the
/// compiler strips CR itself.
/// </para>
/// <para>
/// All three terminator forms are recognised: <c>\r\n</c>, bare <c>\n</c>, and lone <c>\r</c>.
/// See PLAN.md §4b.
/// </para>
/// <para>
/// Internal offsets are UTF-16 code units, the native indexing of a .NET string. The distinction
/// between UTF-8 and UTF-16 appears only in the character component of a
/// <see cref="LinePosition"/>, and is always explicit — see <see cref="PositionEncoding"/>.
/// </para>
/// </remarks>
public sealed class SourceText
{
    private readonly int[] _lineStarts;
    private int[]? _utf8LineStarts;

    private SourceText(string content, string? path)
    {
        Content = content;
        Path = path;
        _lineStarts = ComputeLineStarts(content);
    }

    /// <summary>The file text, byte-for-byte as supplied.</summary>
    public string Content { get; }

    /// <summary>Originating path, or null for text with no file backing it.</summary>
    public string? Path { get; }

    /// <summary>Length of the text in UTF-16 code units.</summary>
    public int Length => Content.Length;

    /// <summary>
    /// Number of lines. Always at least 1. Text ending in a terminator has a final empty line,
    /// matching how editors present it.
    /// </summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>Wraps text exactly as supplied — line endings are not normalised.</summary>
    public static SourceText From(string content, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new SourceText(content, path);
    }

    /// <summary>The UTF-16 code unit at a zero-based offset.</summary>
    public char this[int offset] => Content[offset];

    /// <summary>The text covered by a span, copied into a new string.</summary>
    public string ToString(TextSpan span) => Content.Substring(span.Start, span.Length);

    /// <summary>The text covered by a span, without copying.</summary>
    public ReadOnlySpan<char> AsSpan(TextSpan span) => Content.AsSpan(span.Start, span.Length);

    /// <summary>The full text, byte-for-byte as supplied.</summary>
    public override string ToString() => Content;

    // -- lines ------------------------------------------------------------

    /// <summary>Offset of the first character of <paramref name="line"/>.</summary>
    public int GetLineStart(int line)
    {
        if (line < 0 || line >= _lineStarts.Length)
            throw new ArgumentOutOfRangeException(nameof(line), line, $"line must be in [0, {_lineStarts.Length})");

        return _lineStarts[line];
    }

    /// <summary>The line's text, excluding its terminator.</summary>
    public TextSpan GetLineSpan(int line)
    {
        int start = GetLineStart(line);
        return TextSpan.FromBounds(start, GetLineEnd(line));
    }

    /// <summary>The line's text including its terminator, if it has one.</summary>
    public TextSpan GetLineSpanIncludingTerminator(int line)
    {
        int start = GetLineStart(line);
        int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Content.Length;
        return TextSpan.FromBounds(start, end);
    }

    /// <summary>The line's text as a string, excluding its terminator.</summary>
    public string GetLineText(int line) => ToString(GetLineSpan(line));

    /// <summary>Zero-based line containing <paramref name="offset"/>. Offsets past the end clamp.</summary>
    public int GetLineIndex(int offset)
    {
        if (offset <= 0)
            return 0;

        if (offset >= Content.Length)
            return _lineStarts.Length - 1;

        int index = Array.BinarySearch(_lineStarts, offset);
        return index >= 0 ? index : ~index - 1;
    }

    // -- position conversion ----------------------------------------------

    /// <summary>Converts an offset to a line/character position in the requested encoding.</summary>
    /// <remarks>
    /// An offset landing inside a line terminator — the <c>\n</c> of a <c>\r\n</c>, say — clamps to
    /// the end of that line's content. Reporting a character index past the visible end of the line
    /// would put a client's caret somewhere it cannot render.
    /// </remarks>
    public LinePosition GetLinePosition(int offset, PositionEncoding encoding)
    {
        int clamped = Math.Clamp(offset, 0, Content.Length);
        int line = GetLineIndex(clamped);
        int lineStart = _lineStarts[line];

        clamped = Math.Min(clamped, GetLineEnd(line));

        int character = encoding == PositionEncoding.Utf16
            ? clamped - lineStart
            : Encoding.UTF8.GetByteCount(Content.AsSpan(lineStart, clamped - lineStart));

        return new LinePosition(line, character);
    }

    /// <summary>
    /// Converts a line/character position to an offset. Out-of-range lines and characters clamp
    /// rather than throw, because these values arrive from three separately written clients.
    /// </summary>
    /// <remarks>
    /// A UTF-8 character index landing inside a multi-byte sequence rounds up to the next
    /// character boundary, which is what LSP recommends for malformed positions.
    /// </remarks>
    public int GetOffset(LinePosition position, PositionEncoding encoding)
    {
        if (position.Line >= _lineStarts.Length)
            return Content.Length;

        int line = Math.Max(position.Line, 0);
        int lineStart = _lineStarts[line];
        int lineEnd = GetLineEnd(line);

        if (position.Character <= 0)
            return lineStart;

        if (encoding == PositionEncoding.Utf16)
            return Math.Min(lineStart + position.Character, lineEnd);

        int bytes = 0;
        int offset = lineStart;

        while (offset < lineEnd && bytes < position.Character)
        {
            int units = IsSurrogatePairAt(offset, lineEnd) ? 2 : 1;
            bytes += Encoding.UTF8.GetByteCount(Content.AsSpan(offset, units));
            offset += units;
        }

        return offset;
    }

    /// <summary>Converts an offset to a line/character position with a UTF-16 character component.</summary>
    public LinePosition GetLinePosition(int offset) => GetLinePosition(offset, PositionEncoding.Utf16);

    /// <summary>
    /// Converts a UTF-16 offset into the equivalent UTF-8 byte offset from the start of the file.
    /// </summary>
    /// <remarks>
    /// Used when handing spans to a client that indexes its buffer in bytes. Unlike
    /// <see cref="GetLinePosition(int)"/> this does not clamp to a line end — a file offset inside a
    /// terminator is meaningful.
    ///
    /// Per-line byte offsets are computed once on first use, so a conversion costs a scan of one
    /// line rather than of the whole file.
    /// </remarks>
    public int GetUtf8Offset(int utf16Offset)
    {
        int clamped = Math.Clamp(utf16Offset, 0, Content.Length);
        int line = GetLineIndex(clamped);
        int lineStart = _lineStarts[line];

        return Utf8LineStarts[line] + Encoding.UTF8.GetByteCount(Content.AsSpan(lineStart, clamped - lineStart));
    }

    /// <summary>Length of the text in UTF-8 bytes.</summary>
    public int Utf8Length => GetUtf8Offset(Content.Length);

    private int[] Utf8LineStarts
    {
        get
        {
            if (_utf8LineStarts is not null)
                return _utf8LineStarts;

            int[] starts = new int[_lineStarts.Length];
            int total = 0;

            for (int i = 0; i < _lineStarts.Length; i++)
            {
                starts[i] = total;

                int start = _lineStarts[i];
                int end = i + 1 < _lineStarts.Length ? _lineStarts[i + 1] : Content.Length;
                total += Encoding.UTF8.GetByteCount(Content.AsSpan(start, end - start));
            }

            _utf8LineStarts = starts;
            return starts;
        }
    }

    /// <summary>Converts a zero-based line and character to an offset. Values past the end clamp rather than throw.</summary>
    public int GetOffset(int line, int character, PositionEncoding encoding)
        => GetOffset(new LinePosition(line, character), encoding);

    // -- internals ---------------------------------------------------------

    /// <summary>End of the line's content, before any terminator.</summary>
    private int GetLineEnd(int line)
    {
        int start = _lineStarts[line];

        if (line + 1 >= _lineStarts.Length)
            return Content.Length;

        int end = _lineStarts[line + 1];

        // Walk back over the terminator: \n, \r\n, or a lone \r.
        if (end > start && Content[end - 1] == '\n')
            end--;
        if (end > start && Content[end - 1] == '\r')
            end--;

        return end;
    }

    private bool IsSurrogatePairAt(int offset, int limit)
        => offset + 1 < limit
           && char.IsHighSurrogate(Content[offset])
           && char.IsLowSurrogate(Content[offset + 1]);

    private static int[] ComputeLineStarts(string content)
    {
        List<int> starts = new() { 0 };

        int i = 0;
        while (i < content.Length)
        {
            char c = content[i];

            if (c == '\r')
            {
                i++;
                if (i < content.Length && content[i] == '\n')
                    i++;
                starts.Add(i);
            }
            else if (c == '\n')
            {
                i++;
                starts.Add(i);
            }
            else
            {
                i++;
            }
        }

        return starts.ToArray();
    }
}
