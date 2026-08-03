using System;
using System.Collections.Generic;

namespace Dm.Core.Symbols;

/// <summary>
/// A normalised absolute path to a node in the type tree, such as <c>/obj/item</c>.
/// </summary>
/// <remarks>
/// <para>
/// Always absolute and always normalised to <c>/</c> separators, because mid-path <c>.</c> and
/// <c>/</c> are the same token (PLAN.md §4a) — <c>/obj.item</c> and <c>/obj/item</c> must key the
/// same node. Normalising at construction means every later comparison is an ordinal string
/// compare.
/// </para>
/// <para>
/// Resolution is <b>path-keyed, never name-keyed</b>. <c>/mob/client</c> is a subtype of
/// <c>/mob</c> that happens to be called <c>client</c>; it has nothing to do with the builtin
/// <c>/client</c>, and keying by the last segment would silently merge them.
/// </para>
/// <para>
/// PLAN.md calls for an interned value type. This holds the normalised string instead, which gives
/// the same equality semantics without a table to thread through or keep alive. Interning is a
/// performance change, so it belongs with the rest of M9 and only if profiling asks for it.
/// </para>
/// </remarks>
public readonly struct TypePath : IEquatable<TypePath>, IComparable<TypePath>
{
    private readonly string? _text;

    private TypePath(string text) => _text = text;

    /// <summary>The root of the tree, written <c>/</c>. Every declared type descends from it.</summary>
    public static TypePath Root => new("/");

    /// <summary>The normalised path text, always beginning with <c>/</c>.</summary>
    public string Text => _text ?? "/";

    public bool IsRoot => Text.Length == 1;

    /// <summary>The last segment, which is the type's own name. Empty for the root.</summary>
    public string Name
    {
        get
        {
            string text = Text;
            int slash = text.LastIndexOf('/');
            return slash < 0 || slash == text.Length - 1 ? string.Empty : text[(slash + 1)..];
        }
    }

    /// <summary>The enclosing path. The root's parent is itself, so walking up always terminates.</summary>
    public TypePath Parent
    {
        get
        {
            string text = Text;
            int slash = text.LastIndexOf('/');
            return slash <= 0 ? Root : new TypePath(text[..slash]);
        }
    }

    /// <summary>Builds a path from segments, which may use either separator or none.</summary>
    public static TypePath FromSegments(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
            return Root;

        System.Text.StringBuilder text = new();

        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
                continue;

            text.Append('/');
            text.Append(segment);
        }

        return text.Length == 0 ? Root : new TypePath(text.ToString());
    }

    /// <summary>Parses written text, accepting <c>.</c> and <c>/</c> interchangeably.</summary>
    public static TypePath Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Root;

        List<string> segments = new();
        int start = 0;

        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] is '/' or '.')
            {
                if (i > start)
                    segments.Add(text[start..i]);

                start = i + 1;
            }
        }

        return FromSegments(segments);
    }

    /// <summary>Appends one segment, as indentation does when it nests a declaration.</summary>
    public TypePath Append(string segment)
        => string.IsNullOrEmpty(segment) ? this : new TypePath(IsRoot ? "/" + segment : Text + "/" + segment);

    public TypePath Append(IReadOnlyList<string> segments)
    {
        TypePath path = this;

        foreach (string segment in segments)
            path = path.Append(segment);

        return path;
    }

    /// <summary>True when this path is the other one or sits beneath it.</summary>
    public bool IsUnder(TypePath other)
    {
        if (other.IsRoot)
            return true;

        string text = Text;
        string prefix = other.Text;

        return text.Length >= prefix.Length
               && text.StartsWith(prefix, StringComparison.Ordinal)
               && (text.Length == prefix.Length || text[prefix.Length] == '/');
    }

    public IReadOnlyList<string> Segments
        => IsRoot ? Array.Empty<string>() : Text[1..].Split('/');

    public bool Equals(TypePath other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TypePath other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public int CompareTo(TypePath other) => string.CompareOrdinal(Text, other.Text);

    public static bool operator ==(TypePath left, TypePath right) => left.Equals(right);

    public static bool operator !=(TypePath left, TypePath right) => !left.Equals(right);

    public override string ToString() => Text;
}
