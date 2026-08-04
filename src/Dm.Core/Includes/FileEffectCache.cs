using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
using Dm.Core.Text;

namespace Dm.Core.Includes;

/// <summary>What one recorded step of a file's effect does when replayed.</summary>
internal enum EffectKind
{
    /// <summary>Tokens this file contributed, already expanded.</summary>
    Tokens,

    /// <summary>A macro this file defined.</summary>
    Define,

    /// <summary>A macro this file undefined.</summary>
    Undef,

    /// <summary>A file this one included, which the replay descends into.</summary>
    Include,

    /// <summary>A diagnostic this file's own directives produced.</summary>
    Diagnostic,

    /// <summary>A <c>#pragma multiple</c>, which opts the file out of include-once.</summary>
    Reincludable,
}

/// <summary>
/// Where an <c>#include</c> was written, and what it said.
/// </summary>
/// <remarks>
/// Carried through so a replayed include reports a repeat against the line the author wrote and in
/// the words they used, rather than against the resolved path. A diagnostic that moves when a file
/// is served from cache is a diagnostic that looks like a bug in us.
/// </remarks>
public readonly struct IncludeSite
{
    public IncludeSite(string target, TextSpan span)
    {
        Target = target;
        Span = span;
    }

    /// <summary>The path as written in the directive.</summary>
    public string Target { get; }

    public TextSpan Span { get; }
}

/// <summary>One step of a file's recorded effect on the walk.</summary>
internal readonly struct EffectStep
{
    private EffectStep(EffectKind kind)
    {
        Kind = kind;
        Start = 0;
        Count = 0;
        Macro = null;
        Name = null;
        Diagnostic = default;
        IsLibrary = false;
        HashAfter = 0;
        Site = default;
    }

    public EffectKind Kind { get; private init; }

    /// <summary>Where this stretch sits in the file's run, for a <see cref="EffectKind.Tokens"/> step.</summary>
    /// <remarks>
    /// An offset rather than an array of its own, so a replay can hand the whole run over by
    /// reference and then say only where each stretch belongs in compile order.
    /// </remarks>
    public int Start { get; private init; }

    public int Count { get; private init; }

    public MacroDefinition? Macro { get; private init; }

    /// <summary>An undefined macro's name, or an include's resolved path.</summary>
    public string? Name { get; private init; }

    public Diagnostic Diagnostic { get; private init; }

    public bool IsLibrary { get; private init; }

    /// <summary>
    /// The macro state hash immediately after this include finished, when it was recorded.
    /// </summary>
    /// <remarks>
    /// The validation that makes this cache sound. A file's key covers its own text and the state it
    /// was entered with — it does not cover what its includes do, so an included file that starts
    /// defining a different macro would leave the rest of this file's recorded tokens describing an
    /// expansion that no longer happens. Comparing here catches exactly that.
    /// </remarks>
    public int HashAfter { get; private init; }

    /// <summary>Where the include was written, so a replayed repeat reports against the same line.</summary>
    public IncludeSite Site { get; private init; }

    public static EffectStep ForTokens(int start, int count)
        => new(EffectKind.Tokens) { Start = start, Count = count };

    public static EffectStep ForDefine(MacroDefinition macro)
        => new(EffectKind.Define) { Macro = macro };

    public static EffectStep ForUndef(string name)
        => new(EffectKind.Undef) { Name = name };

    public static EffectStep ForInclude(string resolved, bool isLibrary, IncludeSite site, int hashAfter)
        => new(EffectKind.Include)
        {
            Name = resolved,
            IsLibrary = isLibrary,
            Site = site,
            HashAfter = hashAfter,
        };

    public static EffectStep ForDiagnostic(Diagnostic diagnostic)
        => new(EffectKind.Diagnostic) { Diagnostic = diagnostic };

    public static EffectStep ForReincludable(string path)
        => new(EffectKind.Reincludable) { Name = path };
}

/// <summary>Everything walking one file did, in order, so it can be done again without redoing it.</summary>
internal sealed class FileEffect
{
    public FileEffect(
        SourceText text, int entryHash, List<EffectStep> steps, ExpandedToken[] run, int exitHash)
    {
        Text = text;
        EntryHash = entryHash;
        Steps = steps;
        Run = run;
        ExitHash = exitHash;
    }

    /// <summary>The text this was recorded from, compared by identity.</summary>
    public SourceText Text { get; }

    public int EntryHash { get; }

    public IReadOnlyList<EffectStep> Steps { get; }

    /// <summary>
    /// Everything this file contributed to the stream, in order.
    /// </summary>
    /// <remarks>
    /// Handed to the collector by reference on a replay, so a file that has not changed costs no
    /// copying at all. The <c>Tokens</c> steps index into it, which is what still lets the stretches
    /// be placed individually in compile order between the file's includes.
    /// </remarks>
    public ExpandedToken[] Run { get; }

    /// <summary>Macro state hash when the file finished, checked after a replay as a self-test.</summary>
    public int ExitHash { get; }
}

/// <summary>
/// Remembers what walking a file did, so an unchanged file can be replayed instead of re-walked.
/// </summary>
/// <remarks>
/// <para>
/// The largest cost in a rebuild is the walk: every file is read, lexed, scanned for directives and
/// expanded, to produce a token stream that is then almost always identical to last time's. Caching
/// the *result* of that (see <see cref="Preprocessing.ExpandedRunCache"/>) avoids re-parsing but not
/// the walk itself. This avoids the walk.
/// </para>
/// <para>
/// A file's effect is a pure function of its text and the macro state it is entered with, **given
/// that its includes behave the same way**. That last clause is not covered by the key, so each
/// include step records the macro state hash it produced and the replay checks it. A mismatch means
/// the recorded remainder is describing an expansion that no longer happens, and the build says so
/// rather than serving it.
/// </para>
/// <para>
/// Keyed by path and entry hash together: the same file included twice from different macro states
/// is genuinely two different effects, and DM projects do that deliberately with
/// <c>#pragma multiple</c>.
/// </para>
/// </remarks>
public sealed class FileEffectCache
{
    private readonly Dictionary<(string Path, int EntryHash), FileEffect> _entries = new();

    /// <summary>Files replayed since the last <see cref="ResetStatistics"/>.</summary>
    public int Hits { get; private set; }

    /// <summary>Files walked for real since the last <see cref="ResetStatistics"/>.</summary>
    public int Misses { get; private set; }

    /// <summary>
    /// True when a replay found an include had changed what it does.
    /// </summary>
    /// <remarks>
    /// The build that saw this cannot be trusted — part of it was replayed against a macro state
    /// that no longer holds — so the caller redoes it without the cache. That costs one wasted pass
    /// in exactly the case where a macro moved and everything downstream had to be redone anyway.
    /// </remarks>
    public bool Diverged { get; private set; }

    public void ResetStatistics()
    {
        Hits = 0;
        Misses = 0;
        Diverged = false;
    }

    public void Clear()
    {
        _entries.Clear();
        ResetStatistics();
    }

    internal bool TryGet(string path, SourceText text, int entryHash, out FileEffect effect)
    {
        if (_entries.TryGetValue((path, entryHash), out FileEffect? found) && ReferenceEquals(found.Text, text))
        {
            effect = found;
            Hits++;
            return true;
        }

        effect = null!;
        Misses++;
        return false;
    }

    internal void Add(string path, FileEffect effect) => _entries[(path, effect.EntryHash)] = effect;

    internal void MarkDiverged() => Diverged = true;
}
