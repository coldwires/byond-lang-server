namespace Dm.Core.Tests;

/// <summary>
/// Locates repo-relative paths from a test binary that lives several directories deep in bin/.
/// </summary>
public static class TestPaths
{
    private static readonly Lazy<string> RepoRootLazy = new(FindRepoRoot);

    public static string RepoRoot => RepoRootLazy.Value;

    public static string Corpus => Path.Combine(RepoRoot, "tests", "corpus");

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "byond-lang-server.sln")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"repo root not found above {AppContext.BaseDirectory}; expected byond-lang-server.sln");
    }
}

/// <summary>
/// A throwaway directory that deletes itself at the end of a test.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dm-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes a file under this directory and returns its full path.</summary>
    public string Write(string relativePath, string contents)
    {
        string full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }
}
