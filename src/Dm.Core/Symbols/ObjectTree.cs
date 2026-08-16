using System;
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
    /// <summary>
    /// Which warnings <c>#pragma ignore</c> silenced in this project, and where.
    /// </summary>
    /// <remarks>
    /// Carried here rather than passed to <see cref="Binding.Binder"/> because every caller of the
    /// binder already holds the tree the same walk produced, so this reaches all four of them —
    /// the ABI, the LSP, the CLI and the reference index — without a signature change apiece.
    /// Null on a tree built without a walk, such as a unit test's, where nothing is suppressed.
    /// </remarks>
    internal Preprocessing.PragmaLevels? SuppressedWarnings { get; set; }

    /// <summary>
    /// Reads the state names out of a <c>.dmi</c>, given the resource path as the source writes it
    /// — <c>icons/mob.dmi</c>. Null when nothing supplied one, which is the ordinary case.
    /// </summary>
    /// <remarks>
    /// Injected rather than called directly because <c>Dm.Core</c> does not reference
    /// <c>Dm.Assets</c>: the reader lives in whichever shell is hosting, and <see cref="Workspace"/>
    /// is what turns a relative resource path into an absolute one, since it owns the root.
    ///
    /// It rides here for the same reason the pragma levels do — every caller already holds the tree
    /// the walk produced, so one property reaches the ABI, the LSP and the CLI without four
    /// signature changes and four chances to forget one.
    /// </remarks>
    public System.Func<string, System.Collections.Generic.IReadOnlyList<string>>? IconStates { get; set; }

    private readonly Dictionary<TypePath, TypeSymbol> _types = new();

    /// <summary>Creates a tree holding only the root node.</summary>
    public ObjectTree()
    {
        Root = new TypeSymbol(TypePath.Root, null);
        _types.Add(TypePath.Root, Root);
    }

    /// <summary>The <c>/</c> node. Holds the global procs and vars; not part of any inheritance chain.</summary>
    public TypeSymbol Root { get; }

    /// <summary>Number of nodes, the root included.</summary>
    public int Count => _types.Count;

    /// <summary>Every node, the root included, in no guaranteed order.</summary>
    public IReadOnlyCollection<TypeSymbol> Types => _types.Values;

    /// <summary>The node at a path, or null when nothing brought it into being.</summary>
    public TypeSymbol? Find(TypePath path) => _types.GetValueOrDefault(path);

    /// <summary>Parses written text and finds its node, or null when nothing brought it into being.</summary>
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

        // `parent_type = .sibling` searches upward from this type's own path, so it can only be
        // resolved once the tree is complete. An unresolvable one yields no parent rather than
        // falling back to the path parent, matching what an unresolvable absolute one does: the
        // author asked for a specific type, and quietly substituting a different one would put
        // members on the type that it does not have.
        if (type.RelativeParentType is { } relative)
        {
            return RelativePath.Resolve(this, type.Path, relative) is { } resolved
                ? Find(resolved)
                : null;
        }

        // A type the project declares at root level derives from /datum, which nothing in its path
        // says. Compiler-verified on 516.1666: `src.type`, `src.tag` and `src.vars` all resolve
        // inside a bare `/market_values`, while a name no type declares still errors — so the
        // members are genuinely inherited rather than the check being switched off.
        //
        // Restricted to types the project declares, because the builtins carry their own verified
        // links and three of them (/client, /list, /savefile) genuinely have no parent at all. A
        // blanket rule would invent one for those, and for /world, whose parent was never probed.
        //
        // Without this a root-level datum offers no `type`, `tag` or `vars`, which is how the binder
        // came to report two undefined vars on a project that compiles clean.
        // A one-segment path's parent is the root, and the root is deliberately outside the
        // inheritance chain, so these types currently inherit nothing at all.
        if (type.Path.Segments.Count == 1 && type.Sites.Count > 0 && type.Path != DatumPath)
            return Find(DatumPath);

        return type.Parent;
    }

    private static readonly TypePath DatumPath = TypePath.Parse("/datum");

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

    // The (type, name) pairs whose declaration deserves dm.exe's "previous definition" line
    // because a DESCENDANT type re-declares the name. Built lazily in one pass over the tree —
    // the alternative is a subtree scan per bound declaration, which is the cost the check's
    // cross-file half was deferred over at M11. A TypeSymbol is fresh per tree, so the cache
    // cannot go stale. The walks mirror the binder's descendant-side checks exactly: the nearest
    // ancestor carrying the name decides, a builtin there means the descendant reports a builtin
    // conflict and no previous line exists, and a merely-overriding ancestor is walked past.
    private HashSet<(TypePath Owner, string Name)>? _procsRedeclaredBelow;
    private HashSet<(TypePath Owner, string Name)>? _varsRedeclaredBelow;

    /// <summary>Whether a descendant type re-declares this proc with a <c>proc/</c> segment.</summary>
    internal bool ProcRedeclaredBelow(TypePath owner, string name)
    {
        EnsureRedeclarationIndex();
        return _procsRedeclaredBelow!.Contains((owner, name));
    }

    /// <summary>
    /// The nearest ancestor of <paramref name="type"/> whose <paramref name="procName"/> this
    /// type's own definition overrides: its path, and whether that definition is BYOND's own.
    /// Null when nothing above declares the proc (or the type is unknown), i.e. not an override.
    /// </summary>
    public (TypePath Owner, bool IsBuiltin)? FindOverriddenProc(TypePath type, string procName)
    {
        if (Find(type) is not { } t)
            return null;

        // Redefining the type's OWN built-in (mob/Login(), world/New()) is the most common
        // override of all, and it never involves an ancestor: the builtin and the user's
        // definition share the symbol.
        // SITES, not DeclaringCount: DeclaringCount counts only declarations that wrote a `proc/`
        // segment, and a bare override never does - so keying on it excluded exactly the case
        // this branch exists for. A seeded builtin carries no sites at all, so any site here is
        // the project's own definition. Caught by A_bare_override_of_a_builtin_is_an_override,
        // which returned null against the first version.
        if (t.FindProc(procName) is { IsBuiltin: true, Sites.Count: > 0 })
            return (t.Path, true);

        foreach (TypeSymbol ancestor in InheritanceChain(t))
        {
            if (ReferenceEquals(ancestor, t) || ancestor.FindProc(procName) is not { } above)
                continue;

            if (above.IsBuiltin)
                return (ancestor.Path, true);

            if (above.DeclaringCount > 0)
                return (ancestor.Path, false);
        }

        return null;
    }

    /// <summary>Whether a descendant type re-declares this var with a <c>var/</c> segment.</summary>
    internal bool VarRedeclaredBelow(TypePath owner, string name)
    {
        EnsureRedeclarationIndex();
        return _varsRedeclaredBelow!.Contains((owner, name));
    }

    private void EnsureRedeclarationIndex()
    {
        if (_procsRedeclaredBelow is not null)
            return;

        HashSet<(TypePath, string)> procs = new();
        HashSet<(TypePath, string)> vars = new();

        foreach (TypeSymbol type in Types)
        {
            foreach (ProcSymbol proc in type.Procs)
            {
                if (proc.DeclaringCount == 0 || proc.IsBuiltin)
                    continue;

                foreach (TypeSymbol ancestor in InheritanceChain(type))
                {
                    if (ReferenceEquals(ancestor, type)
                        || ancestor.FindProc(proc.Name) is not ProcSymbol above)
                    {
                        continue;
                    }

                    if (above.IsBuiltin)
                        break;

                    if (above.DeclaringCount > 0)
                    {
                        procs.Add((ancestor.Path, proc.Name));
                        break;
                    }
                }
            }

            foreach (VarSymbol variable in type.Vars)
            {
                if (type.VarDeclaringSites(variable.Name).Count == 0)
                    continue;

                foreach (TypeSymbol ancestor in InheritanceChain(type))
                {
                    if (ReferenceEquals(ancestor, type)
                        || ancestor.FindVar(variable.Name) is not { } above)
                    {
                        continue;
                    }

                    if (above.IsBuiltin)
                        break;

                    if (ancestor.VarDeclaringSites(variable.Name).Count > 0)
                    {
                        vars.Add((ancestor.Path, variable.Name));
                        break;
                    }
                }
            }
        }

        _varsRedeclaredBelow = vars;
        _procsRedeclaredBelow = procs;
    }

    /// <summary>
    /// The nearest DECLARED type a var of this name carries anywhere along the chain. A bare
    /// override on a subtype is an untyped <see cref="VarSymbol"/> shadowing the typed
    /// declaration above it — tgstation's bots override `ai_controller` per type while /atom
    /// declares it `/datum/ai_controller` — so the type is the chain's first non-null, not the
    /// first symbol's.
    /// </summary>
    internal TypePath? ResolveVarType(TypeSymbol type, string name)
    {
        foreach (TypeSymbol candidate in InheritanceChain(type))
        {
            if (candidate.FindVar(name) is { DeclaredType: { } declared })
                return declared;
        }

        return null;
    }

    // Every member name in the program, by kind. What `:` falls back to and what `?:` asks
    // outright - compiler-verified 2026-08-15: on an untyped receiver `x:hp` compiles when `hp`
    // is a member of ANYTHING, builtins included (`x:icon_state` compiles), while a name that is
    // only a PROC does not satisfy a var access (`x:only_a_proc` is "undefined var"). So the sets
    // are kept apart rather than merged.
    private HashSet<string>? _anyVarName;
    private HashSet<string>? _anyProcName;

    /// <summary>Whether any type in the program has a member of this name and kind.</summary>
    /// <remarks>
    /// The widest check DM performs. `:` on an untyped receiver asks exactly this, and so does
    /// `?:` on any receiver - which is NOT the same question `:` asks of a typed one, where the
    /// search is the declared type and its subtypes. `M?:elsewhere` compiles where `M:elsewhere`
    /// does not, which is the pair that pins the difference.
    /// </remarks>
    internal bool AnyMemberNamed(string name, bool isProc)
    {
        if (_anyVarName is null)
        {
            HashSet<string> vars = new(StringComparer.Ordinal);
            HashSet<string> procs = new(StringComparer.Ordinal);

            foreach (TypeSymbol type in Types)
            {
                foreach (VarSymbol variable in type.Vars)
                    vars.Add(variable.Name);

                foreach (ProcSymbol proc in type.Procs)
                    procs.Add(proc.Name);
            }

            _anyVarName = vars;
            _anyProcName = procs;
        }

        return isProc ? _anyProcName!.Contains(name) : _anyVarName.Contains(name);
    }

    // Inheritance children: the inverse of InheritanceParent, which the tree has never needed
    // before because every other question walks upward.
    private Dictionary<TypePath, List<TypeSymbol>>? _inheritanceChildren;

    /// <summary>Whether the type or anything INHERITING from it carries this member.</summary>
    /// <remarks>
    /// <para>
    /// The `:` widening: it checks the declared type and its subtypes, so a property declared
    /// only on a subtype is reachable through `:` and not through `.`.
    /// </para>
    /// <para>
    /// <b>Subtype means INHERITANCE, not path.</b> Probed 2026-08-15: a `/datum/adopted` carrying
    /// `parent_type = /mob/test` satisfies `M:only_there` on a `/mob/test` receiver, so walking
    /// path children would miss every type a project re-parents - and re-parenting is ordinary DM,
    /// which is why `/mob` itself descends from `/atom/movable` rather than from the root.
    /// </para>
    /// <para>
    /// Cycle-guarded for the same reason <see cref="InheritanceChain"/> is: `parent_type` is an
    /// ordinary assignment and nothing stops a project from writing a loop.
    /// </para>
    /// </remarks>
    internal bool AnyDescendantHasMember(TypeSymbol type, string name, bool isProc)
    {
        if (_inheritanceChildren is null)
        {
            Dictionary<TypePath, List<TypeSymbol>> children = new();

            foreach (TypeSymbol candidate in Types)
            {
                if (InheritanceParent(candidate) is not { } parent)
                    continue;

                if (!children.TryGetValue(parent.Path, out List<TypeSymbol>? bucket))
                    children[parent.Path] = bucket = new List<TypeSymbol>();

                bucket.Add(candidate);
            }

            _inheritanceChildren = children;
        }

        HashSet<TypePath> seen = new();
        Stack<TypeSymbol> pending = new();
        pending.Push(type);

        while (pending.Count > 0)
        {
            TypeSymbol current = pending.Pop();

            if (!seen.Add(current.Path))
                continue;

            if (isProc ? current.FindProc(name) is not null : current.FindVar(name) is not null)
                return true;

            if (_inheritanceChildren.TryGetValue(current.Path, out List<TypeSymbol>? below))
            {
                foreach (TypeSymbol child in below)
                    pending.Push(child);
            }
        }

        return false;
    }

    /// <summary>Finds a var on a type or anything it inherits from.</summary>
    internal VarSymbol? ResolveVar(TypeSymbol type, string name)
    {
        foreach (TypeSymbol candidate in InheritanceChain(type))
        {
            if (candidate.FindVar(name) is { } variable)
                return variable;
        }

        return null;
    }

    /// <summary>Finds a proc on a type or anything it inherits from.</summary>
    internal ProcSymbol? ResolveProc(TypeSymbol type, string name)
    {
        foreach (TypeSymbol candidate in InheritanceChain(type))
        {
            if (candidate.FindProc(name) is { } proc)
                return proc;
        }

        return null;
    }

    // Folded initialisers that name a const, memoised per var. Keyed by the VarSymbol itself:
    // the tree is rebuilt on any change, so the memo lives and dies with it. The in-progress set
    // is the cycle guard - `var/const/A = B` with `var/const/B = A` is a compile error in dm.exe
    // and a stack overflow here without it.
    private Dictionary<VarSymbol, Binding.ConstantEvaluator.Constant?>? _constants;
    private HashSet<VarSymbol>? _folding;

    /// <summary>
    /// What a var's initialiser comes to, with a name in it resolved to a <c>const</c> the var's
    /// owner can see, or empty. Supersedes <see cref="VarSymbol.ConstantValue"/> where the two
    /// differ; identical where the initialiser names nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-file fold answers <c>5 * 60</c> and cannot answer <c>TYPE_MAX - 5</c>, because
    /// <c>TYPE_MAX</c> may be declared in another file, on an ancestor, or at root. This is the
    /// half that needs the finished tree. Names resolve in dm.exe's own order for a type-level
    /// initialiser - the owner's inheritance chain nearest-first, then root - and only to a var
    /// that is <c>const</c>, since a non-const name there is <i>"expected a constant
    /// expression"</i> to the compiler and folding it would claim a value the program never has.
    /// The <c>/path::NAME</c> static form resolves against that path's chain instead. Probed
    /// 2026-08-16 with <c>-warn init_proc</c>; see <see cref="Binding.ConstantEvaluator"/>.
    /// </para>
    /// <para>
    /// A const whose value depends on another const gets the other's 32-bit value, not its
    /// six-digit rendering, so the arithmetic matches what dm.exe computes.
    /// </para>
    /// </remarks>
    internal string ConstantValueOf(TypeSymbol owner, VarSymbol variable)
    {
        // The eager fold already answered, or there is nothing a name lookup could add.
        if (variable.ConstantValue.Length > 0
            || !Binding.ConstantEvaluator.NamesAnything(variable.Initializer))
            return variable.ConstantValue;

        return ConstantOf(owner, variable)?.Render() ?? string.Empty;
    }

    private Binding.ConstantEvaluator.Constant? ConstantOf(TypeSymbol owner, VarSymbol variable)
    {
        _constants ??= new Dictionary<VarSymbol, Binding.ConstantEvaluator.Constant?>(
            ReferenceEqualityComparer.Instance);
        _folding ??= new HashSet<VarSymbol>(ReferenceEqualityComparer.Instance);

        if (_constants.TryGetValue(variable, out Binding.ConstantEvaluator.Constant? memo))
            return memo;

        // A cycle: dm.exe rejects it, and the honest answer here is "not a constant".
        if (!_folding.Add(variable))
            return null;

        try
        {
            Binding.ConstantEvaluator.Constant? value = Binding.ConstantEvaluator.Value(
                variable.Initializer,
                (name, scope) => ResolveConst(scope is null ? owner : Find(TypePath.FromSegments(scope.Segments)), name));

            _constants[variable] = value;
            return value;
        }
        finally
        {
            _folding.Remove(variable);
        }
    }

    // A const var by name from a type: its chain nearest-first, then root. `IsConst` is the gate -
    // a bare override on a subtype (`hp = 3`) is not const even when its parent's declaration is,
    // and dm.exe would not fold through it.
    private Binding.ConstantEvaluator.Constant? ResolveConst(TypeSymbol? from, string name)
    {
        if (from is null)
            return null;

        foreach (TypeSymbol candidate in InheritanceChain(from))
        {
            if (candidate.FindVar(name) is { } found)
                return found.IsConst ? ConstantOf(candidate, found) : null;
        }

        return Root.FindVar(name) is { IsConst: true } global ? ConstantOf(Root, global) : null;
    }
}
