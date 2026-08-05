using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;
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
    /// Diagnostics we emit on purpose that <c>dm.exe</c> has no opinion about.
    /// </summary>
    /// <remarks>
    /// Without this the invented column is never empty, and a column that is never empty is one
    /// people stop reading — which is the whole value of the diff. Anything listed here is a
    /// deliberate divergence with a reason, not a gap to close. Adding to it should feel expensive.
    /// </remarks>
    private static readonly Dictionary<string, string> ByDesign = new()
    {
        ["DM0102"] = "duplicate include - the compiler ignores the repeat silently, we surface it",
        ["DM0300"] = "proc block inside a var block - compiles clean and declares nothing",
    };

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

        List<(Entry Key, string Message)> theirs = RunCompiler(compiler, dme, root);
        List<(Entry Key, string Message)> ours = Ours(dme, root, args);

        Console.Out.WriteLine($"  dm.exe   {theirs.Count} diagnostic(s)");
        Console.Out.WriteLine($"  ours     {ours.Count} diagnostic(s)");
        Console.Out.WriteLine();

        HashSet<Entry> theirKeys = theirs.Select(t => t.Key).ToHashSet();
        HashSet<Entry> ourKeys = ours.Select(o => o.Key).ToHashSet();

        List<(Entry Key, string Message)> missed = theirs.Where(t => !ourKeys.Contains(t.Key)).ToList();
        List<(Entry Key, string Message)> unmatched = ours.Where(o => !theirKeys.Contains(o.Key)).ToList();

        List<(Entry Key, string Message)> designed =
            unmatched.Where(o => ByDesign.ContainsKey(IdOf(o.Message))).ToList();

        List<(Entry Key, string Message)> extra =
            unmatched.Where(o => !ByDesign.ContainsKey(IdOf(o.Message))).ToList();

        Report("WE MISS", missed, args);
        Report("WE INVENT", extra, args);

        if (designed.Count > 0)
        {
            Console.Out.WriteLine($"  BY DESIGN ({designed.Count})");

            foreach (IGrouping<string, (Entry Key, string Message)> group in
                     designed.GroupBy(d => IdOf(d.Message)).OrderByDescending(g => g.Count()))
            {
                Console.Out.WriteLine($"    {group.Count(),6}  {group.Key}  {ByDesign[group.Key]}");
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

    private static List<(Entry, string)> RunCompiler(string compiler, string dme, string root)
    {
        ProcessStartInfo start = new(compiler, $"\"{dme}\"")
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

        foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(preprocessed))
        {
            ParseResult parse = DeclarationParser.Parse(source);

            foreach (Diagnostic diagnostic in parse.Diagnostics)
            {
                if (!Comparable(diagnostic))
                    continue;

                int line = source.Text.GetLinePosition(diagnostic.Span.Start).Line + 1;

                found.Add((
                    new Entry(Relative(root, file), line, Severity(diagnostic)),
                    $"{diagnostic.Id} {diagnostic.Message}"));
            }
        }

        return found;
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
