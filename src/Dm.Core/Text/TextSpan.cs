using System;

namespace Dm.Core.Text;

/// <summary>
/// A half-open range of UTF-16 code units within a <see cref="SourceText"/>.
/// </summary>
/// <remarks>
/// Offsets are UTF-16 because that is the native indexing of a .NET string and of the
/// <see cref="SourceText"/> content. Conversion to UTF-8 byte columns happens only at the
/// boundary, in <see cref="SourceText.GetLinePosition(int)"/>.
/// </remarks>
public readonly struct TextSpan : IEquatable<TextSpan>
{
    /// <summary>Creates a span from a start offset and length. Both must be non-negative.</summary>
    public TextSpan(int start, int length)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), start, "start must not be negative");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "length must not be negative");

        Start = start;
        Length = length;
    }

    /// <summary>First UTF-16 code-unit offset covered by the span.</summary>
    public int Start { get; }

    /// <summary>Extent of the span in UTF-16 code units.</summary>
    public int Length { get; }

    /// <summary>Exclusive end offset: the first code unit after the span.</summary>
    public int End => Start + Length;

    /// <summary>True when the span covers no code units.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>Builds a span from a start and an exclusive end. <paramref name="end"/> must not precede <paramref name="start"/>.</summary>
    public static TextSpan FromBounds(int start, int end)
    {
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(end), end, "end must not precede start");

        return new TextSpan(start, end - start);
    }

    /// <summary>True if <paramref name="offset"/> falls inside this span.</summary>
    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>
    /// True if <paramref name="offset"/> falls inside this span or sits exactly at its end.
    /// </summary>
    /// <remarks>
    /// This is the one to use for cursor positions. A caret immediately after the last character
    /// of an identifier is still "in" that identifier as far as completion and hover are concerned.
    /// </remarks>
    public bool ContainsOrTouches(int offset) => offset >= Start && offset <= End;

    /// <summary>True when the spans share at least one code unit. An empty span overlaps nothing.</summary>
    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;

    /// <summary>Value equality on start and length.</summary>
    public bool Equals(TextSpan other) => Start == other.Start && Length == other.Length;

    /// <summary>Value equality.</summary>
    public override bool Equals(object? obj) => obj is TextSpan other && Equals(other);

    /// <summary>Hash of start and length.</summary>
    public override int GetHashCode() => HashCode.Combine(Start, Length);

    /// <summary>Formats as <c>[start..end)</c>, end exclusive.</summary>
    public override string ToString() => $"[{Start}..{End})";

    /// <summary>Value equality.</summary>
    public static bool operator ==(TextSpan left, TextSpan right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(TextSpan left, TextSpan right) => !left.Equals(right);
}
