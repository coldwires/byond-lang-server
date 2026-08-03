using System.Collections.Generic;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>How a path begins, which decides how it resolves.</summary>
/// <remarks>
/// See PLAN.md §4a. The three anchors mean genuinely different things and must not be collapsed:
/// a leading <c>/</c> is absolute from root, a leading <c>.</c> is an upward search through the code
/// tree, and no leading separator means the path is relative to the enclosing indentation.
/// </remarks>
public enum PathAnchor
{
    /// <summary>No leading separator; relative to the enclosing block.</summary>
    Relative,

    /// <summary>Leading <c>/</c>; absolute from root, ignoring indentation.</summary>
    Absolute,

    /// <summary>Leading <c>.</c>; an upward search from the current position.</summary>
    UpwardSearch,
}

/// <summary>A dotted or slashed path such as <c>/obj/item/sword</c> or <c>mob/proc/attack</c>.</summary>
/// <remarks>
/// Mid-path, <c>/</c> and <c>.</c> are the same token, so the segments are stored without recording
/// which separator was written. The anchor is kept because leading position is where they differ.
/// </remarks>
public sealed class PathSyntax
{
    public PathSyntax(PathAnchor anchor, IReadOnlyList<string> segments, TextSpan span, IReadOnlyList<TextSpan> segmentSpans)
    {
        Anchor = anchor;
        Segments = segments;
        Span = span;
        SegmentSpans = segmentSpans;
    }

    public PathAnchor Anchor { get; }

    public IReadOnlyList<string> Segments { get; }

    /// <summary>Span of each segment, so an editor can navigate to one part of a path.</summary>
    public IReadOnlyList<TextSpan> SegmentSpans { get; }

    public TextSpan Span { get; }

    public bool IsEmpty => Segments.Count == 0;

    public string Text => (Anchor == PathAnchor.Absolute ? "/" : Anchor == PathAnchor.UpwardSearch ? "." : string.Empty)
                          + string.Join("/", Segments);

    public override string ToString() => Text;
}

public abstract class SyntaxNode
{
    protected SyntaxNode(TextSpan span) => Span = span;

    /// <summary>Span in the file this node was parsed from.</summary>
    public TextSpan Span { get; }
}

/// <summary>Everything declared in one file.</summary>
public sealed class FileSyntax : SyntaxNode
{
    public FileSyntax(IReadOnlyList<DeclarationSyntax> declarations, TextSpan span) : base(span)
        => Declarations = declarations;

    public IReadOnlyList<DeclarationSyntax> Declarations { get; }
}

public abstract class DeclarationSyntax : SyntaxNode
{
    protected DeclarationSyntax(PathSyntax path, TextSpan span) : base(span) => Path = path;

    public PathSyntax Path { get; }

    /// <summary>The last path segment, which is the declared name.</summary>
    public string Name => Path.Segments.Count > 0 ? Path.Segments[^1] : string.Empty;

    /// <summary>Span of the declared name, for go-to-definition and rename.</summary>
    public TextSpan NameSpan => Path.SegmentSpans.Count > 0 ? Path.SegmentSpans[^1] : Span;
}

/// <summary>A type node such as <c>/obj/item</c>, possibly with members indented beneath it.</summary>
public sealed class TypeDeclarationSyntax : DeclarationSyntax
{
    public TypeDeclarationSyntax(PathSyntax path, IReadOnlyList<DeclarationSyntax> members, TextSpan span)
        : base(path, span)
        => Members = members;

    public IReadOnlyList<DeclarationSyntax> Members { get; }
}

/// <summary>A <c>var</c> declaration, or a group of them under one <c>var/</c>.</summary>
/// <remarks>
/// One <c>var/</c> can introduce several names — <c>var/a = 1, b = 2</c> — so a group is modelled as
/// a parent with children rather than as separate declarations, which keeps the outline readable.
/// </remarks>
public sealed class VarDeclarationSyntax : DeclarationSyntax
{
    public VarDeclarationSyntax(
        PathSyntax path,
        IReadOnlyList<string> modifiers,
        PathSyntax? declaredType,
        bool hasInitializer,
        IReadOnlyList<VarDeclarationSyntax> siblings,
        TextSpan span)
        : base(path, span)
    {
        Modifiers = modifiers;
        DeclaredType = declaredType;
        HasInitializer = hasInitializer;
        Siblings = siblings;
    }

    /// <summary>Modifiers found inside the path: <c>const</c>, <c>tmp</c>, <c>global</c>, <c>static</c>, <c>final</c>.</summary>
    public IReadOnlyList<string> Modifiers { get; }

    /// <summary>
    /// The declared type, or null when untyped. For <c>var/mob/test/t</c> this is <c>/mob/test</c>.
    /// </summary>
    public PathSyntax? DeclaredType { get; }

    public bool HasInitializer { get; }

    /// <summary>Further names declared under the same <c>var/</c>.</summary>
    public IReadOnlyList<VarDeclarationSyntax> Siblings { get; }
}

/// <summary>A proc or verb declaration.</summary>
public sealed class ProcDeclarationSyntax : DeclarationSyntax
{
    public ProcDeclarationSyntax(
        PathSyntax path,
        IReadOnlyList<ParameterSyntax> parameters,
        bool isVerb,
        bool isNewDeclaration,
        TextSpan span)
        : base(path, span)
    {
        Parameters = parameters;
        IsVerb = isVerb;
        IsNewDeclaration = isNewDeclaration;
    }

    public IReadOnlyList<ParameterSyntax> Parameters { get; }

    /// <summary>True when declared under a <c>verb</c> segment rather than <c>proc</c>.</summary>
    public bool IsVerb { get; }

    /// <summary>
    /// True when the path contains a <c>proc</c> or <c>verb</c> segment, which declares a new proc.
    /// Without it the declaration is an override of an inherited one — see PLAN.md §4a.
    /// </summary>
    public bool IsNewDeclaration { get; }
}

/// <summary>One parameter in a proc or verb signature.</summary>
public sealed class ParameterSyntax : SyntaxNode
{
    public ParameterSyntax(string name, PathSyntax? declaredType, string? inputType, bool hasDefault, TextSpan span)
        : base(span)
    {
        Name = name;
        DeclaredType = declaredType;
        InputType = inputType;
        HasDefault = hasDefault;
    }

    public string Name { get; }

    /// <summary>Type from the path form, as in <c>mob/M</c>.</summary>
    public PathSyntax? DeclaredType { get; }

    /// <summary>The <c>as</c> clause, if written.</summary>
    public string? InputType { get; }

    public bool HasDefault { get; }

    public override string ToString() => DeclaredType is null ? Name : $"{DeclaredType}/{Name}";
}
