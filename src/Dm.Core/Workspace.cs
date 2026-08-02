using System;
using System.IO;

namespace Dm.Core;

/// <summary>
/// A loaded DM project, rooted at a <c>.dme</c> file.
/// </summary>
/// <remarks>
/// M0 scaffold. This validates and records the root only; the include graph, preprocessor, and
/// object tree arrive in M2 and M4. The type exists now so the native handle lifecycle has
/// something real to own.
///
/// Thread contract for v1: one workspace is used from one thread at a time. Not enforced.
/// </remarks>
public sealed class Workspace : IDisposable
{
    private bool _disposed;

    private Workspace(string dmePath, string rootDirectory)
    {
        DmePath = dmePath;
        RootDirectory = rootDirectory;
    }

    /// <summary>Absolute path to the <c>.dme</c> this workspace was opened from.</summary>
    public string DmePath { get; }

    /// <summary>Absolute path to the directory containing the <c>.dme</c>.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Opens a workspace from a <c>.dme</c> path.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty or has no parent directory.</exception>
    /// <exception cref="FileNotFoundException">The <c>.dme</c> does not exist.</exception>
    public static Workspace Open(string dmePath)
    {
        if (string.IsNullOrWhiteSpace(dmePath))
            throw new ArgumentException("dme path is empty", nameof(dmePath));

        string full = Path.GetFullPath(dmePath);

        if (!File.Exists(full))
            throw new FileNotFoundException("dme not found", full);

        string? dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir))
            throw new ArgumentException("dme path has no parent directory", nameof(dmePath));

        return new Workspace(full, dir);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }
}
