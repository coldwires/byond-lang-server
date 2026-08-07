using System.Collections.Generic;
using System.Linq;
using Dm.Core.Includes;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Includes;

/// <summary>
/// DreamMaker's own tickmark block. The sort rules are checked against orderings taken from real
/// <c>.dme</c> files DreamMaker generated, because two of the three are counter-intuitive and a
/// hand-reasoned rule confirms the wrong one.
/// </summary>
public class DmeIncludeBlockTests
{
    private const string Crlf = "\r\n";

    private static SourceText Dme(params string[] includes)
    {
        string block = string.Join(Crlf, includes.Select(i => $"#include \"{i}\""));

        return SourceText.From(
            "// DM Environment file for test.dme." + Crlf
            + "// BEGIN_INCLUDE" + Crlf
            + (includes.Length > 0 ? block + Crlf : string.Empty)
            + "// END_INCLUDE" + Crlf);
    }

    private static string Apply(SourceText text, DmeEdit edit)
    {
        string s = text.ToString();
        return s[..edit.Span.Start] + edit.Replacement + s[edit.Span.End..];
    }

    // -- the sort order, against real DreamMaker output ---------------------

    /// <summary>
    /// madridspy's root, exactly as DreamMaker wrote it. <c>skiner.dmf</c> sits below both
    /// <c>.dm</c> files even though <c>skiner</c> sorts before <c>test_lighting</c> — extension
    /// decides before filename, which is the rule most easily got wrong.
    /// </summary>
    [Fact]
    public void Extension_sorts_before_filename()
    {
        string[] real = { "main.dm", "test_lighting.dm", "skiner.dmf", "testmap.dmm", "code\\access.dm" };

        List<string> sorted = new(real);
        sorted.Sort(DmeIncludeBlock.Compare);

        Assert.Equal(real, sorted);
    }

    /// <summary>warklan writes <c>Interface.dmf</c> above <c>Code\Admin.dm</c>.</summary>
    [Fact]
    public void Files_sort_before_directories()
    {
        Assert.True(DmeIncludeBlock.Compare("Interface.dmf", "Code\\Admin.dm") < 0);
    }

    /// <summary>
    /// warklan's <c>Code\</c>, in DreamMaker's order. Byte order on the lowercased name: <c>'</c>
    /// is 0x27, <c>-</c> is 0x2D, <c>i</c> is 0x69.
    /// </summary>
    [Fact]
    public void Comparison_is_ordinal_on_the_lowercased_name()
    {
        string[] real = { "Code\\NPC's.dm", "Code\\NPC-Shop.dm", "Code\\NPCItemAdd.dm" };

        List<string> sorted = new(real);
        sorted.Sort(DmeIncludeBlock.Compare);

        Assert.Equal(real, sorted);

        // Admin before AI is only correct lowercased first: 'I' (0x49) would beat 'd' (0x64).
        Assert.True(DmeIncludeBlock.Compare("Code\\Admin.dm", "Code\\AI.dm") < 0);
    }

    /// <summary>tgstation's <c>interface\</c>: <c>skin.dmf</c> last despite skin &lt; stylesheet.</summary>
    [Fact]
    public void A_subdirectorys_files_sort_by_extension_too()
    {
        string[] real =
        {
            "interface\\interface.dm", "interface\\stylesheet.dm",
            "interface\\skin.dmf", "interface\\fonts\\fonts_datum.dm",
        };

        List<string> sorted = new(real);
        sorted.Sort(DmeIncludeBlock.Compare);

        Assert.Equal(real, sorted);
    }

    // -- reading ------------------------------------------------------------

    [Fact]
    public void Entries_lists_the_block_and_IsTicked_matches_by_path()
    {
        SourceText dme = Dme("src\\a.dm", "src\\b.dm");

        Assert.Equal(new[] { "src\\a.dm", "src\\b.dm" }, DmeIncludeBlock.Entries(dme).Select(e => e.Path));

        Assert.True(DmeIncludeBlock.IsTicked(dme, "src\\a.dm"));

        // Separator and case both normalise: same file, two spellings.
        Assert.True(DmeIncludeBlock.IsTicked(dme, "src/a.dm"));
        Assert.True(DmeIncludeBlock.IsTicked(dme, "SRC\\A.DM"));

        Assert.False(DmeIncludeBlock.IsTicked(dme, "src\\c.dm"));
    }

    /// <summary>
    /// A manual include above the block is the author's and is not a tick. mlaas ships exactly
    /// this shape, with the same path listed manually and again inside the block.
    /// </summary>
    [Fact]
    public void An_include_outside_the_block_is_not_a_tick()
    {
        SourceText dme = SourceText.From(
            "#include \"src\\manual.dm\"" + Crlf
            + "// BEGIN_INCLUDE" + Crlf
            + "#include \"src\\generated.dm\"" + Crlf
            + "// END_INCLUDE" + Crlf);

        Assert.True(DmeIncludeBlock.IsTicked(dme, "src\\generated.dm"));
        Assert.False(DmeIncludeBlock.IsTicked(dme, "src\\manual.dm"));
    }

    // -- ticking ------------------------------------------------------------

    [Fact]
    public void Tick_inserts_at_the_sorted_position()
    {
        SourceText dme = Dme("src\\a.dm", "src\\c.dm");

        DmeEdit edit = Assert.IsType<DmeEdit>(DmeIncludeBlock.Tick(dme, "src\\b.dm", out DmeEditRefusal why));
        Assert.Equal(DmeEditRefusal.None, why);

        string[] after = DmeIncludeBlock.Entries(SourceText.From(Apply(dme, edit)))
            .Select(e => e.Path).ToArray();

        Assert.Equal(new[] { "src\\a.dm", "src\\b.dm", "src\\c.dm" }, after);
    }

    [Fact]
    public void Tick_appends_when_nothing_sorts_after_it()
    {
        SourceText dme = Dme("src\\a.dm");

        DmeEdit edit = Assert.IsType<DmeEdit>(DmeIncludeBlock.Tick(dme, "src\\z.dm", out _));

        Assert.Equal(
            new[] { "src\\a.dm", "src\\z.dm" },
            DmeIncludeBlock.Entries(SourceText.From(Apply(dme, edit))).Select(e => e.Path));
    }

    /// <summary>
    /// A tick is a zero-length insert, which is what lets it apply cleanly to a buffer the caller
    /// owns and has unsaved changes in.
    /// </summary>
    [Fact]
    public void Tick_is_a_zero_length_insert()
    {
        DmeEdit edit = Assert.IsType<DmeEdit>(
            DmeIncludeBlock.Tick(Dme("src\\a.dm"), "src\\b.dm", out _));

        Assert.True(edit.Span.IsEmpty);
    }

    /// <summary>
    /// The file's own terminator, read off an existing line. A lone LF in a CRLF file makes
    /// DreamMaker rewrite everything on its next save.
    /// </summary>
    [Fact]
    public void Tick_uses_the_files_own_terminator()
    {
        DmeEdit crlf = Assert.IsType<DmeEdit>(
            DmeIncludeBlock.Tick(Dme("src\\a.dm"), "src\\b.dm", out _));

        Assert.EndsWith("\r\n", crlf.Replacement);

        SourceText lf = SourceText.From(
            "// BEGIN_INCLUDE\n#include \"src\\a.dm\"\n// END_INCLUDE\n");

        DmeEdit edit = Assert.IsType<DmeEdit>(DmeIncludeBlock.Tick(lf, "src\\b.dm", out _));

        Assert.EndsWith("\n", edit.Replacement);
        Assert.DoesNotContain("\r", edit.Replacement);
    }

    [Fact]
    public void Tick_normalises_forward_slashes_to_backslashes()
    {
        DmeEdit edit = Assert.IsType<DmeEdit>(
            DmeIncludeBlock.Tick(Dme("src\\a.dm"), "src/b.dm", out _));

        Assert.Contains("src\\b.dm", edit.Replacement);
        Assert.DoesNotContain("src/b.dm", edit.Replacement);
    }

    [Fact]
    public void Ticking_something_already_there_is_no_change()
    {
        Assert.Null(DmeIncludeBlock.Tick(Dme("src\\a.dm"), "src\\a.dm", out DmeEditRefusal why));
        Assert.Equal(DmeEditRefusal.NoChange, why);
    }

    // -- unticking ----------------------------------------------------------

    [Fact]
    public void Untick_removes_the_whole_line()
    {
        SourceText dme = Dme("src\\a.dm", "src\\b.dm");

        DmeEdit edit = Assert.IsType<DmeEdit>(DmeIncludeBlock.Untick(dme, "src\\a.dm", out _));

        string after = Apply(dme, edit);

        Assert.Equal(new[] { "src\\b.dm" },
            DmeIncludeBlock.Entries(SourceText.From(after)).Select(e => e.Path));

        // No blank line left behind: the terminator went with it.
        Assert.DoesNotContain("\r\n\r\n", after);
    }

    /// <summary>
    /// The block can carry the same path twice — DreamMaker's generated block re-adding one the
    /// author wrote manually. Unticking answers again until they are gone.
    /// </summary>
    [Fact]
    public void Untick_handles_a_duplicated_entry()
    {
        SourceText dme = Dme("src\\a.dm", "src\\a.dm", "src\\b.dm");

        DmeEdit first = Assert.IsType<DmeEdit>(DmeIncludeBlock.Untick(dme, "src\\a.dm", out _));
        SourceText once = SourceText.From(Apply(dme, first));

        DmeEdit second = Assert.IsType<DmeEdit>(DmeIncludeBlock.Untick(once, "src\\a.dm", out _));
        SourceText twice = SourceText.From(Apply(once, second));

        Assert.Equal(new[] { "src\\b.dm" }, DmeIncludeBlock.Entries(twice).Select(e => e.Path));
        Assert.Null(DmeIncludeBlock.Untick(twice, "src\\a.dm", out DmeEditRefusal why));
        Assert.Equal(DmeEditRefusal.NoChange, why);
    }

    // -- refusals -----------------------------------------------------------

    /// <summary>
    /// A line inside <c>#if</c> does not mean the file is in the build, so neither answer is
    /// correct and refusing beats guessing at somebody's project file.
    /// </summary>
    [Fact]
    public void A_conditional_in_the_block_refuses_the_edit()
    {
        SourceText dme = SourceText.From(
            "// BEGIN_INCLUDE" + Crlf
            + "#ifdef DEBUG" + Crlf
            + "#include \"src\\a.dm\"" + Crlf
            + "#endif" + Crlf
            + "// END_INCLUDE" + Crlf);

        Assert.Null(DmeIncludeBlock.Tick(dme, "src\\b.dm", out DmeEditRefusal ticking));
        Assert.Equal(DmeEditRefusal.Conditional, ticking);

        Assert.Null(DmeIncludeBlock.Untick(dme, "src\\a.dm", out DmeEditRefusal unticking));
        Assert.Equal(DmeEditRefusal.Conditional, unticking);
    }

    [Fact]
    public void A_dme_with_no_block_refuses_rather_than_inventing_one()
    {
        SourceText dme = SourceText.From("#include \"src\\a.dm\"" + Crlf);

        Assert.Null(DmeIncludeBlock.Tick(dme, "src\\b.dm", out DmeEditRefusal why));
        Assert.Equal(DmeEditRefusal.NoBlock, why);
    }

    /// <summary>
    /// An unparseable line is skipped, not treated as position zero — otherwise a stray comment
    /// in the block sends every insert to the top.
    /// </summary>
    [Fact]
    public void An_unparseable_line_is_skipped_not_sorted()
    {
        SourceText dme = SourceText.From(
            "// BEGIN_INCLUDE" + Crlf
            + "// a stray comment" + Crlf
            + "#include \"src\\a.dm\"" + Crlf
            + "// END_INCLUDE" + Crlf);

        Assert.Equal(new[] { "src\\a.dm" }, DmeIncludeBlock.Entries(dme).Select(e => e.Path));

        DmeEdit edit = Assert.IsType<DmeEdit>(DmeIncludeBlock.Tick(dme, "src\\b.dm", out _));

        Assert.Equal(
            new[] { "src\\a.dm", "src\\b.dm" },
            DmeIncludeBlock.Entries(SourceText.From(Apply(dme, edit))).Select(e => e.Path));
    }

    /// <summary>A library include is not a project file and DreamMaker does not list one.</summary>
    [Fact]
    public void A_library_include_is_not_an_entry()
    {
        SourceText dme = SourceText.From(
            "// BEGIN_INCLUDE" + Crlf
            + "#include <deadron/characterhandling>" + Crlf
            + "#include \"src\\a.dm\"" + Crlf
            + "// END_INCLUDE" + Crlf);

        Assert.Equal(new[] { "src\\a.dm" }, DmeIncludeBlock.Entries(dme).Select(e => e.Path));
    }

    /// <summary>An empty block is a legal starting point, and a tick lands inside it.</summary>
    [Fact]
    public void Ticking_into_an_empty_block_works()
    {
        SourceText dme = Dme();

        DmeEdit edit = Assert.IsType<DmeEdit>(DmeIncludeBlock.Tick(dme, "src\\a.dm", out _));

        Assert.Equal(
            new[] { "src\\a.dm" },
            DmeIncludeBlock.Entries(SourceText.From(Apply(dme, edit))).Select(e => e.Path));
    }
}
