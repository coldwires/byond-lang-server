using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dm.Core.Includes;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Includes;

/// <summary>
/// The sort order against whole <c>.dme</c> files DreamMaker itself wrote.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests pin four hand-picked orderings; this pins <b>every</b> entry in a real project.
/// DreamMaker keeps its block sorted, so re-sorting the block with our comparator must reproduce
/// the file byte for byte — a single wrong rule shows up as a diff across hundreds of lines.
/// </para>
/// <para>
/// Skipped rather than failed when the games are not on this machine: they are the author's
/// checkouts, not repo fixtures, and a test that quietly passed without them would be measuring
/// nothing. Same reasoning as the <c>dm.exe</c>-dependent fixtures.
/// </para>
/// </remarks>
public class DmeSortAgainstRealProjectsTests
{
    private static string Games
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "GitHub");

    public static TheoryData<string> Projects() => new()
    {
        Path.Combine(Games, "mlaas", "spies.dme"),
        Path.Combine(Games, "madridspy", "hell.dme"),
        Path.Combine(Games, "warklan", "Klan Wars.dme"),
        Path.Combine(Games, "tgstation", "tgstation.dme"),
    };

    [SkippableTheory]
    [MemberData(nameof(Projects))]
    public void Our_order_reproduces_DreamMakers_own(string dmePath)
    {
        Skip.IfNot(File.Exists(dmePath), $"no .dme at {dmePath}");

        SourceText dme = SourceText.From(File.ReadAllText(dmePath), dmePath);

        List<string> asWritten = DmeIncludeBlock.Entries(dme).Select(e => e.Path).ToList();
        Skip.If(asWritten.Count < 10, "too few entries to discriminate the sort rules");

        List<string> resorted = new(asWritten);
        resorted.Sort(DmeIncludeBlock.Compare);

        // Report the first divergence rather than a wall of paths: one wrong rule moves many
        // entries, and the first pair that moved is what names the rule.
        for (int i = 0; i < asWritten.Count; i++)
        {
            Assert.True(
                string.Equals(asWritten[i], resorted[i], StringComparison.Ordinal),
                $"{Path.GetFileName(dmePath)} entry {i}: DreamMaker wrote '{asWritten[i]}', "
                + $"our order puts '{resorted[i]}' there");
        }
    }
}
