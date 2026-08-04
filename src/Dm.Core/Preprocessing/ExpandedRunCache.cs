using System;
using System.Collections.Generic;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Reuses a file's <see cref="TokenSource"/> and parse when the preprocessor produced the same
/// tokens for it as last time.
/// </summary>
/// <remarks>
/// <para>
/// A rebuild re-parses every file in the project, and after an edit almost every file's tokens are
/// identical to the ones parsed a moment ago. The soundness argument is short: a
/// <see cref="ParseResult"/> is a pure function of the token run it was built from, so a run that
/// hashes the same parses the same.
/// </para>
/// <para>
/// **The hash is over what the parser can see**, which is the kind, the reported position and — for
/// anything a macro produced — the text, since an expanded token's text does not come from its
/// reported span. Hashing the tokens rather than keying on (file, macro state) keeps this
/// independent of *why* a run is unchanged: a file whose text changed in a comment, or one reached
/// through a different include path, both land on the same answer if the tokens match.
/// </para>
/// <para>
/// This does not skip preprocessing. The walk still runs and the tokens are still produced; what is
/// avoided is rebuilding the token source and re-parsing. Skipping the walk itself needs the
/// file-effect memoization described in PLAN.md §6 M9, which is a larger change.
/// </para>
/// </remarks>
public sealed class ExpandedRunCache
{
    private readonly Dictionary<string, Entry> _entries;

    public ExpandedRunCache(StringComparer? comparer = null)
        => _entries = new Dictionary<string, Entry>(comparer ?? StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose parse was reused since the last <see cref="ResetStatistics"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Files parsed afresh since the last <see cref="ResetStatistics"/>.</summary>
    public int Misses { get; private set; }

    public void ResetStatistics()
    {
        Hits = 0;
        Misses = 0;
    }

    public void Clear()
    {
        _entries.Clear();
        ResetStatistics();
    }

    /// <summary>
    /// The token source and parse for one file's run, built only if this run is new.
    /// </summary>
    public (TokenSource Source, ParseResult Parse) GetOrAdd(
        string file, SourceText origin, IReadOnlyList<ExpandedToken> run)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(run);

        bool known = _entries.TryGetValue(file, out Entry entry);

        // A replayed file hands back the very run it produced last time, since the effect cache
        // adopts it by reference rather than copying it. Recognising that is worth more than the
        // hash it saves: hashing is a pass over every token in the project.
        if (known && ReferenceEquals(entry.Run, run) && ReferenceEquals(entry.Origin, origin))
        {
            Hits++;
            return (entry.Source, entry.Parse);
        }

        long hash = HashRun(run);

        if (known
            && entry.Hash == hash
            && entry.Count == run.Count
            && ReferenceEquals(entry.Origin, origin))
        {
            Hits++;
            return (entry.Source, entry.Parse);
        }

        TokenSource source = TokenSource.FromExpanded(origin, run);
        ParseResult parse = DeclarationParser.Parse(source);

        _entries[file] = new Entry(hash, run.Count, origin, run, source, parse);
        Misses++;

        return (source, parse);
    }

    /// <summary>
    /// Hashes what a parse depends on.
    /// </summary>
    /// <remarks>
    /// Position is included because spans reach the client: two runs that differ only in where they
    /// sit produce identical trees with different ranges, and handing back the cached one would put
    /// an outline entry on the wrong line. Text is included only for macro-produced tokens, where it
    /// is not recoverable from the span — for everything else the span settles it, and hashing the
    /// string as well would cost a read of the whole project per rebuild.
    /// </remarks>
    private static long HashRun(IReadOnlyList<ExpandedToken> run)
    {
        // FNV-1a over 64 bits: cheap, sequential, and no allocation.
        const long Prime = 1099511628211;
        long hash = unchecked((long)14695981039346656037);

        for (int i = 0; i < run.Count; i++)
        {
            ExpandedToken token = run[i];
            (SourceText _, TextSpan span) = token.ReportAt;

            hash = unchecked((hash ^ (int)token.Kind) * Prime);
            hash = unchecked((hash ^ span.Start) * Prime);
            hash = unchecked((hash ^ span.Length) * Prime);

            if (token.IsFromMacro)
                hash = unchecked((hash ^ token.Text.GetHashCode(StringComparison.Ordinal)) * Prime);
        }

        return hash;
    }

    private readonly record struct Entry(
        long Hash,
        int Count,
        SourceText Origin,
        IReadOnlyList<ExpandedToken> Run,
        TokenSource Source,
        ParseResult Parse);
}
