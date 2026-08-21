using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Dm.Core;
using Dm.Core.Includes;
using Dm.Core.Preprocessing;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Cli;

/// <summary>
/// Times the pipeline the way an editor drives it.
/// </summary>
/// <remarks>
/// <para>
/// M9 is about making an edit cheap, and an optimisation without a baseline is a guess. This reports
/// what a project costs to open and then what one keystroke costs afterwards — the second being the
/// number that decides whether the library is usable in an editor.
/// </para>
/// <para>
/// Both are broken down by phase, and the <b>warm</b> breakdown is the one to optimise against.
/// Measuring only the cold build sends you after whatever dominates a first run, which is not
/// necessarily what dominates the thousandth.
/// </para>
/// <para>
/// The pipeline here is assembled from the same public pieces <see cref="Workspace.GetObjectTree"/>
/// uses, in the same order, so a phase measured here is a phase that ships. The edit made between
/// rounds is a single new declaration appended to one file: enough that the file's tokens really
/// change, and no more, so the number is the <b>floor</b> for a real edit rather than an average.
/// A comment would be cheaper still and would measure nothing — comments never reach the token
/// stream, so an edit made of one leaves every cache hitting.
/// </para>
/// </remarks>
internal static class Bench
{
    private readonly record struct Phases(
        long Preprocess, long SplitParse, long Tree, long Total,
        int Files, int Types, int Reads, int Parses, int Walks);

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: bench needs a .dme");
            return 1;
        }

        string dmePath = args[1];
        int rounds = Program.OptionValue(args, "--rounds") is { } text && int.TryParse(text, out int given)
            ? given
            : 3;

        IReadOnlyList<string>? defines = Program.BuildOptions(args).Defines;

        // Shared across rounds, exactly as a Workspace shares it across rebuilds.
        SourceCache sources = new();
        ExpandedRunCache runs = new();
        FileEffectCache effects = new();
        Dictionary<string, SourceText> buffers = new(StringComparer.OrdinalIgnoreCase);

        // The contribution cache belongs in that list and was missing from it until 2026-08-18,
        // which made the tree phase measure a DIFFERENT PATH from the product: `AddFile` contributes
        // and applies in one call, so every round re-walked all 7,160 ASTs, while a Workspace holds
        // a ConditionalWeakTable keyed by ParseResult and re-walks only what was re-parsed. The
        // phase read ~1,160 ms on /tg/station that way and is the number the roadmap quoted as the
        // merge's cost. Keyed the same way here, so the rounds measure what an editor pays.
        ConditionalWeakTable<ParseResult, TreeContribution> contributions = new();

        Console.Out.WriteLine($"bench {Path.GetFileName(dmePath)}");
        Console.Out.WriteLine();

        Phases cold = Build(dmePath, defines, sources, runs, effects, buffers, contributions);

        Console.Out.WriteLine($"  files                {cold.Files}");
        Console.Out.WriteLine($"  types                {cold.Types}");
        Console.Out.WriteLine();
        Report("COLD", cold);

        string? target = Program.OptionValue(args, "--file") ?? FirstSourceFile(dmePath, defines);

        if (target is null)
        {
            Console.Error.WriteLine("no source file to edit; pass --file");
            return 1;
        }

        // Canonical form, because the walk hands the provider a full Windows path and a buffer keyed
        // any other way silently never matches — which measures a rebuild with nothing edited.
        target = Path.GetFullPath(target);
        string original = File.ReadAllText(target);
        Phases best = default;
        long bestTotal = long.MaxValue;

        for (int i = 0; i < rounds; i++)
        {
            // A real declaration, not a comment. Comments never reach the token stream, so an edit
            // made of one changes nothing downstream and every cache hits — which measures the
            // machinery rather than an edit.
            buffers[target] = SourceText.From(original + $"\n/obj/bench_marker_{i}\n", target);

            Phases warm = Build(dmePath, defines, sources, runs, effects, buffers, contributions);

            if (warm.Total < bestTotal)
            {
                bestTotal = warm.Total;
                best = warm;
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"  edited file          {Path.GetFileName(target)}");
        Console.Out.WriteLine($"  rounds               {rounds}, reporting the fastest");
        Console.Out.WriteLine();
        Report("ONE KEYSTROKE", best);

        Console.Out.WriteLine();
        Console.Out.WriteLine($"  files read from disk {best.Reads} of {best.Files}");
        Console.Out.WriteLine($"  files re-parsed      {best.Parses} of {best.Files}");
        Console.Out.WriteLine($"  files re-walked      {best.Walks} of {best.Files}");

        // The edit has to reach the walk, or every number above is a rebuild of nothing.
        if (best.Parses == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "  WARNING: nothing was re-parsed, so the edit never reached the preprocessor.");
            Console.Error.WriteLine(
                $"  Check that {target} is a file the .dme actually includes.");
        }

        if (Array.IndexOf(args, "--verify") >= 0)
            Verify(dmePath, defines, buffers);

        if (best.Total > 30)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("  Target is 30 ms. Attack whichever phase above is largest — the cold");
            Console.Out.WriteLine("  breakdown is a different question and will point somewhere else.");
        }

        return 0;
    }

    /// <summary>
    /// One full build, timed by phase.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="Workspace.GetObjectTree"/>: the preprocessed stream, split per file, parsed,
    /// merged in compile order, with pushed buffers winning over disk and the lex cache serving
    /// everything that has not changed.
    /// </remarks>
    private static Phases Build(
        string dmePath,
        IReadOnlyList<string>? defines,
        SourceCache sources,
        ExpandedRunCache runs,
        FileEffectCache effects,
        Dictionary<string, SourceText> buffers,
        ConditionalWeakTable<ParseResult, TreeContribution> contributions)
    {
        sources.ResetStatistics();
        runs.ResetStatistics();
        effects.ResetStatistics();
        Stopwatch total = Stopwatch.StartNew();
        Stopwatch phase = Stopwatch.StartNew();

        IncludeOptions options = new()
        {
            Defines = defines,
            SourceProvider = path => buffers.TryGetValue(path, out SourceText? open) ? open : sources.Read(path),
            LexProvider = (path, text) => sources.Lex(path, text),
            Effects = effects,
        };

        PreprocessResult preprocessed = Preprocessor.Run(dmePath, options);
        long preprocessMs = phase.ElapsedMilliseconds;

        // Split and parse are one phase now: the cache answers both together, since skipping a
        // parse means skipping the token source it would have been built from.
        phase.Restart();
        List<(string File, TokenSource Source, ParseResult Parse)> parsed =
            PreprocessedSplitter.SplitAndParse(preprocessed, runs);
        long splitParseMs = phase.ElapsedMilliseconds;

        phase.Restart();
        ObjectTree tree = new();
        Builtins.Seed(tree);

        foreach ((string file, TokenSource _, ParseResult parse) in parsed)
        {
            // A contribution is a pure function of (file, parse), so an unchanged file replays its
            // recorded mutations instead of re-walking its AST. `AddFile` does both in one call and
            // is what this loop used to call, which measured a walk the product does not perform.
            if (!contributions.TryGetValue(parse, out TreeContribution? contribution))
            {
                contribution = TypeTreeBuilder.Contribute(file, parse);
                contributions.Add(parse, contribution);
            }

            contribution.Apply(tree);
        }

        long treeMs = phase.ElapsedMilliseconds;

        return new Phases(
            preprocessMs, splitParseMs, treeMs, total.ElapsedMilliseconds,
            parsed.Count, tree.Count, sources.Misses, runs.Misses, effects.Misses);
    }

    /// <summary>
    /// Builds the same edited project with every cache disabled and compares the two trees.
    /// </summary>
    /// <remarks>
    /// The caches are only worth having if a cached build is the build. Unit tests cover the shapes
    /// that are easy to get wrong; this covers the one that matters — a real project, edited, with
    /// thousands of files replayed — and it compares owner-and-name pairs rather than counts, since
    /// two trees can agree on totals and disagree about everything in them.
    /// </remarks>
    private static void Verify(
        string dmePath, IReadOnlyList<string>? defines, Dictionary<string, SourceText> buffers)
    {
        Console.Out.WriteLine();
        Console.Out.WriteLine("  verifying against an uncached build of the same text...");

        ObjectTree cached = BuildTree(dmePath, defines, buffers, new SourceCache(), new ExpandedRunCache(), new FileEffectCache());
        ObjectTree fresh = BuildTree(dmePath, defines, buffers, new SourceCache(), new ExpandedRunCache(), null);

        List<string> a = Flatten(cached);
        List<string> b = Flatten(fresh);

        if (a.Count == b.Count && !a.Where((line, i) => line != b[i]).Any())
        {
            Console.Out.WriteLine($"  VERIFIED: {a.Count} declarations identical");
            return;
        }

        Console.Error.WriteLine($"  MISMATCH: cached {a.Count} declarations, uncached {b.Count}");

        foreach (string line in a.Except(b).Take(5))
            Console.Error.WriteLine($"    only in cached:   {line}");

        foreach (string line in b.Except(a).Take(5))
            Console.Error.WriteLine($"    only in uncached: {line}");
    }

    private static ObjectTree BuildTree(
        string dmePath,
        IReadOnlyList<string>? defines,
        Dictionary<string, SourceText> buffers,
        SourceCache sources,
        ExpandedRunCache runs,
        FileEffectCache? effects)
    {
        // Two passes when a cache is in play, so the second one is the one being checked: a cache
        // that is empty has nothing to get wrong.
        ObjectTree tree = new();

        for (int pass = 0; pass < (effects is null ? 1 : 2); pass++)
        {
            IncludeOptions options = new()
            {
                Defines = defines,
                SourceProvider = path => buffers.TryGetValue(path, out SourceText? open) ? open : sources.Read(path),
                LexProvider = (path, text) => sources.Lex(path, text),
                Effects = effects,
            };

            tree = new ObjectTree();
            Builtins.Seed(tree);

            foreach ((string file, TokenSource _, ParseResult parse) in
                     PreprocessedSplitter.SplitAndParse(Preprocessor.Run(dmePath, options), runs))
            {
                TypeTreeBuilder.AddFile(tree, file, parse);
            }
        }

        return tree;
    }

    /// <summary>Every declaration as "owner name", sorted, which is what a diff can be read from.</summary>
    private static List<string> Flatten(ObjectTree tree)
    {
        List<string> lines = new();

        foreach (TypeSymbol type in tree.Types)
        {
            lines.Add($"type {type.Path.Text}");

            foreach (VarSymbol variable in type.Vars)
                lines.Add($"var {type.Path.Text} {variable.Name}");

            foreach (ProcSymbol proc in type.Procs)
                lines.Add($"proc {type.Path.Text} {proc.Name}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void Report(string label, Phases phases)
    {
        Console.Out.WriteLine($"  preprocess           {phases.Preprocess,6} ms");
        Console.Out.WriteLine($"  split + parse        {phases.SplitParse,6} ms");
        Console.Out.WriteLine($"  build tree           {phases.Tree,6} ms");
        Console.Out.WriteLine($"  {label,-19}{phases.Total,7} ms");
    }

    /// <summary>The first real source file in compile order, as a stand-in for a file the user has open.</summary>
    private static string? FirstSourceFile(string dmePath, IReadOnlyList<string>? defines)
    {
        IncludeGraph graph = IncludeGraph.Build(dmePath, new IncludeOptions { Defines = defines });

        foreach (IncludedFile file in graph.Files)
        {
            if (file.Kind == IncludeKind.DmSource
                && !string.Equals(file.Path, dmePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(file.Path))
            {
                return file.Path;
            }
        }

        return null;
    }
}
