using System.Collections.Generic;
using System.Linq;
using Dm.Core.Text;

namespace Dm.Core.Symbols;

/// <summary>Where a declaration was written. A type is legitimately declared in many files.</summary>
internal readonly struct DeclarationSite
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

internal sealed class VarSymbol
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

    /// <summary>
    /// Whether the DM Reference documents this builtin, so a hover can link to it.
    /// </summary>
    /// <remarks>
    /// Recorded rather than assumed: 574 of 789 builtins came from a reference anchor and the rest
    /// from <c>stddef.dm</c> and the verified-members table, which have none. Linking to a section
    /// that does not exist is worse than not linking.
    /// </remarks>
    public bool HasReference { get; init; }

    /// <summary>
    /// The initialiser as the author wrote it, or empty when there is none.
    /// </summary>
    /// <remarks>
    /// Rendered from the source span rather than from the expression tree, for the same reason a
    /// parameter's default is: a construct we model loosely still shows the author's own text, and
    /// the text cannot drift from the declaration the way a rendering can. Still not evaluated —
    /// <c>5 + 1</c> stays <c>5 + 1</c> here, and <see cref="ConstantValue"/> carries the 6 beside
    /// it rather than replacing it.
    /// </remarks>
    public string InitialValue { get; init; } = string.Empty;

    /// <summary>
    /// What the initialiser comes to, when it is a compile-time constant, rendered as DM renders
    /// it. Empty when it is not one, and empty for a bare literal.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="InitialValue"/> rather than instead of it: the author's text is what they
    /// wrote and this is what it means, and a reader of <c>= 5 * 60</c> wants both. A bare literal
    /// folds to nothing on purpose — <c>123456789</c> renders as <c>1.23457e+08</c> in DM's own
    /// six-significant-digit form, which is true and less useful than the literal already there.
    /// </remarks>
    public string ConstantValue { get; init; } = string.Empty;

    /// <summary>
    /// The initialiser's expression, kept so a name in it can be resolved once the tree exists.
    /// </summary>
    /// <remarks>
    /// <see cref="ConstantValue"/> is folded per file, where a name that lives in another file
    /// cannot be answered; <see cref="ObjectTree.ConstantValueOf"/> finishes the job lazily against
    /// the finished tree. Null for a builtin and for a var with no initialiser. The parses are
    /// already retained by the workspace, so this holds nothing new alive.
    /// </remarks>
    internal Syntax.ExpressionSyntax? Initializer { get; init; }

    /// <summary>
    /// Whether a <c>var/</c> introduced this, rather than a bare override.
    /// </summary>
    /// <remarks>
    /// The discriminator for the duplicate-definition check. <c>/obj/item/hp = 3</c> re-assigns an
    /// inherited var and is ordinary DM; <c>var/hp</c> on a type whose ancestor declares one is a
    /// compile error. Treating them alike would fire on most of a real game.
    /// </remarks>
    public bool IsDeclaration { get; init; }

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
internal sealed class ProcSymbol
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

    /// <summary>Whether the DM Reference documents this builtin — see <see cref="VarSymbol.HasReference"/>.</summary>
    public bool HasReference { get; internal set; }

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

    private readonly Dictionary<string, List<DeclarationSite>> _varDeclarationSites =
        new(System.StringComparer.Ordinal);
    private readonly Dictionary<string, ProcSymbol> _procs = new(System.StringComparer.Ordinal);
    private readonly List<TypeSymbol> _children = new();
    private readonly List<DeclarationSite> _sites = new();

    internal TypeSymbol(TypePath path, TypeSymbol? parent)
    {
        Path = path;
        Parent = parent;
    }

    /// <summary>The normalised absolute path that keys this node in the tree.</summary>
    public TypePath Path { get; }

    /// <summary>The path's last segment. Empty for the root.</summary>
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
    /// Where this type's <c>parent_type</c> was written in compile order, or
    /// <see cref="int.MaxValue"/> when it wrote none.
    /// </summary>
    /// <remarks>
    /// Kept because an inheritance CYCLE is one diagnostic to dm.exe, reported against the
    /// participant declared first — which cannot be worked out from the finished tree, only
    /// recorded as it is built.
    /// </remarks>
    internal int ParentTypeOrdinal { get; set; } = int.MaxValue;

    /// <summary>
    /// True when BYOND declares this type. A project reopening <c>/mob</c> adds to it without
    /// clearing the flag, so the tree can still tell a builtin type from one the project invented.
    /// </summary>
    public bool IsBuiltin { get; internal set; }

    /// <summary>Direct path children, in the order the tree created them.</summary>
    public IReadOnlyList<TypeSymbol> Children => _children;

    internal IReadOnlyCollection<VarSymbol> Vars => _vars.Values;

    internal IReadOnlyCollection<ProcSymbol> Procs => _procs.Values;

    /// <summary>
    /// Every place this type was declared. Empty for a node that only exists because something
    /// deeper was declared — <c>/obj/item/sword</c> alone brings <c>/obj/item</c> into being.
    /// </summary>
    internal IReadOnlyList<DeclarationSite> Sites => _sites;

    /// <summary>True when the type was declared outright rather than implied by a descendant.</summary>
    public bool IsDeclared => _sites.Count > 0;

    internal void AddSite(DeclarationSite site) => _sites.Add(site);

    internal void AddChild(TypeSymbol child) => _children.Add(child);

    internal VarSymbol AddVar(VarSymbol variable)
    {
        // Which sites DECLARED the name, for the duplicate-definition check. A bare override
        // (`hp = 3`, no `var/`) is not one — dm.exe accepts those and warning on them would fire
        // on most of a real game.
        //
        // Held HERE rather than on the VarSymbol, and that is the whole design constraint: a
        // VarSymbol instance is cached in a TreeContribution and replayed across rebuilds, so
        // accumulating into it would grow the list every time the tree was rebuilt. A TypeSymbol
        // is fresh per tree, so this is not.
        if (variable.IsDeclaration)
        {
            if (!_varDeclarationSites.TryGetValue(variable.Name, out List<DeclarationSite>? sites))
            {
                sites = new List<DeclarationSite>();
                _varDeclarationSites[variable.Name] = sites;
            }

            sites.Add(variable.Site);
        }

        // A later declaration of the same name on the same type wins, matching include order.
        _vars[variable.Name] = variable;
        return variable;
    }

    /// <summary>
    /// The sites that DECLARED a var on this type, in include order. Empty for a name reached only
    /// by bare override, and for a builtin.
    /// </summary>
    internal IReadOnlyList<DeclarationSite> VarDeclaringSites(string name)
        => _varDeclarationSites.TryGetValue(name, out List<DeclarationSite>? sites)
            ? sites
            : System.Array.Empty<DeclarationSite>();

    internal ProcSymbol GetOrAddProc(string name, bool isVerb)
    {
        if (_procs.TryGetValue(name, out ProcSymbol? existing))
            return existing;

        ProcSymbol proc = new(name, isVerb);
        _procs.Add(name, proc);
        return proc;
    }

    internal VarSymbol? FindVar(string name) => _vars.GetValueOrDefault(name);

    internal ProcSymbol? FindProc(string name) => _procs.GetValueOrDefault(name);

    /// <summary>The path text, such as <c>/obj/item</c>.</summary>
    public override string ToString() => Path.Text;
}
