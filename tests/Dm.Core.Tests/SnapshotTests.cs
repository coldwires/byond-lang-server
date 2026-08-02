namespace Dm.Core.Tests;

/// <summary>
/// The snapshot helper backs every lexer and parser fixture, so it gets its own tests.
/// </summary>
public class SnapshotTests
{
    [Fact]
    public void Matching_text_passes()
    {
        using TempDirectory temp = new();
        string path = temp.Write("expected.txt", "alpha\nbeta\n");

        Snapshot.Matches("alpha\nbeta\n", path);
    }

    [Fact]
    public void Line_endings_and_trailing_whitespace_are_normalized()
    {
        using TempDirectory temp = new();
        string path = temp.Write("expected.txt", "alpha\r\nbeta\r\n\r\n");

        Snapshot.Matches("alpha\nbeta", path);
    }

    [Fact]
    public void Mismatch_reports_the_first_differing_line()
    {
        using TempDirectory temp = new();
        string path = temp.Write("expected.txt", "alpha\nbeta\ngamma\n");

        SnapshotException ex = Assert.Throws<SnapshotException>(
            () => Snapshot.Matches("alpha\nBETA\ngamma", path));

        Assert.Contains("line 2", ex.Message);
        Assert.Contains("\"beta\"", ex.Message);
        Assert.Contains("\"BETA\"", ex.Message);
    }

    [Fact]
    public void Missing_snapshot_reports_the_actual_output()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "absent.txt");

        SnapshotException ex = Assert.Throws<SnapshotException>(
            () => Snapshot.Matches("produced text", path));

        Assert.Contains("DM_UPDATE_SNAPSHOTS", ex.Message);
        Assert.Contains("produced text", ex.Message);
    }

    [Fact]
    public void Repo_root_is_locatable_from_the_test_binary()
    {
        Assert.True(Directory.Exists(TestPaths.RepoRoot));
        Assert.True(File.Exists(Path.Combine(TestPaths.RepoRoot, "PLAN.md")));
    }
}
