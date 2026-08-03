using System.Collections.Generic;

namespace Dm.Core.Symbols;

/// <summary>
/// Every type in a project, merged from every file that contributed to it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes <c>mob.</c> answerable. A completion needs every proc and var on
/// <c>/mob</c>, plus everything inherited, plus the BYOND builtins — all of which are lookups here
/// rather than type inference.
/// </para>
/// <para>
/// Nodes are created on demand: declaring <c>/obj/item/sword</c> brings <c>/obj</c> and
/// <c>/obj/item</c> into being even if nothing declared them, which is what DM does.
/// </para>
/// </remarks>
public sealed class ObjectTree
{
    private readonly Dictionary<TypePath, TypeSymbol> _types = new();

    public ObjectTree()
    {
        Root = new TypeSymbol(TypePath.Root, null);
        _types.Add(TypePath.Root, Root);
    }

    public TypeSymbol Root { get; }

    public int Count => _types.Count;

    public IReadOnlyCollection<TypeSymbol> Types => _types.Values;

    public TypeSymbol? Find(TypePath path) => _types.GetValueOrDefault(path);

    public TypeSymbol? Find(string path) => Find(TypePath.Parse(path));

    /// <summary>Returns the node for a path, creating it and every ancestor as needed.</summary>
    public TypeSymbol GetOrAdd(TypePath path)
    {
        if (_types.TryGetValue(path, out TypeSymbol? existing))
            return existing;

        TypeSymbol parent = path.IsRoot ? Root : GetOrAdd(path.Parent);
        TypeSymbol type = new(path, parent);

        _types.Add(path, type);
        parent.AddChild(type);

        return type;
    }

    /// <summary>
    /// The type a member lookup should continue into, following <c>parent_type</c> when one was
    /// declared and the path otherwise.
    /// </summary>
    /// <remarks>
    /// Resolved here rather than stored on the symbol because a <c>parent_type</c> can name a type
    /// declared later in include order than the type naming it.
    /// </remarks>
    public TypeSymbol? InheritanceParent(TypeSymbol type)
    {
        if (type.ParentType is { } explicitParent)
            return Find(explicitParent);

        return type.Parent;
    }

    /// <summary>
    /// Walks a type and everything it inherits from, nearest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The root is <b>not</b> part of the chain. It holds the global procs, and global scope is not
    /// a base type: <c>istype(x)</c> is a call, while <c>mob.istype()</c> is not valid DM. Ending
    /// the walk at the last real type is what keeps a completion list after <c>mob.</c> from
    /// offering every global proc in the language.
    /// </para>
    /// <para>
    /// Guards against a cycle. <c>parent_type</c> is an ordinary assignment, so nothing stops a
    /// project from writing one that loops, and an editor must not hang on it.
    /// </para>
    /// </remarks>
    public IEnumerable<TypeSymbol> InheritanceChain(TypeSymbol type)
    {
        HashSet<TypePath> seen = new();
        TypeSymbol? current = type;

        while (current is not null && !current.Path.IsRoot && seen.Add(current.Path))
        {
            yield return current;
            current = InheritanceParent(current);
        }

        // Asking for the root's own members is still meaningful — that is where globals live.
        if (type.Path.IsRoot)
            yield return Root;
    }

    /// <summary>Finds a var on a type or anything it inherits from.</summary>
    public VarSymbol? ResolveVar(TypeSymbol type, string name)
    {
        foreach (TypeSymbol candidate in InheritanceChain(type))
        {
            if (candidate.FindVar(name) is { } variable)
                return variable;
        }

        return null;
    }

    /// <summary>Finds a proc on a type or anything it inherits from.</summary>
    public ProcSymbol? ResolveProc(TypeSymbol type, string name)
    {
        foreach (TypeSymbol candidate in InheritanceChain(type))
        {
            if (candidate.FindProc(name) is { } proc)
                return proc;
        }

        return null;
    }
}
