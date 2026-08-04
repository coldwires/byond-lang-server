using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Symbols;

namespace Dm.Core.Services;

/// <summary>One hit from a workspace-wide symbol search.</summary>
public sealed class WorkspaceSymbol
{
    public WorkspaceSymbol(
        string name, string detail, SymbolKind kind, string file, Text.TextSpan span, Text.TextSpan nameSpan)
    {
        Name = name;
        Detail = detail;
        Kind = kind;
        File = file;
        Span = span;
        NameSpan = nameSpan;
    }

    public string Name { get; }

    /// <summary>The owning path, so two <c>New</c>s are distinguishable in a picker.</summary>
    public string Detail { get; }

    public SymbolKind Kind { get; }

    public string File { get; }

    public Text.TextSpan Span { get; }

    /// <summary>The name alone, which is where a caret should land.</summary>
    public Text.TextSpan NameSpan { get; }

    public override string ToString() => $"{Detail} ({Kind})";
}

/// <summary>
/// Finds symbols across the whole project by name.
/// </summary>
/// <remarks>
/// <para>
/// Ranked rather than merely filtered: an exact name beats a prefix, which beats a substring, and
/// ties break on the shorter name. Typing <c>New</c> in a codebase with thousands of matches is
/// useless without that, and it is the whole difference between a usable picker and a wall.
/// </para>
/// <para>
/// Builtins are excluded. They have no declaration site to navigate to, so offering one would
/// produce a result that cannot be opened — the object tree knows them, but no file declares them.
/// </para>
/// </remarks>
public static class WorkspaceSymbolService
{
    /// <summary>How many hits to return when the caller does not say.</summary>
    public const int DefaultLimit = 200;

    public static IReadOnlyList<WorkspaceSymbol> Search(
        ObjectTree tree,
        string query,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);

        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<WorkspaceSymbol>();

        string needle = query.Trim();
        List<(int Rank, int Length, WorkspaceSymbol Symbol)> hits = new();

        foreach (TypeSymbol type in tree.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!type.Path.IsRoot && Rank(type.Path.Name, needle) is { } typeRank)
            {
                foreach (DeclarationSite site in type.Sites)
                {
                    hits.Add((typeRank, type.Path.Name.Length, new WorkspaceSymbol(
                        type.Path.Name, type.Path.Text, SymbolKind.Type,
                        site.File, site.Span, site.NameSpan)));
                }
            }

            foreach (VarSymbol variable in type.Vars)
            {
                if (variable.IsBuiltin || Rank(variable.Name, needle) is not { } rank)
                    continue;

                DeclarationSite site = variable.Site;
                hits.Add((rank, variable.Name.Length, new WorkspaceSymbol(
                    variable.Name, Describe(type, variable.Name), SymbolKind.Variable,
                    site.File, site.Span, site.NameSpan)));
            }

            foreach (ProcSymbol proc in type.Procs)
            {
                if (Rank(proc.Name, needle) is not { } rank)
                    continue;

                foreach (DeclarationSite site in proc.Sites)
                {
                    hits.Add((rank, proc.Name.Length, new WorkspaceSymbol(
                        proc.Name, Describe(type, proc.Name) + "()",
                        proc.IsVerb ? SymbolKind.Verb : SymbolKind.Proc,
                        site.File, site.Span, site.NameSpan)));
                }
            }
        }

        hits.Sort(static (a, b) =>
        {
            int byRank = a.Rank.CompareTo(b.Rank);
            if (byRank != 0)
                return byRank;

            int byLength = a.Length.CompareTo(b.Length);
            return byLength != 0 ? byLength : string.CompareOrdinal(a.Symbol.Detail, b.Symbol.Detail);
        });

        List<WorkspaceSymbol> results = new(Math.Min(limit, hits.Count));

        foreach ((_, _, WorkspaceSymbol symbol) in hits)
        {
            if (results.Count >= limit)
                break;

            results.Add(symbol);
        }

        return results;
    }

    /// <summary>0 exact, 1 prefix, 2 substring, or null for no match. Case-insensitive.</summary>
    private static int? Rank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;

        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : null;
    }

    private static string Describe(TypeSymbol owner, string name)
        => owner.Path.IsRoot ? $"/{name}" : $"{owner.Path.Text}/{name}";
}
