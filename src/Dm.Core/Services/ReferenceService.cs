using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Binding;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>How a reference uses the symbol it names.</summary>
public enum ReferenceKind
{
    /// <summary>The value is read, or the type path is mentioned.</summary>
    Read = 0,

    /// <summary>The name is the target of an assignment, compound assignments included.</summary>
    Write = 1,

    /// <summary>The name is invoked as a proc.</summary>
    Call = 2,

    /// <summary>A proc declaration overriding the target — the incoming half of a type hierarchy.</summary>
    Override = 3,
}

/// <summary>One use of a symbol somewhere in the project.</summary>
public sealed class Reference
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public Reference(string file, TextSpan span, string target, ReferenceKind kind, string inside)
    {
        File = file;
        Span = span;
        Target = target;
        Kind = kind;
        Inside = inside;
    }

    /// <summary>The referencing file.</summary>
    public string File { get; }

    /// <summary>The referencing name's span in that file.</summary>
    public TextSpan Span { get; }

    /// <summary>
    /// The referenced symbol in definition's detail spelling — <c>/mob/test/hp</c> for a var,
    /// <c>/mob/test/heal()</c> for a proc, <c>/heal()</c> for a global, a type's path for a type —
    /// canonicalised to the FARTHEST declaring type, so a call through a subtype receiver and an
    /// override of the same proc share one target.
    /// </summary>
    public string Target { get; }

    /// <summary>Read, write, call or override — how the site uses the symbol.</summary>
    public ReferenceKind Kind { get; }

    /// <summary>
    /// The enclosing symbol, in the same spelling: the proc a hit sits inside, the type for a
    /// type-level initialiser, <c>/</c> at the root. Grouping by it is a call hierarchy.
    /// </summary>
    public string Inside { get; }

    /// <summary>Debug rendering: file, kind, target and enclosing symbol.</summary>
    public override string ToString() => $"{File}: {Kind} {Target} inside {Inside}";
}

/// <summary>A reference listing, and whether the cap cut it short.</summary>
public sealed class ReferenceListing
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public ReferenceListing(IReadOnlyList<Reference> references, bool truncated)
    {
        References = references;
        Truncated = truncated;
    }

    /// <summary>The hits, file by file in the order the caller supplied the files.</summary>
    public IReadOnlyList<Reference> References { get; }

    /// <summary>True when the cap cut the list. Reported rather than left to infer from the count.</summary>
    public bool Truncated { get; }
}

/// <summary>
/// Finds every use of a symbol across the project — the index four features stand on: references,
/// call hierarchy (group by <see cref="Reference.Inside"/>), document highlight, and rename.
/// </summary>
/// <remarks>
/// <para>
/// The hits come from the <b>binder's</b> walk with a sink attached, not from a second resolver:
/// a reference exists exactly where diagnostics resolution succeeds, so the index and the
/// squiggles can never disagree about what a name means. The same conservatism follows — a member
/// is a hit only through a receiver whose type is written down, so the list under-reports rather
/// than lies. Locals and parameters are not index symbols; a query for one answers empty.
/// </para>
/// <para>
/// No persistent index yet: each query walks every file's retained parse. On the acceptance-sized
/// project that is milliseconds; on /tg/station it is a bounded scan with a cap and a
/// <see cref="ReferenceListing.Truncated"/> flag, and making it incremental is M9-shaped work for
/// the day a profile asks.
/// </para>
/// </remarks>
public static class ReferenceService
{
    /// <summary>How many references a query returns when the caller does not say.</summary>
    public const int DefaultLimit = 1000;

    /// <summary>Every use of the symbol named by a canonical target string.</summary>
    public static ReferenceListing Find(
        ObjectTree tree,
        IReadOnlyList<(string File, ParseResult Parse)> files,
        string target,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(target);

        if (limit <= 0)
            limit = DefaultLimit;

        string name = NameOf(target);
        List<Reference> hits = new();
        bool truncated = false;

        foreach ((string file, ParseResult parse) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Binder.Bind(tree, parse.Root, file, reference =>
            {
                if (!string.Equals(reference.Target, target, StringComparison.Ordinal))
                    return;

                if (hits.Count < limit)
                    hits.Add(reference);
                else
                    truncated = true;
            }, name);
        }

        return new ReferenceListing(hits, truncated);
    }

    /// <summary>
    /// References of the symbol at a position: resolved exactly as go-to-definition resolves it,
    /// then found by target. Null when nothing at the position is an index symbol — a local, a
    /// parameter, whitespace.
    /// </summary>
    /// <remarks>
    /// The FARTHEST definition is the canonical target, matching the walk's canonicalisation:
    /// asking for references on any override of a proc answers about the whole family.
    /// </remarks>
    public static ReferenceListing? At(
        ObjectTree tree,
        IReadOnlyList<(string File, ParseResult Parse)> files,
        Document document,
        int line,
        int character,
        PositionEncoding encoding = PositionEncoding.Utf16,
        int limit = DefaultLimit,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
            tree, document, line, character, encoding, cancellationToken);

        return found.Count == 0
            ? null
            : Find(tree, files, found[^1].Detail, limit, cancellationToken);
    }

    /// <summary>The bare name inside a canonical target, for the walk's cheap prefilter.</summary>
    internal static string NameOf(string target)
    {
        string trimmed = target.EndsWith("()", StringComparison.Ordinal) ? target[..^2] : target;
        int slash = trimmed.LastIndexOf('/');

        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
