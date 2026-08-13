using System;
using System.Collections.Generic;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// One file's tokens, which may be a run adopted whole from a cache or one being appended to.
/// </summary>
/// <remarks>
/// A replayed file contributes exactly the run it contributed last time, so the cache can hand that
/// array over and the collector can take it by reference. Growing one afterwards is possible — a
/// file included twice under <c>#pragma multiple</c> does exactly that — so an adopted run is
/// copied into a list the first time anything appends to it, and not before.
/// </remarks>
internal sealed class RunState
{
    private List<ExpandedToken>? _mutable;

    public RunState(IReadOnlyList<ExpandedToken> adopted) => Tokens = adopted;

    public RunState()
    {
        _mutable = new List<ExpandedToken>();
        Tokens = _mutable;
    }

    public IReadOnlyList<ExpandedToken> Tokens { get; private set; }

    public void Append(IReadOnlyList<ExpandedToken> tokens)
    {
        if (_mutable is null)
        {
            _mutable = new List<ExpandedToken>(Tokens);
            Tokens = _mutable;
        }

        for (int i = 0; i < tokens.Count; i++)
            _mutable.Add(tokens[i]);
    }
}

/// <summary>One file's worth of the preprocessed stream.</summary>
internal sealed class PreprocessedFile
{
    private readonly RunState _state;

    internal PreprocessedFile(SourceText origin, RunState state)
    {
        Origin = origin;
        _state = state;
    }

    /// <summary>The file these tokens were <b>written</b> in, which for a macro is the invocation site.</summary>
    public SourceText Origin { get; }

    /// <summary>This file's tokens, gathered, in the order they were emitted.</summary>
    public IReadOnlyList<ExpandedToken> Tokens => _state.Tokens;

    public override string ToString() => $"{Origin.Path} ({Tokens.Count} tokens)";
}

/// <summary>
/// Collects the preprocessed stream as one run per file, in compile order.
/// </summary>
/// <remarks>
/// <para>
/// The parser works a file at a time, so gathering per file is what it wants — and the walk already
/// knows which file it is in, so the grouping is free here. It used to be done afterwards by
/// regrouping the whole project's tokens by origin, which on /tg/station cost as much as parsing
/// them.
/// </para>
/// <para>
/// True emission order is still recoverable. A file's tokens are interrupted by its includes, so the
/// gathered runs alone cannot say what came before what across files; <see cref="Segments"/> records
/// each contiguous stretch as it is emitted, which is what <see cref="Flatten"/> replays. That keeps
/// the compile-order view available for anything that wants the stream as the compiler sees it,
/// without holding the tokens twice.
/// </para>
/// </remarks>
internal sealed class RunCollector
{
    private readonly Dictionary<SourceText, RunState> _byOrigin = new();
    private readonly List<PreprocessedFile> _order = new();
    private readonly List<(SourceText Origin, int Start, int Count)> _segments = new();

    public IReadOnlyList<PreprocessedFile> Files => _order;

    public IReadOnlyList<(SourceText Origin, int Start, int Count)> Segments => _segments;

    /// <summary>How many tokens this file has contributed so far.</summary>
    public int LengthOf(SourceText origin)
        => _byOrigin.TryGetValue(origin, out RunState? state) ? state.Tokens.Count : 0;

    /// <summary>Appends a contiguous stretch of tokens emitted while walking one file.</summary>
    public void Append(SourceText origin, IReadOnlyList<ExpandedToken> tokens)
    {
        if (tokens.Count == 0)
            return;

        if (!_byOrigin.TryGetValue(origin, out RunState? state))
        {
            state = new RunState();
            _byOrigin.Add(origin, state);
            _order.Add(new PreprocessedFile(origin, state));
        }

        _segments.Add((origin, state.Tokens.Count, tokens.Count));
        state.Append(tokens);
    }

    /// <summary>
    /// Takes a file's whole run by reference, for a file being replayed from cache.
    /// </summary>
    /// <remarks>
    /// The point of the effect cache is that a replayed file does no work, and copying its tokens
    /// into a fresh list is work — on /tg/station it was around 10M copies per rebuild. Adopting is
    /// only possible when the file has contributed nothing yet, which is every case except a file
    /// re-entered under <c>#pragma multiple</c>; that one falls back to appending.
    /// </remarks>
    public bool TryAdopt(SourceText origin, IReadOnlyList<ExpandedToken> run)
    {
        // A file that contributed nothing has no run to adopt, and creating an empty one would put
        // a file in the list that a build without the cache never lists — a header of nothing but
        // directives is the ordinary case. Appending has the same rule, by returning early on an
        // empty stretch.
        if (run.Count == 0)
            return false;

        if (_byOrigin.ContainsKey(origin))
            return false;

        RunState state = new(run);
        _byOrigin.Add(origin, state);
        _order.Add(new PreprocessedFile(origin, state));

        return true;
    }

    /// <summary>
    /// Records that a stretch of an adopted run belongs here in compile order.
    /// </summary>
    /// <remarks>
    /// Adoption places a file's tokens in one go, but the order they interleave with its includes is
    /// still per stretch — so the segments are replayed separately from the tokens themselves.
    /// </remarks>
    public void AddSegment(SourceText origin, int start, int count)
    {
        if (count > 0)
            _segments.Add((origin, start, count));
    }

    /// <summary>Copies out what a file has contributed since an index, which is its recorded run.</summary>
    public ExpandedToken[] Slice(SourceText origin, int start)
    {
        if (!_byOrigin.TryGetValue(origin, out RunState? state) || start >= state.Tokens.Count)
            return Array.Empty<ExpandedToken>();

        IReadOnlyList<ExpandedToken> tokens = state.Tokens;
        ExpandedToken[] slice = new ExpandedToken[tokens.Count - start];

        for (int i = 0; i < slice.Length; i++)
            slice[i] = tokens[start + i];

        return slice;
    }

    /// <summary>
    /// The whole project in true compile order, rebuilt from the segments.
    /// </summary>
    /// <remarks>
    /// Materialised on demand rather than kept, because the consumers that want this view — a token
    /// dump, a count — ask once, while the parser asks for the per-file runs on every rebuild.
    /// </remarks>
    public List<ExpandedToken> Flatten()
    {
        int total = 0;

        foreach ((SourceText _, int _, int count) in _segments)
            total += count;

        List<ExpandedToken> flat = new(total);

        foreach ((SourceText origin, int start, int count) in _segments)
        {
            IReadOnlyList<ExpandedToken> run = _byOrigin[origin].Tokens;

            for (int i = 0; i < count; i++)
                flat.Add(run[start + i]);
        }

        return flat;
    }
}
