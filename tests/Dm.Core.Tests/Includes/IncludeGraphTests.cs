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

    // -- conditional compilation -------------------------------------------

    /// <summary>
    /// The whole reason the graph builder has to be a preprocessor pass: an include in a dead
    /// branch is not compiled, so it is not part of the project.
    /// </summary>
    [Fact]
    public void An_include_in_a_false_branch_is_not_followed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#ifdef NEVER\n#include \"dead.dm\"\n#endif\n#include \"live.dm\"\n");
        temp.Write("dead.dm", "/mob/dead\n");
        temp.Write("live.dm", "/mob/live\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "live.dm" }, RelativeFiles(graph, temp.Path));
        Assert.DoesNotContain(graph.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void An_include_in_a_true_branch_is_followed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#define ENABLED 1\n#ifdef ENABLED\n#include \"a.dm\"\n#endif\n");
        temp.Write("a.dm", "/mob/a\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains("a.dm", RelativeFiles(graph, temp.Path));
    }

    [Fact]
    public void Else_branches_are_honoured()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#ifdef NEVER\n#include \"a.dm\"\n#else\n#include \"b.dm\"\n#endif\n");
        temp.Write("a.dm", "/mob/a\n");
        temp.Write("b.dm", "/mob/b\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "b.dm" }, RelativeFiles(graph, temp.Path));
    }

    [Fact]
    public void Only_the_first_true_elif_branch_is_taken()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme",
            "#define N 2\n#if N == 1\n#include \"one.dm\"\n#elif N == 2\n#include \"two.dm\"\n" +
            "#elif N == 2\n#include \"also_two.dm\"\n#else\n#include \"other.dm\"\n#endif\n");
        temp.Write("one.dm", "/mob/a\n");
        temp.Write("two.dm", "/mob/b\n");
        temp.Write("also_two.dm", "/mob/c\n");
        temp.Write("other.dm", "/mob/d\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(new[] { "game.dme", "two.dm" }, RelativeFiles(graph, temp.Path));
    }

    /// <summary>
    /// A condition inside a skipped region must not be evaluated. Such conditions routinely
    /// reference macros that only exist in the branch that was taken.
    /// </summary>
    [Fact]
    public void Conditions_inside_a_skipped_region_are_not_evaluated()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme",
            "#ifdef NEVER\n#if UNDEFINED_NAME_THAT_WOULD_ERROR\n#include \"a.dm\"\n#endif\n#endif\n");
        temp.Write("a.dm", "/mob/a\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Single(graph.Files);
        Assert.DoesNotContain(graph.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Defines_carry_across_files_in_include_order()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"defs.dm\"\n#include \"user.dm\"\n");
        temp.Write("defs.dm", "#define FEATURE 1\n");
        temp.Write("user.dm", "#ifdef FEATURE\n#include \"enabled.dm\"\n#endif\n");
        temp.Write("enabled.dm", "/mob/enabled\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains("enabled.dm", RelativeFiles(graph, temp.Path));
    }

    [Fact]
    public void Undef_takes_effect_for_later_files()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#define FEATURE 1\n#undef FEATURE\n#include \"user.dm\"\n");
        temp.Write("user.dm", "#ifdef FEATURE\n#include \"enabled.dm\"\n#endif\n");
        temp.Write("enabled.dm", "/mob/enabled\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.DoesNotContain("enabled.dm", RelativeFiles(graph, temp.Path));
    }

    [Fact]
    public void An_unterminated_conditional_is_reported()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#ifdef SOMETHING\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains(graph.Diagnostics, d => d.Id == "DM0103");
    }

    [Fact]
    public void An_endif_without_an_if_is_reported()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#endif\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains(graph.Diagnostics, d => d.Id == "DM0104");
    }

    /// <summary>
    /// <c>#pragma multiple</c> opts a file out of the compiler's include-once rule.
    /// </summary>
    [Fact]
    public void Pragma_multiple_allows_reinclusion()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"twice.dm\"\n#include \"twice.dm\"\n");
        temp.Write("twice.dm", "#pragma multiple\n/mob/a\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal(3, graph.Files.Count);
        Assert.DoesNotContain(graph.Diagnostics, d => d.Id == "DM0102");
    }

    [Fact]
    public void Predefined_macros_are_available_to_conditions()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#if DM_VERSION >= 500\n#include \"modern.dm\"\n#endif\n");
        temp.Write("modern.dm", "/mob/modern\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains("modern.dm", RelativeFiles(graph, temp.Path));
    }

    /// <summary>
    /// The reference is explicit: <c>__MAIN__</c> is defined in the .dme being compiled and not in
    /// any file it includes.
    /// </summary>
    [Fact]
    public void Main_is_defined_in_the_dme_but_not_in_included_files()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme",
            "#ifdef __MAIN__\n#include \"seen_by_dme.dm\"\n#endif\n" +
            "#include \"child.dm\"\n" +
            "#ifdef __MAIN__\n#include \"still_seen_after.dm\"\n#endif\n");
        temp.Write("child.dm", "#ifdef __MAIN__\n#include \"wrongly_seen.dm\"\n#endif\n");
        temp.Write("seen_by_dme.dm", "/mob/a\n");
        temp.Write("still_seen_after.dm", "/mob/b\n");
        temp.Write("wrongly_seen.dm", "/mob/c\n");

        IncludeGraph graph = IncludeGraph.Build(Path.Combine(temp.Path, "game.dme"));
        string[] files = RelativeFiles(graph, temp.Path);

        Assert.Contains("seen_by_dme.dm", files);
        Assert.DoesNotContain("wrongly_seen.dm", files);

        // And it must be restored when control returns to the .dme.
        Assert.Contains("still_seen_after.dm", files);
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

    /// <summary>
    /// A library that lives only in the INSTALL's own folder still resolves. dm.exe searches
    /// there — probed on 516.1687 by putting a library beside the binary and compiling a project
    /// that includes it — and we searched only the user folder until 2026-08-15, so a project
    /// leaning on an installed library resolved for the compiler and not for us.
    /// </summary>
    [Fact]
    public void A_library_in_the_system_root_resolves()
    {
        using TempDirectory temp = new();
        using TempDirectory user = new();
        using TempDirectory system = new();

        temp.Write("game.dme", "#include <vendor/thing>\n");
        system.Write("vendor/thing/thing.dm", "/mob/from_the_install\n");

        IncludeGraph graph = IncludeGraph.Build(
            Path.Combine(temp.Path, "game.dme"),
            new IncludeOptions { LibraryRoot = user.Path, SystemLibraryRoot = system.Path });

        Assert.Equal(2, graph.Files.Count);
        Assert.True(graph.Files[1].FromLibrary);
        Assert.Empty(graph.Diagnostics);
    }

    /// <summary>
    /// And when both carry the name, the USER folder wins — which is the opposite of the order
    /// the DM Reference documents. Probed by shadowing a real user library with one of the same
    /// name beside the binary: the shadow's marker stayed undefined while the real library's own
    /// var resolved. Fails by resolving the install's copy, which is a silently different program.
    /// </summary>
    [Fact]
    public void The_user_root_wins_over_the_system_root()
    {
        using TempDirectory temp = new();
        using TempDirectory user = new();
        using TempDirectory system = new();

        temp.Write("game.dme", "#include <vendor/thing>\n");
        user.Write("vendor/thing/thing.dm", "/mob/from_the_user_folder\n");
        system.Write("vendor/thing/thing.dm", "/mob/from_the_install\n");

        IncludeGraph graph = IncludeGraph.Build(
            Path.Combine(temp.Path, "game.dme"),
            new IncludeOptions { LibraryRoot = user.Path, SystemLibraryRoot = system.Path });

        Assert.Equal(2, graph.Files.Count);
        Assert.StartsWith(user.Path, graph.Files[1].Path);
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
