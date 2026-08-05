using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;
using Dm.Core.Preprocessing;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Xunit;

namespace Dm.Core.Tests.Fixtures;

/// <summary>
/// Drives <c>tests/fixtures</c> from <c>dotnet test</c>, so there is one entry point.
/// </summary>
/// <remarks>
/// <para>
/// The point is a cheap BYOND upgrade. A new compiler version should be answerable by running the
/// suite and reading what moved — which errors and warnings changed, what syntax it now accepts,
/// what it now means at runtime — rather than by remembering that a second script exists. So the
/// fixtures are discovered from disk: <b>adding a case is adding a file</b>, never a code change.
/// </para>
/// <para>
/// Split by what each needs. The checks that run our own pipeline are in-process and need no BYOND,
/// so they run everywhere including CI. The ones that need <c>dm.exe</c> <b>skip</b> when it is
/// absent — never silently pass, which is why this project takes a dependency on
/// <c>Xunit.SkippableFact</c>: xunit 2.x cannot skip at runtime and a fixture that quietly did
/// nothing would read as green.
/// </para>
/// <para>
/// The heavy probe corpus stays behind <c>run.ps1 -Probes</c>: 252 compiles would turn a
/// sub-second suite into a minute. Its goldens are still re-checked on an upgrade, because
/// <see cref="The_goldens_match_the_installed_compiler"/> fails loudly the moment the installed
/// version leaves the one recorded, and points at the procedure.
/// </para>
/// </remarks>
public class FixtureTests
{
    private static string Root => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures");

    private static string ByondBin =>
        Environment.GetEnvironmentVariable("DM_BYOND_BIN")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "BYOND", "bin");

    private static string Compiler => Path.Combine(ByondBin, "dm.exe");

    /// <summary>Every fixture `.dme`, probes included.</summary>
    public static TheoryData<string> AllFixtures => Discover(includeProbes: true);

    /// <summary>The hand-written fixtures. Probes are the slow corpus; see the class remarks.</summary>
    public static TheoryData<string> HandWritten => Discover(includeProbes: false);

    private static TheoryData<string> Discover(bool includeProbes)
    {
        TheoryData<string> data = new();

        foreach (string dme in Directory.EnumerateFiles(Root, "*.dme", SearchOption.AllDirectories))
        {
            if (!includeProbes && Path.GetFileName(Path.GetDirectoryName(dme)) == "probes")
                continue;

            data.Add(Path.GetRelativePath(Root, dme).Replace('\\', '/'));
        }

        return data;
    }

    // -- our side: no compiler needed, so these run in CI ---------------------

    /// <summary>
    /// Every fixture goes through the whole pipeline without throwing.
    /// </summary>
    /// <remarks>
    /// Most of these files are malformed on purpose, which makes them the right robustness corpus:
    /// an editor buffer is broken on every keystroke, and M1 onwards promises we never throw on one.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Every_fixture_survives_the_pipeline(string relative)
    {
        IReadOnlyList<Diagnostic> ours = Analyse(Path.Combine(Root, relative));

        Assert.NotNull(ours);
    }

    /// <summary>
    /// A fixture recorded as compiling clean must draw nothing from us either.
    /// </summary>
    /// <remarks>
    /// This is the zero-invented rule where it actually belongs: on code the compiler accepts. On a
    /// deliberately broken file extra diagnostics are error recovery working, not spurious output.
    /// </remarks>
    [Theory]
    [MemberData(nameof(HandWritten))]
    public void We_invent_nothing_on_a_fixture_that_compiles_clean(string relative)
    {
        string dme = Path.Combine(Root, relative);

        if (Expected(dme).Count > 0)
            return;

        string[] complaints = Analyse(dme)
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .Where(d => !DeliberateDivergences.Contains(d.Id))
            .Select(d => $"{d.Id} {d.Message}")
            .ToArray();

        Assert.True(
            complaints.Length == 0,
            $"{relative} compiles clean; we report: {string.Join("; ", complaints)}");
    }

    // -- the compiler's side: skips when BYOND is absent ----------------------

    /// <summary>
    /// <c>dm.exe</c> still says what the fixture records.
    /// </summary>
    /// <remarks>
    /// Checks the golden rather than us, and that is the value on an upgrade: when a new BYOND
    /// changes a message, moves a line, or starts accepting something it rejected, this is what says
    /// so — per fixture, by name.
    /// </remarks>
    [SkippableTheory]
    [MemberData(nameof(HandWritten))]
    public void The_compiler_still_reports_what_the_fixture_records(string relative)
    {
        Skip.IfNot(File.Exists(Compiler), $"no dm.exe at {Compiler}");

        string dme = Path.Combine(Root, relative);
        string output = Compile(dme);
        List<string> wanted = Expected(dme);

        if (wanted.Count == 0)
        {
            Match summary = Regex.Match(output, @"(\d+) errors?, (\d+) warnings?");

            Assert.True(summary.Success, $"no compiler summary for {relative}:\n{output}");
            Assert.True(
                summary.Groups[1].Value == "0" && summary.Groups[2].Value == "0",
                $"{relative} should compile clean, got {summary.Value}");

            return;
        }

        foreach (string line in wanted)
        {
            string[] parts = line.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);

            Assert.True(
                output.Contains($":{parts[0]}:{parts[1]}", StringComparison.Ordinal)
                && output.Contains(parts[2], StringComparison.Ordinal),
                $"{relative} no longer reports '{line}':\n{output}");
        }
    }

    /// <summary>
    /// The goldens were captured from the compiler that is installed.
    /// </summary>
    /// <remarks>
    /// The upgrade tripwire, and the reason the rest of this file can be trusted. Every
    /// <c>.expected</c> in the tree is version-specific data; comparing them against a different
    /// compiler silently measures the wrong thing. When this fails, the answer is not to edit a
    /// number — it is to re-run the suite and read what the new version changed.
    /// </remarks>
    [SkippableFact]
    public void The_goldens_match_the_installed_compiler()
    {
        Skip.IfNot(File.Exists(Compiler), $"no dm.exe at {Compiler}");

        string recorded = File.ReadAllText(Path.Combine(Root, "BYOND_VERSION.txt")).Trim();
        string output = Compile(Path.Combine(Root, "ok", "ok.dme"));
        Match found = Regex.Match(output, @"DM compiler version (\S+)");

        Assert.True(found.Success, $"could not read the compiler version:\n{output}");

        Assert.True(
            found.Groups[1].Value == recorded,
            $"goldens were captured from BYOND {recorded}, installed is {found.Groups[1].Value}. "
            + "This is an upgrade, not a bug: re-run `pwsh tests/fixtures/run.ps1 -Probes`, read "
            + "every difference, then update the .expected files and BYOND_VERSION.txt together.");
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>Runs our whole pipeline over a `.dme`, as `dmc diagdiff` does.</summary>
    private static IReadOnlyList<Diagnostic> Analyse(string dme)
    {
        List<Diagnostic> found = new();
        PreprocessResult preprocessed = Preprocessor.Run(dme, new IncludeOptions());

        found.AddRange(preprocessed.Diagnostics);

        List<(string File, ParseResult Parse)> files = new();
        ObjectTree tree = new();
        Builtins.Seed(tree);

        foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(preprocessed))
        {
            ParseResult parse = DeclarationParser.Parse(source);
            files.Add((file, parse));
            TypeTreeBuilder.AddFile(tree, file, parse);
        }

        foreach ((string _, ParseResult parse) in files)
        {
            found.AddRange(parse.Diagnostics);
            found.AddRange(Binder.Bind(tree, parse.Root));
        }

        return found;
    }

    /// <summary>The `line severity text` rows beside a fixture; comments and blanks dropped.</summary>
    private static List<string> Expected(string dme)
    {
        string path = Path.ChangeExtension(dme, ".expected");

        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToList();
    }

    private static string Compile(string dme)
    {
        ProcessStartInfo start = new(Compiler, $"\"{dme}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dme)!,
        };

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return output;
    }
}
