using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Dm.Core;
using Dm.Core.Includes;
using Dm.Core.Preprocessing;
using Dm.Core.Services;
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
/// the two numbers that decide whether the library is usable in an editor: what a project costs to
/// open, and what one keystroke costs afterwards. The second is the one that matters — a client
/// pushes a buffer on every edit and then asks a question, and today that discards the whole tree.
/// </para>
/// <para>
/// Each phase is also broken down, because "slow" is not actionable. The preprocessor walk, the
/// parse and the tree merge are separately timed so the next change targets whichever dominates.
/// </para>
/// </remarks>
internal static class Bench
{
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

        Console.Out.WriteLine($"bench {Path.GetFileName(dmePath)}");
        Console.Out.WriteLine();

        // -- cold: what opening a project costs --------------------------------
        Stopwatch total = Stopwatch.StartNew();

        using Workspace workspace = Workspace.Open(dmePath, defines);

        Stopwatch phase = Stopwatch.StartNew();
        IncludeOptions options = new() { Defines = defines };
        PreprocessResult preprocessed = Preprocessor.Run(dmePath, options);
        long preprocessMs = phase.ElapsedMilliseconds;

        phase.Restart();
        List<(string File, TokenSource Source)> runs = PreprocessedSplitter.Split(preprocessed);
        long splitMs = phase.ElapsedMilliseconds;

        phase.Restart();
        List<(string File, ParseResult Parse)> parsed = new(runs.Count);
        foreach ((string file, TokenSource source) in runs)
            parsed.Add((file, DeclarationParser.Parse(source)));
        long parseMs = phase.ElapsedMilliseconds;

        phase.Restart();
        ObjectTree tree = new();
        Builtins.Seed(tree);
        foreach ((string file, ParseResult parse) in parsed)
            TypeTreeBuilder.AddFile(tree, file, parse);
        long treeMs = phase.ElapsedMilliseconds;

        long coldMs = total.ElapsedMilliseconds;

        Console.Out.WriteLine($"  files                {runs.Count}");
        Console.Out.WriteLine($"  types                {tree.Count}");
        Console.Out.WriteLine();
        Console.Out.WriteLine($"  preprocess           {preprocessMs,6} ms");
        Console.Out.WriteLine($"  split                {splitMs,6} ms");
        Console.Out.WriteLine($"  parse                {parseMs,6} ms");
        Console.Out.WriteLine($"  build tree           {treeMs,6} ms");
        Console.Out.WriteLine($"  COLD TOTAL           {coldMs,6} ms");
        Console.Out.WriteLine();

        // -- warm: what one keystroke costs ------------------------------------
        //
        // The shape a client actually produces: push the buffer for the file being edited, then ask
        // a question. Whether the edit changed anything semantic is not the point — the client
        // pushes on every keystroke either way.
        string? target = Program.OptionValue(args, "--file") ?? FirstSourceFile(runs, dmePath);

        if (target is null)
        {
            Console.Error.WriteLine("no source file to edit; pass --file");
            return 1;
        }

        string original = File.ReadAllText(target);

        // Prime it, so the first timed round measures an edit rather than the initial build.
        workspace.SetBuffer(target, original);
        workspace.GetObjectTree();

        List<long> warm = new(rounds);

        for (int i = 0; i < rounds; i++)
        {
            // A comment is the cheapest possible edit: nothing downstream can change, so this is the
            // floor for what an edit costs, not an average.
            workspace.SetBuffer(target, original + $"\n// bench {i}\n");

            Stopwatch round = Stopwatch.StartNew();
            workspace.GetObjectTree();
            warm.Add(round.ElapsedMilliseconds);
        }

        Console.Out.WriteLine($"  edited file          {Path.GetFileName(target)}");
        Console.Out.WriteLine($"  warm rebuilds        {string.Join(" ms, ", warm)} ms");
        Console.Out.WriteLine();

        long best = long.MaxValue;
        foreach (long ms in warm)
            best = Math.Min(best, ms);

        Console.Out.WriteLine($"  ONE KEYSTROKE        {best,6} ms   (target: under 30)");

        if (best > 30)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(
                "  Every edit rebuilds the whole project. That is what M9 is for; the number above is");
            Console.Out.WriteLine(
                "  the baseline to beat, and the phase breakdown says which part to attack first.");
        }

        return 0;
    }

    /// <summary>The first real source file in compile order, which is a fair stand-in for "a file the user has open".</summary>
    private static string? FirstSourceFile(List<(string File, TokenSource Source)> runs, string dmePath)
    {
        foreach ((string file, TokenSource _) in runs)
        {
            if (!string.Equals(file, dmePath, StringComparison.OrdinalIgnoreCase)
                && file.EndsWith(".dm", StringComparison.OrdinalIgnoreCase)
                && File.Exists(file))
            {
                return file;
            }
        }

        return null;
    }
}
