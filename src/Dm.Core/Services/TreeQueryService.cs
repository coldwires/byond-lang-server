using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Symbols;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>One type in a browse response.</summary>
/// <remarks>
/// <see cref="ChildCount"/> is the number of children the type has in the tree, which is what lets a
/// panel draw an expander arrow without asking for the children first. It counts what exists rather
/// than what <see cref="Children"/> holds, so a depth-limited response still says whether there is
/// more below.
/// </remarks>
public sealed class TreeNode
{
    public TreeNode(
        string path,
        string name,
        bool declared,
        bool builtin,
        string? parentType,
        int childCount,
        int varCount,
        int procCount,
        IReadOnlyList<TreeNode> children)
    {
        Path = path;
        Name = name;
        Declared = declared;
        Builtin = builtin;
        ParentType = parentType;
        ChildCount = childCount;
        VarCount = varCount;
        ProcCount = procCount;
        Children = children;
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>
    /// False for a node that exists only because something deeper was declared — <c>/obj/item/sword</c>
    /// alone brings <c>/obj/item</c> into being.
    /// </summary>
    public bool Declared { get; }

    public bool Builtin { get; }

    /// <summary>Where this type inherits from, which is not always its path parent.</summary>
    public string? ParentType { get; }

    public int ChildCount { get; }

    public int VarCount { get; }

    public int ProcCount { get; }

    public IReadOnlyList<TreeNode> Children { get; }

    public override string ToString() => Path;
}

/// <summary>One var or proc in a members response.</summary>
public sealed class MemberEntry
{
    public MemberEntry(
        string name,
        string detail,
        SymbolKind kind,
        bool builtin,
        bool inherited,
        string owner,
        string file,
        TextSpan span,
        TextSpan nameSpan)
    {
        Name = name;
        Detail = detail;
        Kind = kind;
        Builtin = builtin;
        Inherited = inherited;
        Owner = owner;
        File = file;
        Span = span;
        NameSpan = nameSpan;
    }

    public string Name { get; }

    /// <summary>The declaration as written: a signature for a proc, a declared type for a var.</summary>
    public string Detail { get; }

    public SymbolKind Kind { get; }

    public bool Builtin { get; }

    /// <summary>True when the member comes from an ancestor rather than the type asked about.</summary>
    public bool Inherited { get; }

    /// <summary>The type the member is declared on, which is what <see cref="Inherited"/> points at.</summary>
    public string Owner { get; }

    public string File { get; }

    public TextSpan Span { get; }

    public TextSpan NameSpan { get; }

    public override string ToString() => $"{Owner} {Name}";
}

/// <summary>A flat subtype listing, and whether the cap cut it short.</summary>
/// <remarks>
/// <see cref="Truncated"/> is reported rather than left for the caller to infer from the count,
/// because a list that happens to be exactly as long as the limit is indistinguishable from one that
/// was cut. A picker that silently shows the first 500 of 4,000 subtypes reads as "there are 500".
/// </remarks>
public sealed class SubtypeListing
{
    public SubtypeListing(IReadOnlyList<TreeNode> types, bool truncated)
    {
        Types = types;
        Truncated = truncated;
    }

    public IReadOnlyList<TreeNode> Types { get; }

    public bool Truncated { get; }
}

/// <summary>A type's members, as one response.</summary>
public sealed class TypeMembers
{
    public TypeMembers(string path, IReadOnlyList<MemberEntry> vars, IReadOnlyList<MemberEntry> procs)
    {
        Path = path;
        Vars = vars;
        Procs = procs;
    }

    public string Path { get; }

    public IReadOnlyList<MemberEntry> Vars { get; }

    public IReadOnlyList<MemberEntry> Procs { get; }
}

/// <summary>
/// Bulk questions about the object tree, for the panels an IDE draws beside the editor.
/// </summary>
/// <remarks>
/// <para>
/// These are the queries that do not fit the position-shaped calls: a tree browser asks about a path
/// rather than a caret, and it asks for a lot at once. They are value-shaped for the same reason
/// every other service is — the answers cross the C ABI as JSON and will cross LSP as
/// <c>dm/objectTree</c> and friends, so nothing here may hand back a live symbol.
/// </para>
/// <para>
/// Depth and result caps are the caller's, with sane defaults, because the honest answer to "the
/// whole tree" on /tg/station is 45,000 nodes and a panel that asked for it by accident should get a
/// bounded response rather than a hang.
/// </para>
/// </remarks>
public static class TreeQueryService
{
    /// <summary>How many levels a browse returns when the caller does not say.</summary>
    public const int DefaultDepth = 1;

    /// <summary>How many entries a subtype listing returns when the caller does not say.</summary>
    public const int DefaultSubtypeLimit = 500;

    /// <summary>
    /// One type and its children, to the requested depth.
    /// </summary>
    /// <remarks>
    /// A depth of 1 is one level of children, which is what a tree panel needs to draw a node the
    /// user just expanded. Depth 0 is the node alone, and is how a panel refreshes one row.
    /// </remarks>
    public static TreeNode? Browse(
        ObjectTree tree,
        string path,
        int depth = DefaultDepth,
        bool includeBuiltins = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        TypeSymbol? type = Resolve(tree, path);

        return type is null ? null : Build(tree, type, depth, includeBuiltins, cancellationToken);
    }

    /// <summary>
    /// Every type beneath a path, flat and in tree order.
    /// </summary>
    /// <remarks>
    /// Flat because the callers that want this — "what can I place here", a subtype picker — want a
    /// list rather than a shape. The root itself is not included; a subtype listing of <c>/obj</c>
    /// that begins with <c>/obj</c> reads as a bug in the picker.
    /// </remarks>
    public static SubtypeListing? Subtypes(
        ObjectTree tree,
        string path,
        int limit = DefaultSubtypeLimit,
        bool includeBuiltins = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        TypeSymbol? type = Resolve(tree, path);

        if (type is null)
            return null;

        if (limit <= 0)
            limit = DefaultSubtypeLimit;

        List<TreeNode> results = new();
        bool truncated = false;

        Walk(type);
        return new SubtypeListing(results, truncated);

        void Walk(TypeSymbol current)
        {
            foreach (TypeSymbol child in current.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (results.Count >= limit)
                {
                    truncated = true;
                    return;
                }

                if (!includeBuiltins && child.IsBuiltin)
                    continue;

                results.Add(Build(tree, child, depth: 0, includeBuiltins, cancellationToken));
                Walk(child);
            }
        }
    }

    /// <summary>
    /// A type's vars and procs, optionally including everything it inherits.
    /// </summary>
    /// <remarks>
    /// Inherited members walk <see cref="ObjectTree.InheritanceChain"/> rather than the path
    /// ancestry, since <c>parent_type</c> can point anywhere and <c>/mob</c> inherits from
    /// <c>/atom/movable</c> despite being a child of the root by path. A name declared on the type
    /// hides the same name on an ancestor, which is what the compiler does.
    /// </remarks>
    public static TypeMembers? Members(
        ObjectTree tree,
        string path,
        bool inherited = true,
        bool includeBuiltins = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        TypeSymbol? type = Resolve(tree, path);

        if (type is null)
            return null;

        List<MemberEntry> vars = new();
        List<MemberEntry> procs = new();
        HashSet<string> seenVars = new(StringComparer.Ordinal);
        HashSet<string> seenProcs = new(StringComparer.Ordinal);

        foreach (TypeSymbol current in inherited ? tree.InheritanceChain(type) : new[] { type })
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool fromAncestor = !ReferenceEquals(current, type);

            foreach (VarSymbol variable in current.Vars)
            {
                if (!includeBuiltins && variable.IsBuiltin)
                    continue;

                // The nearest declaration wins, so a later ancestor cannot replace it.
                if (!seenVars.Add(variable.Name))
                    continue;

                vars.Add(new MemberEntry(
                    variable.Name,
                    variable.DeclaredType is { } declared ? declared.Text : string.Empty,
                    SymbolKind.Variable,
                    variable.IsBuiltin,
                    fromAncestor,
                    current.Path.Text,
                    variable.Site.File,
                    variable.Site.Span,
                    variable.Site.NameSpan));
            }

            foreach (ProcSymbol proc in current.Procs)
            {
                if (!includeBuiltins && proc.IsBuiltin)
                    continue;

                if (!seenProcs.Add(proc.Name))
                    continue;

                DeclarationSite site = proc.Sites.Count > 0 ? proc.Sites[0] : default;

                procs.Add(new MemberEntry(
                    proc.Name,
                    $"{proc.Name}({string.Join(", ", proc.Parameters)})",
                    proc.IsVerb ? SymbolKind.Verb : SymbolKind.Proc,
                    proc.IsBuiltin,
                    fromAncestor,
                    current.Path.Text,
                    site.File ?? string.Empty,
                    site.Span,
                    site.NameSpan));
            }
        }

        return new TypeMembers(type.Path.Text, vars, procs);
    }

    /// <summary>Resolves a path, treating an empty one and <c>/</c> as the root.</summary>
    private static TypeSymbol? Resolve(ObjectTree tree, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return tree.Root;

        return tree.Find(path);
    }

    private static TreeNode Build(
        ObjectTree tree,
        TypeSymbol type,
        int depth,
        bool includeBuiltins,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<TreeNode> children = new();
        int childCount = 0;

        foreach (TypeSymbol child in type.Children)
        {
            if (!includeBuiltins && child.IsBuiltin)
                continue;

            childCount++;

            if (depth > 0)
                children.Add(Build(tree, child, depth - 1, includeBuiltins, cancellationToken));
        }

        int varCount = 0;
        int procCount = 0;

        foreach (VarSymbol variable in type.Vars)
        {
            if (includeBuiltins || !variable.IsBuiltin)
                varCount++;
        }

        foreach (ProcSymbol proc in type.Procs)
        {
            if (includeBuiltins || !proc.IsBuiltin)
                procCount++;
        }

        return new TreeNode(
            type.Path.Text,
            type.Path.IsRoot ? "/" : type.Name,
            type.IsDeclared,
            type.IsBuiltin,
            tree.InheritanceParent(type)?.Path.Text,
            childCount,
            varCount,
            procCount,
            children);
    }
}
