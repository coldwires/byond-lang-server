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
        ArgumentNullException.ThrowIfNull(parse);

        foreach (DeclarationSyntax declaration in parse.Root.Declarations)
            Walk(tree, file, declaration, TypePath.Root, cancellationToken);
    }

    private static void Walk(
        ObjectTree tree,
        string file,
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
                    Walk(tree, file, member, owner, cancellationToken);

                break;
            }

            case TypeDeclarationSyntax type:
            {
                TypePath path = Combine(enclosing, type.Path);
                TypeSymbol symbol = tree.GetOrAdd(path);
                symbol.AddSite(new DeclarationSite(file, type.Span, type.NameSpan));

                foreach (DeclarationSyntax member in type.Members)
                    Walk(tree, file, member, path, cancellationToken);

                break;
            }

            case VarDeclarationSyntax variable:
                AddVar(tree, file, variable, enclosing);

                foreach (VarDeclarationSyntax sibling in variable.Siblings)
                    AddVar(tree, file, sibling, enclosing);

                break;

            case ProcDeclarationSyntax proc:
                AddProc(tree, file, proc, enclosing);
                break;
        }
    }

    private static void AddVar(ObjectTree tree, string file, VarDeclarationSyntax variable, TypePath enclosing)
    {
        // What the leading segments mean depends on how the variable was introduced. Under a `var`
        // they are its declared type and it belongs to the enclosing type; without one this is a
        // bare assignment and they name the type being overridden.
        TypePath owner = variable.InVarContext
            ? VarOwner(enclosing, variable.Path)
            : BareAssignmentOwner(enclosing, variable.Path);

        // `parent_type = /obj/thing` re-points inheritance, so it is a link rather than a variable.
        if (string.Equals(variable.Name, "parent_type", StringComparison.Ordinal))
        {
            if (variable.Initializer is PathExpressionSyntax path)
            {
                // It is a real var as well as an inheritance link, and `dm.exe -o` lists it as one.
                TypeSymbol target = tree.GetOrAdd(owner);

                // A leading `.` is a search from this type's own path, not a name, so it has to
                // wait for the finished tree. dm.exe accepts `parent_type = .sibling`.
                if (path.Path.Anchor == PathAnchor.UpwardSearch)
                    target.RelativeParentType = path.Path.Segments;
                else
                    target.ParentType = TypePath.FromSegments(path.Path.Segments);
            }
        }

        TypePath? declaredType = variable.DeclaredType is { } written && written.Segments.Count > 0
            ? TypePath.FromSegments(written.Segments)
            : null;

        tree.GetOrAdd(owner).AddVar(new VarSymbol(
            variable.Name,
            declaredType,
            variable.Modifiers,
            new DeclarationSite(file, variable.Span, variable.NameSpan)));
    }

    private static void AddProc(ObjectTree tree, string file, ProcDeclarationSyntax proc, TypePath enclosing)
    {
        List<string> parameters = new(proc.Parameters.Count);

        foreach (ParameterSyntax parameter in proc.Parameters)
            parameters.Add(parameter.Name);

        tree.GetOrAdd(ProcOwner(enclosing, proc.Path))
            .GetOrAddProc(proc.Name, proc.IsVerb)
            .Add(new DeclarationSite(file, proc.Span, proc.NameSpan), proc.IsNewDeclaration, parameters);
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
    private static TypePath ProcOwner(TypePath enclosing, PathSyntax path)
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
    /// </remarks>
    private static TypePath VarOwner(TypePath enclosing, PathSyntax path)
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
    private static TypePath GroupOwner(TypePath enclosing, PathSyntax path)
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
    private static TypePath BareAssignmentOwner(TypePath enclosing, PathSyntax path)
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
    private static TypePath Combine(TypePath enclosing, PathSyntax path)
        => path.Anchor == PathAnchor.Absolute
            ? TypePath.FromSegments(path.Segments)
            : enclosing.Append(path.Segments);
}
