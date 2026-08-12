using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Dm.Core.Tests;

/// <summary>
/// The countable facts the live docs state, checked against the code that decides them.
/// </summary>
/// <remarks>
/// <para>
/// Seven doc syncs have now found the same shape — a fact stated in four places, one of them a
/// version behind — and each concluded "follow the rule". That has not worked, so this is the rule
/// as a gate instead. It is the same instinct as the probe ratchet and the corpus baseline: make
/// the thing that keeps going wrong impossible to get wrong silently.
/// </para>
/// <para>
/// Only facts that change DELIBERATELY are checked: the ABI version and the export count. Test
/// counts, binary sizes and check counts move on nearly every commit, and PLAN.md's own rule is
/// that a number which invalidates itself does not belong in a live doc at all — the commit count
/// was removed from both roadmap docs for exactly that reason.
/// </para>
/// <para>
/// The anchors are distinctive phrases rather than "the first match in the file". These docs
/// legitimately name older versions in their history: PLAN.md's changelog says "ABI 0.22, 36
/// exports" and that entry is correct and must stay. A gate that flagged it would be trained away
/// within a week.
/// </para>
/// <para>
/// The specific failure this would have caught: ROADMAP.txt once said "37 exports" when Exports.cs
/// had 36 and always had — a number that was never right rather than merely stale, found by hand
/// on the fifth sync.
/// </para>
/// </remarks>
public sealed class DocConsistencyTests
{
    private static string Version => $"{DmAbi.Major}.{DmAbi.Minor}";

    /// <summary>The exports are the ABI: one <c>[UnmanagedCallersOnly]</c> each, counted at source.</summary>
    private static int Exports => Regex.Matches(
        File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "src", "Dm.Native", "Exports.cs")),
        @"\[UnmanagedCallersOnly").Count;

    private static string Read(string name) => File.ReadAllText(Path.Combine(TestPaths.RepoRoot, name));

    [Fact]
    public void INTEGRATION_banner_states_the_current_abi_and_export_count()
    {
        Assert.Contains($"ABI version at last update: {Version}  ({Exports} exports", Read("INTEGRATION.txt"));
    }

    [Fact]
    public void ROADMAP_status_board_states_the_current_abi_and_export_count()
    {
        Assert.Contains($"ABI {Version}, {Exports} exports", Read("ROADMAP.txt"));
    }

    [Fact]
    public void PLAN_contract_section_states_the_current_abi_and_export_count()
    {
        // §7 opens "abi/dm_core.h is the source of truth. ABI 0.24, 38 exports: ..."
        Assert.Contains($"source of truth. ABI {Version}, {Exports} exports", Read("PLAN.md"));
    }

    [Fact]
    public void PLAN_status_banner_states_the_current_abi()
    {
        // The banner carries no export count, so the version alone is the check. Bold-delimited so
        // the changelog's own "ABI 0.14**" cannot satisfy it by accident — that entry is history.
        string plan = Read("PLAN.md");
        int banner = plan.IndexOf("> Status:", System.StringComparison.Ordinal);

        Assert.True(banner >= 0, "PLAN.md has no '> Status:' banner to check.");

        string line = plan[banner..plan.IndexOf('\n', banner)];
        Assert.Contains($"ABI {Version}", line);
    }
}
