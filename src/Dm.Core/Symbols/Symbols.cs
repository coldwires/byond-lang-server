using System.Collections.Generic;
using System.Linq;
using Dm.Core.Text;

namespace Dm.Core.Symbols;

/// <summary>Where a declaration was written. A type is legitimately declared in many files.</summary>
public readonly struct DeclarationSite
{
    public DeclarationSite(string file, TextSpan span, TextSpan nameSpan)
    {
        File = file;
        Span = span;
        NameSpan = nameSpan;
    }

    public string File { get; }

    public TextSpan Span { get; }

    /// <summary>Span of the name alone, for go-to-definition.</summary>
    public TextSpan NameSpan { get; }

    public override string ToString() => $"{File}{Span}";
}

public sealed class VarSymbol
{
    internal VarSymbol(string name, TypePath? declaredType, IReadOnlyList<string> modifiers, DeclarationSite site)
    {
        Name = name;
        DeclaredType = declaredType;
        Modifiers = modifiers;
        Site = site;
    }

    public string Name { get; }

    /// <summary>
    /// The declared type, or null when untyped. This is what makes <c>var/mob/test/t</c> then
    /// <c>t.</c> resolvable without inference.
    /// </summary>
    public TypePath? DeclaredType { get; }

    public IReadOnlyList<string> Modifiers { get; }

    public DeclarationSite Site { get; }

    /// <summary>True for a BYOND builtin, which has no declaration site in any file.</summary>
    public bool IsBuiltin { get; init; }

    public bool IsConst => Modifiers.Contains("const");

    public override string ToString() => DeclaredType is { } type ? $"{type}/{Name}" : Name;
}

/// <summary>
/// A proc on a type, together with every declaration of it in include order.
/// </summary>
/// <remarks>
/// The chain matters for two reasons. <c>..()</c> reaches the previous entry rather than strictly
/// the parent type, so a type that overrides the same proc twice has a chain of its own. And
/// declaring <c>proc/</c> twice on one type is a duplicate-definition error, which needs the count
/// to diagnose at M11.
/// </remarks>
public sealed class ProcSymbol
{
    private readonly List<DeclarationSite> _sites = new();

    internal ProcSymbol(string name, bool isVerb)
    {
        Name = name;
        IsVerb = isVerb;
    }

    public string Name { get; }

    public bool IsVerb { get; }

    /// <summary>True for a BYOND builtin, which has no declaration site in any file.</summary>
    public bool IsBuiltin { get; internal set; }

    /// <summary>Parameter names from the first declaration that gave any.</summary>
    public IReadOnlyList<string> Parameters { get; private set; } = System.Array.Empty<string>();

    /// <summary>Every declaration, in include order. The first is the introducing one.</summary>
    public IReadOnlyList<DeclarationSite> Sites => _sites;

    /// <summary>
    /// How many declarations used a <c>proc/</c> or <c>verb/</c> segment. More than one on the same
    /// type is the duplicate-definition error DM reports.
    /// </summary>
    public int DeclaringCount { get; private set; }

    /// <summary>
    /// The sites that used the <c>proc/</c> or <c>verb/</c> segment, in include order — the ones
    /// <see cref="DeclaringCount"/> counts. The duplicate-definition check needs which sites
    /// declared, not only how many.
    /// </summary>
    public IReadOnlyList<DeclarationSite> DeclaringSites => _declaringSites;

    private readonly List<DeclarationSite> _declaringSites = new();

    /// <summary>Sets the signature for a builtin, which has no declaration to read one from.</summary>
    internal void SetBuiltinParameters(IReadOnlyList<string> parameters) => Parameters = parameters;

    internal void Add(DeclarationSite site, bool declaresNew, IReadOnlyList<string> parameters)
    {
        _sites.Add(site);

        if (declaresNew)
        {
            DeclaringCount++;
            _declaringSites.Add(site);
        }

        if (Parameters.Count == 0 && parameters.Count > 0)
            Parameters = parameters;
    }

    public override string ToString() => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>
/// One node in the type tree, merging everything every file said about that path.
/// </summary>
public sealed class TypeSymbol
{
    private readonly Dictionary<string, VarSymbol> _vars = new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, ProcSymbol> _procs = new(System.StringComparer.Ordinal);
    private readonly List<TypeSymbol> _children = new();
    private readonly List<DeclarationSite> _sites = new();

    internal TypeSymbol(TypePath path, TypeSymbol? parent)
    {
        Path = path;
        Parent = parent;
    }

    public TypePath Path { get; }

    public string Name => Path.Name;

    /// <summary>The implicit parent, from the path. <c>/obj/item</c>'s is <c>/obj</c>.</summary>
    public TypeSymbol? Parent { get; }

    /// <summary>
    /// An explicit <c>parent_type</c>, which replaces the implicit link for inheritance.
    /// </summary>
    /// <remarks>
    /// Resolved lazily via <see cref="ObjectTree"/> because the target may be declared later in
    /// include order than the type naming it.
    /// </remarks>
    public TypePath? ParentType { get; internal set; }

    /// <summary>
    /// Segments of a <c>parent_type</c> written as a leading-<c>.</c> path, resolved on demand.
    /// </summary>
    /// <remarks>
    /// Kept unresolved for the same reason <see cref="ObjectTree.InheritanceParent"/> resolves
    /// <see cref="ParentType"/> late: the target can be declared after the type naming it. A
    /// relative path additionally cannot be resolved without the finished tree, since the search
    /// asks which candidates exist.
    /// </remarks>
    public IReadOnlyList<string>? RelativeParentType { get; internal set; }

    /// <summary>
    /// True when BYOND declares this type. A project reopening <c>/mob</c> adds to it without
    /// clearing the flag, so the tree can still tell a builtin type from one the project invented.
    /// </summary>
    public bool IsBuiltin { get; internal set; }

    public IReadOnlyList<TypeSymbol> Children => _children;

    public IReadOnlyCollection<VarSymbol> Vars => _vars.Values;

    public IReadOnlyCollection<ProcSymbol> Procs => _procs.Values;

    /// <summary>
    /// Every place this type was declared. Empty for a node that only exists because something
    /// deeper was declared — <c>/obj/item/sword</c> alone brings <c>/obj/item</c> into being.
    /// </summary>
    public IReadOnlyList<DeclarationSite> Sites => _sites;

    /// <summary>True when the type was declared outright rather than implied by a descendant.</summary>
    public bool IsDeclared => _sites.Count > 0;

    internal void AddSite(DeclarationSite site) => _sites.Add(site);

    internal void AddChild(TypeSymbol child) => _children.Add(child);

    internal VarSymbol AddVar(VarSymbol variable)
    {
        // A later declaration of the same name on the same type wins, matching include order.
        _vars[variable.Name] = variable;
        return variable;
    }

    internal ProcSymbol GetOrAddProc(string name, bool isVerb)
    {
        if (_procs.TryGetValue(name, out ProcSymbol? existing))
            return existing;

        ProcSymbol proc = new(name, isVerb);
        _procs.Add(name, proc);
        return proc;
    }

    public VarSymbol? FindVar(string name) => _vars.GetValueOrDefault(name);

    public ProcSymbol? FindProc(string name) => _procs.GetValueOrDefault(name);

    public override string ToString() => Path.Text;
}
