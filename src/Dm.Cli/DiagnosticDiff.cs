using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;
using Dm.Core.Symbols;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Cli;

/// <summary>
/// Diffs our diagnostics against the ones <c>dm.exe</c> reports for the same project.
/// </summary>
/// <remarks>
/// <para>
/// The M11 oracle. Semantic diagnostics are the one area where being wrong is worse than being
/// absent: an error the compiler does not report reads as our bug, and a project that builds clean
/// while we complain is a tool nobody trusts. So every check added from here is measured against the
/// compiler from its first commit rather than after the fact, the same way <c>-o</c> measures the
/// object tree.
/// </para>
/// <para>
/// Compared on <c>file:line</c> and severity, never on the compiler's symbol column: it echoes the
/// offending text with whitespace stripped, so <c>return I.nothere</c> comes back as
/// <c>returnI.nothere</c>. Line-level is also the honest granularity — column conventions differ and
/// matching them would fail for reasons that have nothing to do with whether we found the same
/// problem.
/// </para>
/// </remarks>
internal static class DiagnosticDiff
{
    /// <summary>`file:line:error: sym: message`, or `file:line:warning (name): sym: message`.</summary>
    private static readonly Regex CompilerLine = new(
        @"^(?<file>.+?):(?<line>\d+):(?<severity>error|warning)(?:\s*\((?<name>[^)]+)\))?:\s*(?<message>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Why a deliberate divergence is here, for printing beside its count.
    /// </summary>
    /// <remarks>
    /// The set itself lives in <see cref="DeliberateDivergences"/> so the fixture tests use the same
    /// one. A second copy sat here until the tests were written, and the two disagreed immediately:
    /// a fixture that compiles clean failed a zero-invented check over a warning we meant to emit.
    /// </remarks>
    private static string Reason(string id)
        => DeliberateDivergences.TryGetReason(id, out string reason) ? reason : string.Empty;

    private readonly record struct Entry(string File, int Line, string Severity)
    {
        public override string ToString() => $"{File}:{Line} {Severity}";
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: diagdiff needs a .dme");
            return 1;
        }

        string dme = Path.GetFullPath(args[1]);
        string root = Path.GetDirectoryName(dme) ?? ".";
        string compiler = Program.OptionValue(args, "--dm") ?? DefaultCompiler();

        if (!File.Exists(compiler))
        {
            Console.Error.WriteLine($"error: dm.exe not found at {compiler}; pass --dm <path>");
            return 1;
        }

        Console.Out.WriteLine($"diagdiff {Path.GetFileName(dme)}");
        Console.Out.WriteLine();

        List<(Entry Key, string Message)> theirs = RunCompiler(compiler, dme, root, args);
        List<(Entry Key, string Message)> ours = Ours(dme, root, args);

        Console.Out.WriteLine($"  dm.exe   {theirs.Count} diagnostic(s)");
        Console.Out.WriteLine($"  ours     {ours.Count} diagnostic(s)");
        Console.Out.WriteLine();

        HashSet<Entry> theirKeys = theirs.Select(t => t.Key).ToHashSet();
        HashSet<Entry> ourKeys = ours.Select(o => o.Key).ToHashSet();

        List<(Entry Key, string Message)> missed = theirs.Where(t => !ourKeys.Contains(t.Key)).ToList();
        List<(Entry Key, string Message)> unmatched = ours.Where(o => !theirKeys.Contains(o.Key)).ToList();

        List<(Entry Key, string Message)> designed =
            unmatched.Where(o => DeliberateDivergences.Contains(IdOf(o.Message))).ToList();

        List<(Entry Key, string Message)> extra =
            unmatched.Where(o => !DeliberateDivergences.Contains(IdOf(o.Message))).ToList();

        Report("WE MISS", missed, args);
        Report("WE INVENT", extra, args);

        if (designed.Count > 0)
        {
            Console.Out.WriteLine($"  BY DESIGN ({designed.Count})");

            foreach (IGrouping<string, (Entry Key, string Message)> group in
                     designed.GroupBy(d => IdOf(d.Message)).OrderByDescending(g => g.Count()))
            {
                Console.Out.WriteLine($"    {group.Count(),6}  {group.Key}  {Reason(group.Key)}");
            }

            Console.Out.WriteLine();
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"  agreed   {theirs.Count - missed.Count}");
        Console.Out.WriteLine($"  missed   {missed.Count}");
        Console.Out.WriteLine($"  invented {extra.Count}");

        // Inventing is the one that matters. A diagnostic the compiler does not report is a project
        // that builds clean while we complain, and that is how a tool loses its users.
        if (extra.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("  Invented diagnostics are the failure to chase. Missing ones are");
            Console.Out.WriteLine("  M11 work still to do; invented ones are M11 work done wrong.");
        }

        if (Program.OptionValue(args, "--baseline") is { } baseline)
            return Baseline(baseline, dme, theirs.Count - missed.Count, missed.Count, extra.Count, args);

        return 0;
    }

    /// <summary>
    /// Checks the run against a recorded row, or rewrites it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same ratchet the mined probes carry, pointed at the <b>missed</b> column. Zero-invented
    /// is a standing rule that gets read every time; nothing played that role for missed, so nobody
    /// read it — including in a session where the number was printed, seen and reported as only the
    /// invented column. A number that fails a run is read; a number in a report is not.
    /// </para>
    /// <para>
    /// <b>This is a local gate and cannot be anything else.</b> The corpus projects are games on the
    /// author's disk, not in this repo, so CI can never run it. That is also why the key is the
    /// <c>.dme</c>'s file name rather than its path — paths differ per machine, names do not.
    /// </para>
    /// <para>
    /// Missed going UP is a regression and fails. Missed going DOWN is the point, and fails too,
    /// with a different message: it means a check landed and the row is now stale. Agreed going
    /// down is a check that stopped firing.
    /// </para>
    /// </remarks>
    private static int Baseline(
        string path, string dme, int agreed, int missed, int invented, string[] args)
    {
        string key = Path.GetFileName(dme);
        Dictionary<string, (int Agreed, int Missed, int Invented)> rows = new(StringComparer.OrdinalIgnoreCase);
        List<string> header = new();

        if (File.Exists(path))
        {
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.StartsWith('#') || line.Trim().Length == 0)
                {
                    if (rows.Count == 0)
                        header.Add(line);

                    continue;
                }

                string[] parts = line.Split('\t');

                if (parts.Length == 4
                    && int.TryParse(parts[1], out int a)
                    && int.TryParse(parts[2], out int m)
                    && int.TryParse(parts[3], out int i))
                {
                    rows[parts[0]] = (a, m, i);
                }
            }
        }

        if (args.Contains("--update"))
        {
            rows[key] = (agreed, missed, invented);

            if (header.Count == 0)
            {
                header.Add("# Per-project agreement with dm.exe. A RATCHET on the missed column.");
                header.Add("#");
                header.Add("# Local only: these are games on disk, not in this repo, so CI cannot run it.");
                header.Add("# Keyed by .dme file name, since paths differ per machine and names do not.");
                header.Add("#");
                header.Add("# Update deliberately, after reading why a number moved:");
                header.Add("#   dmc diagdiff <dme> --baseline <this file> --update");
                header.Add("#");
                header.Add("# dme\tagreed\tmissed\tinvented");
            }

            File.WriteAllLines(
                path,
                header.Concat(rows.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(r => $"{r.Key}\t{r.Value.Agreed}\t{r.Value.Missed}\t{r.Value.Invented}")));

            Console.Out.WriteLine();
            Console.Out.WriteLine($"  baseline updated: {key}  agreed {agreed}  missed {missed}  invented {invented}");
            return 0;
        }

        if (!rows.TryGetValue(key, out (int Agreed, int Missed, int Invented) was))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  no baseline row for {key}. Record one with --baseline <file> --update");
            return 1;
        }

        List<string> moved = new();

        if (invented > was.Invented)
            moved.Add($"invented {was.Invented} -> {invented} — a project that builds clean while we complain");

        if (missed > was.Missed)
            moved.Add($"missed {was.Missed} -> {missed} — a diagnostic we used to report and no longer do");

        if (missed < was.Missed)
            moved.Add($"missed {was.Missed} -> {missed} — a check landed; re-run with --update");

        if (agreed < was.Agreed)
            moved.Add($"agreed {was.Agreed} -> {agreed} — a check stopped firing");

        Console.Out.WriteLine();

        if (moved.Count == 0)
        {
            Console.Out.WriteLine($"  baseline holds: {key}  agreed {agreed}  missed {missed}  invented {invented}");
            return 0;
        }

        foreach (string line in moved)
            Console.Error.WriteLine($"  BASELINE MOVED: {line}");

        return 1;
    }

    private static void Report(string label, List<(Entry Key, string Message)> entries, string[] args)
    {
        if (entries.Count == 0)
            return;

        Console.Out.WriteLine($"  {label} ({entries.Count})");

        // Grouped by message, because a hundred instances of one cause is one thing to fix and the
        // list of a hundred lines is not what tells you that.
        IEnumerable<IGrouping<string, (Entry Key, string Message)>> groups = entries
            .GroupBy(e => Normalise(e.Message))
            .OrderByDescending(g => g.Count());

        bool verbose = args.Contains("--verbose");

        foreach (IGrouping<string, (Entry Key, string Message)> group in groups)
        {
            Console.Out.WriteLine($"    {group.Count(),6}  {group.Key}");

            if (verbose)
            {
                foreach ((Entry key, string _) in group.Take(5))
                    Console.Out.WriteLine($"            {key}");
            }
        }

        Console.Out.WriteLine();
    }

    /// <summary>Our diagnostics are carried as "ID message"; this recovers the ID.</summary>
    private static string IdOf(string message)
    {
        int space = message.IndexOf(' ');
        return space > 0 ? message[..space] : message;
    }

    /// <summary>Strips the parts that vary per occurrence, so a cause groups as one line.</summary>
    private static string Normalise(string message)
    {
        int colon = message.IndexOf(':');
        return colon >= 0 && colon + 1 < message.Length ? message[(colon + 1)..].Trim() : message.Trim();
    }

    /// <summary>Runs dm.exe over the project, with the same injected defines we were given.</summary>
    /// <remarks>
    /// The defines have to reach BOTH sides or the diff compares two different programs: anything
    /// behind an <c>#ifdef</c> is in one build and not the other, and every diagnostic inside it
    /// reads as missing or invented. /tg/station builds with <c>-DCBT</c>, so without this it is not
    /// measurable at all.
    /// </remarks>
    private static List<(Entry, string)> RunCompiler(string compiler, string dme, string root, string[] args)
    {
        string flags = string.Join(' ', args
            .Where(a => a.StartsWith("-D", StringComparison.Ordinal) && a.Length > 2)
            .Select(a => $"\"{a}\""));

        ProcessStartInfo start = new(compiler, $"{flags} \"{dme}\"".TrimStart())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        List<(Entry, string)> found = new();

        foreach (string line in output.Split('\n'))
        {
            Match match = CompilerLine.Match(line.Trim());

            if (!match.Success)
                continue;

            string file = match.Groups["file"].Value;

            // "loading src\file.dm" and similar carry no line number and do not reach here, but a
            // path with a colon in it would, so require the file to look like source.
            if (!file.EndsWith(".dm", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".dme", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found.Add((
                new Entry(Relative(root, file), int.Parse(match.Groups["line"].Value), match.Groups["severity"].Value),
                match.Groups["message"].Value));
        }

        return found;
    }

    private static List<(Entry, string)> Ours(string dme, string root, string[] args)
    {
        List<(Entry, string)> found = new();
        IncludeOptions options = Program.BuildOptions(args);
        PreprocessResult preprocessed = Preprocessor.Run(dme, options);

        foreach (Diagnostic diagnostic in preprocessed.Diagnostics)
        {
            if (!Comparable(diagnostic))
                continue;

            // A walk-time diagnostic now says which file it came from, so it can be compared at the
            // line dm.exe reports it on instead of collapsing onto the .dme at line 0 — which made
            // every one of them a guaranteed miss.
            string file = diagnostic.File ?? preprocessed.Graph.DmePath;
            int line = diagnostic.File is null ? 0 : LineOf(diagnostic.File, diagnostic.Span.Start);

            found.Add((
                new Entry(Relative(root, file), line, Severity(diagnostic)),
                $"{diagnostic.Id} {diagnostic.Message}"));
        }

        // Parse every file first, then bind. Binding needs the FINISHED tree: a type is reopened
        // across files and a proc is overridden in a later one, so a member checked against a
        // half-built tree would be reported missing purely because its file has not been read yet.
        List<(string File, TokenSource Source, ParseResult Parse)> files = new();
        ObjectTree tree = new();
        Builtins.Seed(tree);

        // The walk's pragma levels, so `#pragma ignore <name>` silences here exactly as it does in
        // the compiler. Without this the diff reports what the project asked us not to say.
        tree.SuppressedWarnings = preprocessed.Graph.Warnings;

        foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(preprocessed))
        {
            ParseResult parse = DeclarationParser.Parse(source);
            files.Add((file, source, parse));
            TypeTreeBuilder.AddFile(tree, file, parse);
        }

        foreach ((string file, TokenSource source, ParseResult parse) in files)
        {
            foreach (Diagnostic diagnostic in parse.Diagnostics)
                Add(file, source, diagnostic);

            foreach (Diagnostic diagnostic in Binder.Bind(tree, parse.Root, file))
                Add(file, source, diagnostic);
        }

        return found;

        void Add(string file, TokenSource source, Diagnostic diagnostic)
        {
            if (!Comparable(diagnostic))
                return;

            int line = source.Text.GetLinePosition(diagnostic.Span.Start).Line + 1;

            found.Add((
                new Entry(Relative(root, file), line, Severity(diagnostic)),
                $"{diagnostic.Id} {diagnostic.Message}"));
        }
    }

    /// <summary>
    /// Whether the compiler could have an opinion about this at all.
    /// </summary>
    /// <remarks>
    /// Information and hints are advisory and have no counterpart in a build log, so diffing them
    /// against it compares two different questions.
    /// </remarks>
    private static bool Comparable(Diagnostic diagnostic)
        => diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning;

    private static string Severity(Diagnostic diagnostic)
        => diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning";

    /// <summary>
    /// The 1-based line an offset falls on, for a walk-time diagnostic that names its own file.
    /// </summary>
    /// <remarks>
    /// Read through <see cref="SourceFileReader"/> rather than <c>File.ReadAllText</c>, since real
    /// projects contain Windows-1252 files and a mis-decoded byte moves every later offset. Cached
    /// because a project can raise many diagnostics in one file.
    /// </remarks>
    private static readonly Dictionary<string, SourceText> LineSources = new(StringComparer.OrdinalIgnoreCase);

    private static int LineOf(string file, int offset)
    {
        if (!LineSources.TryGetValue(file, out SourceText? text))
        {
            try
            {
                text = SourceFileReader.Read(file);
            }
            catch (IOException)
            {
                return 0;
            }

            LineSources[file] = text;
        }

        return text.GetLinePosition(offset).Line + 1;
    }

    private static string Relative(string root, string path)
    {
        string full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);

        return Path.GetRelativePath(root, full).Replace('\\', '/').ToLowerInvariant();
    }

    /// <summary>
    /// Where to find <c>dm.exe</c> when <c>--dm</c> was not passed.
    /// </summary>
    /// <remarks>
    /// <c>DM_BYOND_BIN</c> wins over the install location, which is the same order
    /// <c>FixtureTests.ByondBin</c> uses — the two halves of the harness have to agree about where
    /// BYOND is or one of them silently measures a different compiler. That is not hypothetical:
    /// this method used to consult the install location only, so a run against a standalone build
    /// compiled the fixtures with the new compiler and then diffed diagnostics against the old one.
    /// On a machine with no install at all — a CI runner — it failed for every fixture instead.
    /// </remarks>
    private static string DefaultCompiler()
    {
        string? overridden = Environment.GetEnvironmentVariable("DM_BYOND_BIN");

        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "BYOND", "bin", "dm.exe")
            : Path.Combine(overridden, "dm.exe");
    }
}
