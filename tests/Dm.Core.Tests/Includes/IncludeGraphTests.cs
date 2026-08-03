using Dm.Core.Diagnostics;
using Dm.Core.Includes;

namespace Dm.Core.Tests.Includes;

public class IncludeGraphTests
{
    private static string[] RelativeFiles(IncludeGraph graph, string root)
    {
        List<string> names = new();
        foreach (IncludedFile file in graph.Files)
            names.Add(Path.GetRelativePath(root, file.Path).Replace('\\', '/'));

        return names.ToArray();
    }

    [Fact]
    public void Lists_files_in_compile_order()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"b.dm\"\n");
        temp.Write("a.dm", "/mob/a\n");
        temp.Write("b.dm", "/mob/b\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "a.dm", "b.dm" }, RelativeFiles(graph, temp.Path));
        Assert.Empty(graph.Diagnostics);
    }

    /// <summary>
    /// Depth-first in directive order, matching the compiler. Order is not cosmetic: DM resolves
    /// overrides by it.
    /// </summary>
    [Fact]
    public void Nested_includes_are_walked_depth_first()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"c.dm\"\n");
        temp.Write("a.dm", "#include \"b.dm\"\n");
        temp.Write("b.dm", "/mob/b\n");
        temp.Write("c.dm", "/mob/c\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "a.dm", "b.dm", "c.dm" }, RelativeFiles(graph, temp.Path));
    }

    /// <summary>
    /// Verified against dm.exe: <c>sub/a.dm</c> including <c>"b.dm"</c> finds <c>sub/b.dm</c>, and
    /// fails when only <c>./b.dm</c> exists.
    /// </summary>
    [Fact]
    public void A_nested_include_resolves_relative_to_the_including_file()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"sub/a.dm\"\n");
        temp.Write("sub/a.dm", "#include \"b.dm\"\n");
        temp.Write("sub/b.dm", "/mob/in_sub\n");
        temp.Write("b.dm", "/mob/at_root\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains("sub/b.dm", RelativeFiles(graph, temp.Path));
        Assert.DoesNotContain("b.dm", RelativeFiles(graph, temp.Path));
    }

    [Fact]
    public void Backslash_separators_resolve()
    {
        // Real .dme files are written this way, so a project only loads on Linux if these are
        // normalised.
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"src\\a.dm\"\n");
        temp.Write("src/a.dm", "/mob/a\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains("src/a.dm", RelativeFiles(graph, temp.Path));
        Assert.Empty(graph.Diagnostics);
    }

    /// <summary>
    /// The compiler ignores a repeated include. Real .dme files hit this when DreamMaker's
    /// generated block re-adds a manual entry, so it must not be an error.
    /// </summary>
    [Fact]
    public void A_duplicate_include_is_reported_as_information_and_listed_once()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"a.dm\"\n");
        temp.Write("a.dm", "/mob/a\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "a.dm" }, RelativeFiles(graph, temp.Path));
        Assert.Contains(graph.Diagnostics, d => d.Id == "DM0102" && d.Severity == DiagnosticSeverity.Information);
        Assert.DoesNotContain(graph.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void The_same_file_reached_by_two_spellings_is_included_once()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"./sub/../a.dm\"\n");
        temp.Write("a.dm", "/mob/a\n");
        temp.Write("sub/keep.dm", "/mob/keep\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(2, graph.Files.Count);
    }

    [Fact]
    public void A_missing_include_is_an_error_that_names_what_was_tried()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"absent.dm\"\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Diagnostic error = Assert.Single(graph.Diagnostics);
        Assert.Equal("DM0101", error.Id);
        Assert.Contains("absent.dm", error.Message);
    }

    [Fact]
    public void Interface_and_map_files_are_recorded_but_not_walked()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"ui.dmf\"\n#include \"world.dmm\"\n");
        temp.Write("ui.dmf", "window mainwindow\n");
        temp.Write("world.dmm", "\"a\" = (/turf)\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(IncludeKind.Interface, graph.Files[1].Kind);
        Assert.Equal(IncludeKind.Map, graph.Files[2].Kind);
        Assert.Empty(graph.Diagnostics);
    }

    /// <summary>
    /// Directives are found through the token stream, so one inside a comment is not a directive.
    /// Real code contains exactly this: a library documents its own usage in a block comment.
    /// </summary>
    [Fact]
    public void An_include_inside_a_comment_is_not_followed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n");
        temp.Write("a.dm", "/*\nTo use this, write:\n#include \"b.dm\"\n*/\n/mob/a\n");
        temp.Write("b.dm", "/mob/b\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.DoesNotContain("b.dm", RelativeFiles(graph, temp.Path));
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void A_commented_out_include_is_not_followed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "//#include \"a.dm\"\n#include \"b.dm\"\n");
        temp.Write("a.dm", "/mob/a\n");
        temp.Write("b.dm", "/mob/b\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "b.dm" }, RelativeFiles(graph, temp.Path));
    }

    [Fact]
    public void A_cycle_terminates()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n");
        temp.Write("a.dm", "#include \"b.dm\"\n");
        temp.Write("b.dm", "#include \"a.dm\"\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(3, graph.Files.Count);
    }

    // -- library includes --------------------------------------------------

    [Fact]
    public void A_library_include_resolves_against_the_library_root()
    {
        using TempDirectory temp = new();
        using TempDirectory lib = new();

        temp.Write("game.dme", "#include <vendor/thing>\n");
        lib.Write("vendor/thing/thing.dm", "/mob/from_library\n");

        IncludeGraph graph = IncludeGraph.Build(
            Path.Combine(temp.Path, "game.dme"),
            new IncludeOptions { LibraryRoot = lib.Path });

        Assert.Equal(2, graph.Files.Count);
        Assert.True(graph.Files[1].FromLibrary);
        Assert.Empty(graph.Diagnostics);
    }

    [Fact]
    public void Files_reached_through_a_library_are_marked_as_library_too()
    {
        using TempDirectory temp = new();
        using TempDirectory lib = new();

        temp.Write("game.dme", "#include <vendor/thing>\n");
        lib.Write("vendor/thing/thing.dm", "#include \"more.dm\"\n");
        lib.Write("vendor/thing/more.dm", "/mob/deeper\n");

        IncludeGraph graph = IncludeGraph.Build(
            Path.Combine(temp.Path, "game.dme"),
            new IncludeOptions { LibraryRoot = lib.Path });

        Assert.Equal(3, graph.Files.Count);
        Assert.True(graph.Files[2].FromLibrary);
    }

    [Fact]
    public void A_missing_library_is_an_error()
    {
        using TempDirectory temp = new();
        using TempDirectory lib = new();

        temp.Write("game.dme", "#include <vendor/absent>\n");

        IncludeGraph graph = IncludeGraph.Build(
            Path.Combine(temp.Path, "game.dme"),
            new IncludeOptions { LibraryRoot = lib.Path });

        Assert.Contains(graph.Diagnostics, d => d.Id == "DM0101");
    }
}
