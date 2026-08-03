using System;

namespace Dm.Core.Text;

/// <summary>
/// How the character component of a <see cref="LinePosition"/> is measured.
/// </summary>
/// <remarks>
/// The two shells disagree, so this is never implicit. LSP's default is UTF-16 code units; the
/// C ABI hands out UTF-8 byte offsets. Every service that accepts or returns a position takes
/// this explicitly — see PLAN.md §4.
/// </remarks>
public enum PositionEncoding
{
    /// <summary>UTF-8 bytes from the start of the line. Used by the C ABI.</summary>
    Utf8,

    /// <summary>UTF-16 code units from the start of the line. LSP's default.</summary>
    Utf16,
}

/// <summary>
/// A zero-based line and character position.
/// </summary>
/// <remarks>
/// Line/character rather than a raw offset is what makes line-ending style irrelevant across the
/// boundary: <c>\r</c> is always a terminator and never sits inside a line, so it cannot shift a
/// character index. See PLAN.md §4b.
/// </remarks>
public readonly struct LinePosition : IEquatable<LinePosition>, IComparable<LinePosition>
{
    public LinePosition(int line, int character)
    {
        if (line < 0)
            throw new ArgumentOutOfRangeException(nameof(line), line, "line must not be negative");
        if (character < 0)
            throw new ArgumentOutOfRangeException(nameof(character), character, "character must not be negative");

        Line = line;
        Character = character;
    }

    public int Line { get; }

    public int Character { get; }

    public bool Equals(LinePosition other) => Line == other.Line && Character == other.Character;

    public override bool Equals(object? obj) => obj is LinePosition other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Line, Character);

    public int CompareTo(LinePosition other)
    {
        int byLine = Line.CompareTo(other.Line);
        return byLine != 0 ? byLine : Character.CompareTo(other.Character);
    }

    public override string ToString() => $"{Line}:{Character}";

    public static bool operator ==(LinePosition left, LinePosition right) => left.Equals(right);

    public static bool operator !=(LinePosition left, LinePosition right) => !left.Equals(right);

    public static bool operator <(LinePosition left, LinePosition right) => left.CompareTo(right) < 0;

    public static bool operator >(LinePosition left, LinePosition right) => left.CompareTo(right) > 0;

    public static bool operator <=(LinePosition left, LinePosition right) => left.CompareTo(right) <= 0;

    public static bool operator >=(LinePosition left, LinePosition right) => left.CompareTo(right) >= 0;
}
