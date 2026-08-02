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
