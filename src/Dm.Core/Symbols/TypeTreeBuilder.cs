using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Symbols;

/// <summary>
/// Merges parsed files into one <see cref="ObjectTree"/>.
/// </summary>
/// <remarks>
/// <para>
/// Files must arrive in <b>include order</b>. DM resolves overrides by the order the compiler sees
/// them, so the same set of files in a different order is a different program — see PLAN.md §4a.
/// </para>
/// <para>
/// Three path rules from §4a drive most of this:
/// </para>
/// <list type="bullet">
/// <item><description>Indentation nests, so a declaration's path is appended to its enclosing one —
/// but only when it has no leading separator. A leading <c>/</c> is absolute and ignores the
/// indentation entirely.</description></item>
/// <item><description><c>var</c>, <c>proc</c> and <c>verb</c> are ordinary path segments, not
/// keywords, so the owning type is whatever precedes them.</description></item>
/// <item><description>Omitting <c>proc/</c> makes a declaration an override rather than a new proc,
/// which is what <see cref="ProcSymbol.DeclaringCount"/> records.</description></item>
/// </list>
/// </remarks>
public static class TypeTreeBuilder
{
    /// <summary>Builds a tree from files already in include order.</summary>
    public static ObjectTree Build(
        IEnumerable<(string File, ParseResult Parse)> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        ObjectTree tree = new();

        foreach ((string file, ParseResult parse) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddFile(tree, file, parse, cancellationToken);
        }

        return tree;
    }

    /// <summary>Adds one file's declarations to an existing tree.</summary>
    public static void AddFile(
        ObjectTree tree,
        string file,
        ParseResult parse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        Contribute(file, parse, cancellationToken).Apply(tree, cancellationToken);
    }

    /// <summary>
    /// One file's tree mutations, precomputed from its parse so a rebuild can replay them.
    /// </summary>
    /// <remarks>
    /// The tree merge was the last phase still walking every file's AST per rebuild — 385 ms of a
    /// 793 ms keystroke on /tg/station with one file re-parsed. A contribution is a pure function
    /// of (file, parse), so the workspace caches it by the parse's identity and replays: the AST
    /// walk, the owner-path computation and the parameter rendering are paid once per parse, and a
    /// rebuild does only the dictionary inserts. There is ONE walk implementation — this one — so
    /// the recorded ops cannot drift from what a direct build would have done.
    /// </remarks>
    public static TreeContribution Contribute(
        string file,
        ParseResult parse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parse);

        TreeContribution contribution = new();

        foreach (DeclarationSyntax declaration in parse.Root.Declarations)
            Walk(contribution, file, parse.Text, declaration, TypePath.Root, cancellationToken);

        return contribution;
    }

    private static void Walk(
        TreeContribution contribution,
        string file,
        SourceText text,
        DeclarationSyntax declaration,
        TypePath enclosing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (declaration)
        {
            // A `var` or `proc` header declares nothing itself; it says what kind its children are,
            // and giving it a node would create a type literally named `var`.
            //
            // It can still carry a type path in front of the keyword. `mob/proc` heads a block of
            // procs on /mob, so only the trailing keyword is the marker — passing the enclosing path
            // through unchanged puts every child on the root instead.
            case TypeDeclarationSyntax { IsGroupHeader: true } group:
            {
                TypePath owner = GroupOwner(enclosing, group.Path);

                foreach (DeclarationSyntax member in group.Members)
                    Walk(contribution, file, text, member, owner, cancellationToken);

                break;
            }

            case TypeDeclarationSyntax type:
            {
                TypePath path = Combine(enclosing, type.Path);
                contribution.RecordTypeSite(path, new DeclarationSite(file, type.Span, type.NameSpan));

                foreach (DeclarationSyntax member in type.Members)
                    Walk(contribution, file, text, member, path, cancellationToken);

                break;
            }

            case VarDeclarationSyntax variable:
                AddVar(contribution, file, text, variable, enclosing);

                foreach (VarDeclarationSyntax sibling in variable.Siblings)
                    AddVar(contribution, file, text, sibling, enclosing);

                break;

            case ProcDeclarationSyntax proc:
                AddProc(contribution, file, text, proc, enclosing);
                break;
        }
    }

    private static void AddVar(
        TreeContribution contribution, string file, SourceText text, VarDeclarationSyntax variable,
        TypePath enclosing)
    {
        // What the leading segments mean depends on how the variable was introduced. Under a `var`
        // they are its declared type and it belongs to the enclosing type; without one this is a
        // bare assignment and they name the type being overridden.
        TypePath owner = variable.InVarContext
            ? VarOwner(enclosing, variable.Path)
            : BareAssignmentOwner(enclosing, variable.Path);

        TypePath? parentType = null;
        IReadOnlyList<string>? relativeParentType = null;

        // `parent_type = /obj/thing` re-points inheritance, so it is a link as well as a variable —
        // `dm.exe -o` lists it as one. A leading `.` is a search from this type's own path, not a
        // name, so it has to wait for the finished tree; dm.exe accepts `parent_type = .sibling`.
        if (string.Equals(variable.Name, "parent_type", StringComparison.Ordinal)
            && variable.Initializer is PathExpressionSyntax path)
        {
            if (path.Path.Anchor == PathAnchor.UpwardSearch)
                relativeParentType = path.Path.Segments;
            else
                parentType = TypePath.FromSegments(path.Path.Segments);
        }

        TypePath? declaredType = variable.DeclaredType is { } written && written.Segments.Count > 0
            ? TypePath.FromSegments(written.Segments)
            : null;

        // The initialiser AS WRITTEN, rendered from source rather than from the tree, for the same
        // reason a parameter's default is: an expression we model loosely still shows the author's
        // own text. It is what a client would otherwise re-parse the file to display, and for a
        // `const` it is the whole meaning of the declaration.
        string initialValue = variable.Initializer is { } initializer
            ? text.ToString(initializer.Span)
            : string.Empty;

        contribution.RecordVar(
            owner,
            new VarSymbol(
                variable.Name,
                declaredType,
                variable.Modifiers,
                new DeclarationSite(file, variable.Span, variable.NameSpan))
            {
                InitialValue = initialValue,
            },
            parentType,
            relativeParentType);
    }

    /// <summary>
    /// Renders a parameter with its type, so a signature is more than a list of names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The declared type and the <c>as</c> clause are the two things a caller most needs and the
    /// parser already has them; keeping only the name threw that away and left a signature reading
    /// <c>heal(target, amount)</c> when the source says <c>heal(mob/target, amount as num)</c>.
    /// </para>
    /// <para>
    /// This is why DM needs no <c>@param</c> convention: the types are in the declaration, so a
    /// signature derived from it cannot drift out of date the way a comment can.
    /// </para>
    /// </remarks>
    private static string Render(ParameterSyntax parameter, SourceText text)
    {
        // The path is rendered resolved, so `mob/target` in source shows as `/mob/target`. That is
        // the type it actually names, and a leading separator is what distinguishes a type from a
        // bare name at a glance.
        string rendered = parameter.DeclaredType is { } type && type.Segments.Count > 0
            ? $"{type.Text}/{parameter.Name}"
            : parameter.Name;

        // `as num`, `as text|null` - an input filter rather than a type, but part of the signature.
        if (parameter.InputType is { Length: > 0 } inputType)
            rendered += $" as {inputType}";

        // The default, as written. A caller reading `heal(amount = 5)` learns something a caller
        // reading `heal(amount)` does not: that the argument is optional, and what it falls back to.
        // Rendered from source rather than from the tree, so an expression we model loosely still
        // shows the author's own text.
        if (parameter.DefaultValue is { } value)
            rendered += $" = {text.ToString(value.Span)}";
        else if (parameter.HasDefault)
            rendered += " = ...";

        return rendered;
    }

    private static void AddProc(
        TreeContribution contribution, string file, SourceText text, ProcDeclarationSyntax proc, TypePath enclosing)
    {
        List<string> parameters = new(proc.Parameters.Count);

        foreach (ParameterSyntax parameter in proc.Parameters)
            parameters.Add(Render(parameter, text));

        contribution.RecordProc(
            ProcOwner(enclosing, proc.Path),
            proc.Name,
            proc.IsVerb,
            new DeclarationSite(file, proc.Span, proc.NameSpan),
            proc.IsNewDeclaration,
            parameters);
    }

    /// <summary>
    /// The type a proc belongs to: everything before the <c>proc</c> or <c>verb</c> segment.
    /// </summary>
    /// <remarks>
    /// An override has no such segment — <c>/mob/Login()</c> — and then everything before the name
    /// is the owner. This is the opposite of the var rule below, and the difference is why they are
    /// separate: a proc's leading segments are always a type path, while a variable's may be its
    /// declared type instead.
    /// </remarks>
    /// <remarks>
    /// Internal for the same reason as <see cref="GroupOwner"/>: the binder must agree with the tree
    /// about which type a proc sits on, and a second copy of this rule would drift.
    /// </remarks>
    internal static TypePath ProcOwner(TypePath enclosing, PathSyntax path)
    {
        IReadOnlyList<string> segments = path.Segments;
        int take = Math.Max(segments.Count - 1, 0);

        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] is "proc" or "verb")
            {
                take = i;
                break;
            }
        }

        return Owner(enclosing, path, take);
    }

    /// <summary>
    /// The type a variable declared under a <c>var</c> belongs to.
    /// </summary>
    /// <remarks>
    /// Everything before the <c>var</c> segment. With no such segment the declaration came from a
    /// bare <c>var</c> block, so it belongs to the enclosing type and the leading segments are its
    /// declared type — which the parser has already split out.
    /// Internal for the same reason as <see cref="GroupOwner"/>: the outline reports each symbol's
    /// owner and must agree with the tree about it.
    /// </remarks>
    internal static TypePath VarOwner(TypePath enclosing, PathSyntax path)
    {
        IReadOnlyList<string> segments = path.Segments;
        int take = 0;

        for (int i = 0; i < segments.Count; i++)
        {
            if (string.Equals(segments[i], "var", StringComparison.Ordinal))
            {
                take = i;
                break;
            }
        }

        return Owner(enclosing, path, take);
    }

    /// <summary>
    /// The type whose members a <c>var</c>/<c>proc</c> block header introduces.
    /// </summary>
    /// <remarks>
    /// Everything before the keyword. A bare <c>var</c> or <c>proc</c> has nothing before it and so
    /// leaves the enclosing type alone, but <c>mob/proc</c> owns its children on <c>/mob</c>.
    /// Found by diffing against <c>dm.exe -o</c> on mlaas: 34 procs were landing on the root.
    /// </remarks>
    /// <summary>
    /// The type a bare <c>var</c>/<c>proc</c>/<c>verb</c> group header's children belong to.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because <see cref="Dm.Core.Binding.Binder"/> has to agree with
    /// the tree about which type a member sits on. Two copies of this would disagree eventually,
    /// and the symptom would be diagnostics reported against the wrong type.
    /// </remarks>
    internal static TypePath GroupOwner(TypePath enclosing, PathSyntax path)
    {
        IReadOnlyList<string> segments = path.Segments;

        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i] is "var" or "proc" or "verb")
                return Owner(enclosing, path, i);
        }

        // No keyword means this is a header nested inside a `var` block, where the segments are a
        // type prefix for the children rather than a path to them. `UI/Stats/Child` under
        // `/UI/Stats/Frame` declares members of Frame, not of `/UI/Stats/Frame/UI/Stats`.
        return enclosing;
    }

    /// <summary>The type a bare assignment overrides a var on: everything before the name.</summary>
    internal static TypePath BareAssignmentOwner(TypePath enclosing, PathSyntax path)
        => Owner(enclosing, path, Math.Max(path.Segments.Count - 1, 0));

    /// <summary>Takes the first <paramref name="count"/> segments, honouring the path's anchor.</summary>
    private static TypePath Owner(TypePath enclosing, PathSyntax path, int count)
    {
        List<string> owner = new(count);

        for (int i = 0; i < count; i++)
            owner.Add(path.Segments[i]);

        // A leading `/` is absolute and ignores the indentation it was written under.
        return path.Anchor == PathAnchor.Absolute
            ? TypePath.FromSegments(owner)
            : enclosing.Append(owner);
    }

    /// <summary>Combines an enclosing path with a declaration's own, honouring its anchor.</summary>
    internal static TypePath Combine(TypePath enclosing, PathSyntax path)
        => path.Anchor == PathAnchor.Absolute
            ? TypePath.FromSegments(path.Segments)
            : enclosing.Append(path.Segments);
}

/// <summary>One file's tree mutations, in declaration order, replayable into any tree.</summary>
/// <remarks>
/// <para>
/// Order is part of the contract: sites and override chains record the order the compiler sees
/// declarations, so ops replay exactly as they were walked, within the file and — because the
/// caller applies files in include order — across it.
/// </para>
/// <para>
/// A recorded <see cref="VarSymbol"/> is REUSED across replays rather than reconstructed: it is
/// immutable after construction, only one tree built from these contributions is ever live, and
/// the reuse is what makes a replayed var almost free. Procs cannot be reused the same way —
/// a <see cref="ProcSymbol"/> accumulates sites from every file — so a proc op carries the raw
/// ingredients and replays through <see cref="TypeSymbol.GetOrAddProc"/>.
/// </para>
/// </remarks>
public sealed class TreeContribution
{
    private readonly List<Op> _ops = new();

    internal void RecordTypeSite(TypePath path, DeclarationSite site)
        => _ops.Add(new Op(OpKind.TypeSite, path) { Site = site });

    internal void RecordVar(
        TypePath owner, VarSymbol symbol, TypePath? parentType, IReadOnlyList<string>? relativeParentType)
        => _ops.Add(new Op(OpKind.Var, owner)
        {
            Var = symbol,
            ParentType = parentType,
            RelativeParentType = relativeParentType,
        });

    internal void RecordProc(
        TypePath owner,
        string name,
        bool isVerb,
        DeclarationSite site,
        bool declaresNew,
        IReadOnlyList<string> parameters)
        => _ops.Add(new Op(OpKind.Proc, owner)
        {
            Name = name,
            IsVerb = isVerb,
            Site = site,
            DeclaresNew = declaresNew,
            Parameters = parameters,
        });

    /// <summary>Replays every recorded mutation into the tree, in order.</summary>
    public void Apply(ObjectTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        foreach (Op op in _ops)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TypeSymbol owner = tree.GetOrAdd(op.Owner);

            switch (op.Kind)
            {
                case OpKind.TypeSite:
                    owner.AddSite(op.Site);
                    break;

                case OpKind.Var:
                    if (op.ParentType is not null)
                        owner.ParentType = op.ParentType;

                    if (op.RelativeParentType is not null)
                        owner.RelativeParentType = op.RelativeParentType;

                    owner.AddVar(op.Var!);
                    break;

                case OpKind.Proc:
                    owner.GetOrAddProc(op.Name!, op.IsVerb).Add(op.Site, op.DeclaresNew, op.Parameters!);
                    break;
            }
        }
    }

    private enum OpKind : byte
    {
        TypeSite,
        Var,
        Proc,
    }

    private sealed class Op
    {
        public Op(OpKind kind, TypePath owner)
        {
            Kind = kind;
            Owner = owner;
        }

        public OpKind Kind { get; }

        public TypePath Owner { get; }

        public DeclarationSite Site { get; init; }

        public VarSymbol? Var { get; init; }

        public TypePath? ParentType { get; init; }

        public IReadOnlyList<string>? RelativeParentType { get; init; }

        public string? Name { get; init; }

        public bool IsVerb { get; init; }

        public bool DeclaresNew { get; init; }

        public IReadOnlyList<string>? Parameters { get; init; }
    }
}
