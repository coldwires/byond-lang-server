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
}
