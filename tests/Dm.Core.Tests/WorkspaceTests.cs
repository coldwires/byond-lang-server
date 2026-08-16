namespace Dm.Core.Tests;

public class WorkspaceTests
{
    [Fact]
    public void Open_records_the_directory_containing_the_dme()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", "#include \"game.dm\"\n");

        using Workspace workspace = Workspace.Open(dme);

        Assert.Equal(dme, workspace.DmePath);
        Assert.True(
            Path.GetFullPath(temp.Path).TrimEnd(Path.DirectorySeparatorChar) ==
            workspace.RootDirectory.TrimEnd(Path.DirectorySeparatorChar),
            $"expected root {temp.Path}, got {workspace.RootDirectory}");
    }

    [Fact]
    public void Open_resolves_a_relative_path_to_an_absolute_one()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", string.Empty);

        string previous = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(temp.Path);

            using Workspace workspace = Workspace.Open("game.dme");

            Assert.True(Path.IsPathRooted(workspace.DmePath));
            Assert.True(Path.IsPathRooted(workspace.RootDirectory));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void Open_throws_when_the_dme_is_missing()
    {
        using TempDirectory temp = new();
        string missing = Path.Combine(temp.Path, "absent.dme");

        Assert.Throws<FileNotFoundException>(() => Workspace.Open(missing));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Open_throws_on_an_empty_path(string path)
    {
        Assert.Throws<ArgumentException>(() => Workspace.Open(path));
    }

    // -- documents ---------------------------------------------------------

    /// <summary>
    /// PLAN.md §4: once a client pushes text for a path, that text is the only source for it. Disk
    /// is stale by definition — the editor is ahead of the filesystem on every unsaved keystroke.
    /// </summary>
    [Fact]
    public void A_pushed_buffer_wins_over_disk()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);
        string file = temp.Write("code.dm", "/mob/from_disk\n");

        using Workspace workspace = Workspace.Open(dme);
        workspace.SetBuffer(file, "/mob/from_buffer\n");

        Assert.Equal("/mob/from_buffer\n", workspace.GetDocument(file).Text.Content);
        Assert.True(workspace.HasBuffer(file));
    }

    [Fact]
    public void Closing_a_buffer_falls_back_to_disk()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);
        string file = temp.Write("code.dm", "/mob/from_disk\n");

        using Workspace workspace = Workspace.Open(dme);
        workspace.SetBuffer(file, "/mob/from_buffer\n");
        workspace.CloseBuffer(file);

        Assert.Equal("/mob/from_disk\n", workspace.GetDocument(file).Text.Content);
        Assert.False(workspace.HasBuffer(file));
    }

    [Fact]
    public void Setting_a_buffer_twice_replaces_it()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);

        using Workspace workspace = Workspace.Open(dme);
        workspace.SetBuffer("code.dm", "first");
        workspace.SetBuffer("code.dm", "second");

        Assert.Equal("second", workspace.GetDocument("code.dm").Text.Content);
    }

    /// <summary>
    /// Relative paths resolve against the project root, not the process working directory — which
    /// is whatever the host IDE happened to start in.
    /// </summary>
    [Fact]
    public void Relative_paths_resolve_against_the_project_root()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);
        temp.Write("sub/code.dm", "/mob/a\n");

        using Workspace workspace = Workspace.Open(dme);

        Assert.Equal("/mob/a\n", workspace.GetDocument("sub/code.dm").Text.Content);
    }

    /// <summary>
    /// One file must not end up under two keys, or an edit through one path would leave the other
    /// stale.
    /// </summary>
    [Fact]
    public void The_same_file_reached_by_different_paths_is_one_document()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);
        temp.Write("sub/code.dm", "/mob/a\n");

        using Workspace workspace = Workspace.Open(dme);
        workspace.SetBuffer("sub/code.dm", "/mob/edited\n");

        Assert.Equal("/mob/edited\n", workspace.GetDocument("sub/../sub/code.dm").Text.Content);
    }

    [Fact]
    public void The_lex_result_is_computed_once_per_document()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);

        using Workspace workspace = Workspace.Open(dme);
        Document document = workspace.SetBuffer("code.dm", "/mob/a\n");

        Assert.Same(document.Lex, document.Lex);
    }

    [Fact]
    public void An_edit_produces_a_fresh_lex()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);

        using Workspace workspace = Workspace.Open(dme);
        Document before = workspace.SetBuffer("code.dm", "/mob/a\n");
        Document after = workspace.SetBuffer("code.dm", "/mob/b\n");

        Assert.NotSame(before, after);
        Assert.NotSame(before.Lex, after.Lex);
    }

    [Fact]
    public void Reading_a_missing_file_with_no_buffer_throws()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);

        using Workspace workspace = Workspace.Open(dme);

        Assert.Throws<FileNotFoundException>(() => workspace.GetDocument("absent.dm"));
        Assert.False(workspace.TryGetDocument("absent.dm", out _));
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", string.Empty);

        Workspace workspace = Workspace.Open(dme);

        workspace.Dispose();
        workspace.Dispose();
    }

    // -- the object tree sees what the preprocessor sees ---------------------

    /// <summary>
    /// A type produced by a macro is the type it expands to, not the macro's name.
    /// </summary>
    /// <remarks>
    /// The workspace read each file's own text until this landed, so nothing that declared through
    /// a macro existed in the tree the ABI serves — every <c>SUBSYSTEM_DEF</c>, <c>GLOBAL_VAR</c>
    /// and <c>VAR_PRIVATE</c> in a real codebase.
    /// </remarks>
    [Fact]
    public void The_object_tree_expands_macros()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", "#define DECLARE(X) /obj/##X\nDECLARE(sword)\n\tvar/damage = 5\n");

        using Workspace workspace = Workspace.Open(dme);
        Dm.Core.Symbols.ObjectTree tree = workspace.GetObjectTree();

        Dm.Core.Symbols.TypeSymbol? sword = tree.Find("/obj/sword");

        Assert.NotNull(sword);
        Assert.NotNull(sword!.FindVar("damage"));

        // The macro's own name must not have become a type.
        Assert.Null(tree.Find("/obj/DECLARE"));
    }

    /// <summary>
    /// The include walk reads pushed buffers, not disk.
    /// </summary>
    /// <remarks>
    /// PLAN.md §4 makes a pushed buffer the only source for its path. The preprocessor loads files
    /// itself, so without a hook the tree would describe the last saved version and every unsaved
    /// keystroke would be analysed against stale text.
    /// </remarks>
    [Fact]
    public void The_object_tree_reads_pushed_buffers_rather_than_disk()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", "#include \"code.dm\"\n");
        string file = temp.Write("code.dm", "/obj/on_disk\n");

        using Workspace workspace = Workspace.Open(dme);
        Assert.NotNull(workspace.GetObjectTree().Find("/obj/on_disk"));

        workspace.SetBuffer(file, "/obj/in_buffer\n\tvar/unsaved = 1\n");

        Dm.Core.Symbols.ObjectTree tree = workspace.GetObjectTree();

        Assert.NotNull(tree.Find("/obj/in_buffer"));
        Assert.Null(tree.Find("/obj/on_disk"));
    }

    /// <summary>Defines reach the walk, so the branch the build compiles is the one we analyse.</summary>
    [Fact]
    public void Defines_passed_to_open_select_the_conditional_branch()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", "#ifdef CBT\n/obj/with_cbt\n#else\n/obj/without_cbt\n#endif\n");

        using Workspace plain = Workspace.Open(dme);
        Assert.NotNull(plain.GetObjectTree().Find("/obj/without_cbt"));

        using Workspace flagged = Workspace.Open(dme, new[] { "CBT" });
        Assert.NotNull(flagged.GetObjectTree().Find("/obj/with_cbt"));
    }

    // -- the macros a file can see ------------------------------------------

    /// <summary>
    /// The macro table is sequential state, and the walk's END state is not what any one file
    /// saw: a name defined in a later file must not be offered in an earlier one, <c>__MAIN__</c>
    /// is defined only inside the <c>.dme</c> itself, and a seed or a <c>-D</c> inject is visible
    /// everywhere. <see cref="Workspace.GetMacroNames"/> stays the end state; this is the
    /// per-file view completion asks for.
    /// </summary>
    [Fact]
    public void A_file_sees_the_macros_defined_at_or_before_it()
    {
        using TempDirectory temp = new();
        string dme = temp.Write("game.dme", "#define IN_DME 1\n#include \"first.dm\"\n#include \"second.dm\"\n");
        string first = temp.Write("first.dm", "#define EARLY 1\n/obj/a\n");
        string second = temp.Write("second.dm", "#define LATE 2\n/obj/b\n");

        using Workspace workspace = Workspace.Open(dme, new[] { "INJECTED" });

        IReadOnlyCollection<string> inFirst = workspace.GetMacroNamesFor(first);
        IReadOnlyCollection<string> inSecond = workspace.GetMacroNamesFor(second);
        IReadOnlyCollection<string> inDme = workspace.GetMacroNamesFor(dme);

        Assert.Contains("EARLY", inFirst);
        Assert.DoesNotContain("LATE", inFirst);
        Assert.Contains("EARLY", inSecond);
        Assert.Contains("LATE", inSecond);

        // The .dme's own defines, the -D inject and the seeds reach every file.
        Assert.Contains("IN_DME", inFirst);
        Assert.Contains("INJECTED", inFirst);
        Assert.Contains("TRUE", inFirst);

        // __MAIN__ is the .dme's alone.
        Assert.DoesNotContain("__MAIN__", inFirst);
        Assert.DoesNotContain("__MAIN__", inSecond);
        Assert.Contains("__MAIN__", inDme);

        // The end state still holds everything, as before.
        Assert.Contains("LATE", workspace.GetMacroNames());
        Assert.Contains("__MAIN__", workspace.GetMacroNames());

        // A file the walk never reached cannot be placed and gets the whole table.
        Assert.Contains("LATE", workspace.GetMacroNamesFor(temp.Write("loose.dm", "/obj/c\n")));
    }
}
