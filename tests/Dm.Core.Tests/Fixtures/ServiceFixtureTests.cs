using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Fixtures;

/// <summary>
/// Tier 2 of the fixture suite: end-to-end service answers over a real workspace — the check that
/// <c>mob.</c> returns the right list, which no unit test covers because every unit test builds
/// its own tree. This opens the fixture the way an IDE does and asks the same questions.
/// </summary>
/// <remarks>
/// <para>
/// Positions are marked in the fixture source as comments — <c>//? complete 7:4 => hp, !reload</c>
/// — so adding a case is adding a line, never a code change, and every position is 1-based
/// line:column exactly as <c>dmc complete|definition|hover|signature</c> takes it: any failing
/// mark can be reproduced with the CLI verbatim. The grammar, documented in the fixture README:
/// <c>complete</c> lists names that must be present (<c>!name</c> must be absent,
/// <c>(empty)</c> the whole list); <c>definition</c> takes the nearest hit as
/// <c>file.dm:line</c>; <c>hover</c> takes the resolved detail; <c>signature</c> takes
/// <c>name @ activeParameter</c>.
/// </para>
/// <para>
/// The fixture projects also compile clean under <c>dm.exe</c> and are swept by the ordinary
/// fixture gates, so the same files that pin service answers also hold the zero-invented line.
/// </para>
/// </remarks>
public class ServiceFixtureTests
{
    private static string Root => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "services");

    private static readonly Regex Annotation = new(
        @"^\s*//\?\s+(?<verb>complete|definition|hover|signature|references)\s+(?<line>\d+):(?<col>\d+)\s*=>\s*(?<expected>.+?)\s*$",
        RegexOptions.Compiled);

    public static TheoryData<string> Fixtures()
    {
        TheoryData<string> data = new();

        foreach (string dme in Directory.EnumerateFiles(Root, "*.dme", SearchOption.AllDirectories))
            data.Add(Path.GetRelativePath(Root, dme).Replace('\\', '/'));

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_marked_position_answers_as_recorded(string relative)
    {
        string dme = Path.Combine(Root, relative);
        string directory = Path.GetDirectoryName(dme)!;

        using Workspace workspace = Workspace.Open(dme);

        List<string> failures = new();
        int marks = 0;

        foreach (string file in Directory.EnumerateFiles(directory, "*.dm"))
        {
            string name = Path.GetFileName(file);
            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                Match mark = Annotation.Match(lines[i]);

                if (!mark.Success)
                    continue;

                marks++;

                string? failure = Check(
                    workspace,
                    name,
                    mark.Groups["verb"].Value,
                    int.Parse(mark.Groups["line"].Value),
                    int.Parse(mark.Groups["col"].Value),
                    mark.Groups["expected"].Value);

                if (failure is not null)
                    failures.Add($"{name}:{i + 1} [{mark.Groups["verb"].Value} {mark.Groups["line"].Value}:{mark.Groups["col"].Value}] {failure}");
            }
        }

        // A fixture with no marks would pass by testing nothing — the probe-that-cannot-fail trap.
        Assert.True(marks > 0, $"{relative} has no //? annotations");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Runs one marked question and returns what went wrong, or null.</summary>
    private static string? Check(
        Workspace workspace, string file, string verb, int line, int column, string expected)
    {
        Document document = workspace.GetDocument(file);

        switch (verb)
        {
            case "complete":
            {
                CompletionResult result = CompletionService.CompleteAt(
                    workspace.GetObjectTree(),
                    document,
                    line - 1,
                    column - 1,
                    workspace.GetMacroNames(),
                    workspace.GetFileText);

                HashSet<string> names = result.Items
                    .Select(item => item.Name)
                    .ToHashSet(StringComparer.Ordinal);

                if (expected == "(empty)")
                    return names.Count == 0 ? null : $"expected no items, got {names.Count}";

                List<string> wrong = new();

                foreach (string entry in expected.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (entry.StartsWith('!'))
                    {
                        if (names.Contains(entry[1..]))
                            wrong.Add($"{entry[1..]} offered, must not be");
                    }
                    else if (!names.Contains(entry))
                    {
                        wrong.Add($"{entry} missing");
                    }
                }

                return wrong.Count == 0 ? null : string.Join("; ", wrong);
            }

            case "definition":
            {
                IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
                    workspace.GetObjectTree(), document, line - 1, column - 1);

                if (found.Count == 0)
                    return "nothing resolved";

                DefinitionLocation nearest = found[0];
                SourceText text = workspace.GetDocument(nearest.File).Text;
                LinePosition at = text.GetLinePosition(nearest.NameSpan.Start);
                string actual = $"{Path.GetFileName(nearest.File)}:{at.Line + 1}";

                return actual == expected ? null : $"resolved to {actual}, expected {expected}";
            }

            case "hover":
            {
                HoverResult? hover = HoverService.HoverAt(
                    workspace.GetObjectTree(), document, line - 1, column - 1);

                if (hover is null)
                    return "nothing to show";

                return hover.Detail == expected ? null : $"detail is '{hover.Detail}', expected '{expected}'";
            }

            case "signature":
            {
                SignatureHelpResult? help = SignatureHelpService.SignatureAt(
                    workspace.GetObjectTree(), document, line - 1, column - 1);

                if (help is null)
                    return "no enclosing call";

                string[] parts = expected.Split('@', StringSplitOptions.TrimEntries);
                string procName = parts[0];
                int active = int.Parse(parts[1]);

                if (help.Name != procName)
                    return $"resolved '{help.Name}', expected '{procName}'";

                return help.ActiveParameter == active
                    ? null
                    : $"active parameter {help.ActiveParameter}, expected {active}";
            }

            case "references":
            {
                ReferenceListing? found = ReferenceService.At(
                    workspace.GetObjectTree(),
                    workspace.GetProjectParses(),
                    document,
                    line - 1,
                    column - 1);

                if (found is null)
                    return "nothing at that position is an index symbol";

                List<string> actual = found.References
                    .Select(reference =>
                    {
                        SourceText text = workspace.GetDocument(reference.File).Text;
                        int hitLine = text.GetLinePosition(reference.Span.Start).Line + 1;

                        return $"{Path.GetFileName(reference.File)}:{hitLine} "
                            + reference.Kind.ToString().ToLowerInvariant();
                    })
                    .OrderBy(entry => entry, StringComparer.Ordinal)
                    .ToList();

                List<string> wanted = expected
                    .Split(',', StringSplitOptions.TrimEntries)
                    .OrderBy(entry => entry, StringComparer.Ordinal)
                    .ToList();

                // Exact set equality: a missing hit and a surplus one are both wrong answers.
                return actual.SequenceEqual(wanted)
                    ? null
                    : $"got [{string.Join("; ", actual)}], expected [{string.Join("; ", wanted)}]";
            }

            default:
                return $"unknown verb '{verb}'";
        }
    }
}
