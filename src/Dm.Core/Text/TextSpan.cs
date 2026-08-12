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
    public TextSpan(int start, int length)
    {
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start), start, "start must not be negative");
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "length must not be negative");

        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }

    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

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

    public bool OverlapsWith(TextSpan other) => Start < other.End && other.Start < End;

    public bool Equals(TextSpan other) => Start == other.Start && Length == other.Length;

    public override bool Equals(object? obj) => obj is TextSpan other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Start, Length);

    public override string ToString() => $"[{Start}..{End})";

    public static bool operator ==(TextSpan left, TextSpan right) => left.Equals(right);

    public static bool operator !=(TextSpan left, TextSpan right) => !left.Equals(right);
}
