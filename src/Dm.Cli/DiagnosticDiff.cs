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

        return 0;
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

            found.Add((
                new Entry(Relative(root, preprocessed.Graph.DmePath), 0, Severity(diagnostic)),
                $"{diagnostic.Id} {diagnostic.Message}"));
        }

        // Parse every file first, then bind. Binding needs the FINISHED tree: a type is reopened
        // across files and a proc is overridden in a later one, so a member checked against a
        // half-built tree would be reported missing purely because its file has not been read yet.
        List<(string File, TokenSource Source, ParseResult Parse)> files = new();
        ObjectTree tree = new();
        Builtins.Seed(tree);

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

    private static string Relative(string root, string path)
    {
        string full = Path.IsPathRooted(path) ? path : Path.Combine(root, path);

        return Path.GetRelativePath(root, full).Replace('\\', '/').ToLowerInvariant();
    }

    private static string DefaultCompiler()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "BYOND", "bin", "dm.exe");
}
