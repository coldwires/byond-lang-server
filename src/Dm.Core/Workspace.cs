using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Text;
using System.Threading;
using Dm.Core.Includes;
using Dm.Core.Symbols;

namespace Dm.Core;

/// <summary>
/// A loaded DM project, rooted at a <c>.dme</c> file, plus the documents open against it.
/// </summary>
/// <remarks>
/// Two things live here: the document store, which clients push editor buffers into and everything
/// downstream reads from, and the project's object tree, built on demand from the include graph and
/// cached until a buffer changes.
///
/// Thread contract for v1: one workspace is used from one thread at a time. Not enforced.
/// </remarks>
public sealed class Workspace : IDisposable
{
    private readonly Dictionary<string, Document> _documents;
    private ObjectTree? _tree;
    private bool _disposed;

    private Workspace(string dmePath, string rootDirectory)
    {
        DmePath = dmePath;
        RootDirectory = rootDirectory;
        _documents = new Dictionary<string, Document>(PathComparer);
    }

    /// <summary>Absolute path to the <c>.dme</c> this workspace was opened from.</summary>
    public string DmePath { get; }

    /// <summary>Absolute path to the directory containing the <c>.dme</c>.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Windows paths differ only by case; POSIX paths do not. Getting this wrong means one file
    /// held under two keys, so an edit through one path would not invalidate the other.
    /// </summary>
    private static StringComparer PathComparer
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Opens a workspace from a <c>.dme</c> path.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty or has no parent directory.</exception>
    /// <exception cref="FileNotFoundException">The <c>.dme</c> does not exist.</exception>
    public static Workspace Open(string dmePath)
    {
        if (string.IsNullOrWhiteSpace(dmePath))
            throw new ArgumentException("dme path is empty", nameof(dmePath));

        string full = System.IO.Path.GetFullPath(dmePath);

        if (!File.Exists(full))
            throw new FileNotFoundException("dme not found", full);

        string? dir = System.IO.Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir))
            throw new ArgumentException("dme path has no parent directory", nameof(dmePath));

        return new Workspace(full, dir);
    }

    // -- documents ---------------------------------------------------------

    /// <summary>
    /// Records the text a client currently has open for a file. Replaces any previous buffer.
    /// </summary>
    /// <remarks>
    /// Once set, this text is the only source for that path until <see cref="CloseBuffer"/>. Disk
    /// is never consulted for it, which is what makes editor-side line-ending normalisation
    /// harmless: we analyse exactly what the client displays.
    /// </remarks>
    public Document SetBuffer(string path, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string key = NormalisePath(path);
        Document document = new(key, SourceText.From(content, key), fromBuffer: true);
        _documents[key] = document;

        // The tree was built from the previous text, so it no longer describes the project.
        _tree = null;

        return document;
    }

    /// <summary>Drops a client buffer. Later reads for that path fall back to disk.</summary>
    public bool CloseBuffer(string path)
    {
        _tree = null;
        return _documents.Remove(NormalisePath(path));
    }

    // -- object tree -------------------------------------------------------

    /// <summary>
    /// The project's object tree, with the BYOND builtins beneath it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built from the include graph so files arrive in compile order, which is what decides override
    /// resolution — a directory walk would silently produce a different program. Pushed buffers win
    /// over disk for the files that have them, so the tree describes what the client is looking at.
    /// </para>
    /// <para>
    /// Rebuilt whole whenever a buffer changes. That is the wrong shape for a large project on every
    /// keystroke and is what M9 exists to fix; the boundary snapshots from M3 are the intended lever.
    /// A client that wants to control the cost can build its own tree from
    /// <see cref="Symbols.TypeTreeBuilder"/>.
    /// </para>
    /// </remarks>
    public ObjectTree GetObjectTree(CancellationToken cancellationToken = default)
    {
        if (_tree is not null)
            return _tree;

        ObjectTree tree = new();
        Builtins.Seed(tree);

        IncludeGraph graph = IncludeGraph.Build(DmePath);

        foreach (IncludedFile file in graph.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (file.Kind != IncludeKind.DmSource)
                continue;

            if (!TryGetDocument(file.Path, out Document document))
                continue;

            TypeTreeBuilder.AddFile(tree, file.Path, document.Parse, cancellationToken);
        }

        _tree = tree;
        return tree;
    }

    /// <summary>
    /// Returns the document for a path, using a pushed buffer if there is one and reading from disk
    /// otherwise.
    /// </summary>
    /// <exception cref="FileNotFoundException">No buffer is set and the file does not exist.</exception>
    public Document GetDocument(string path)
    {
        string key = NormalisePath(path);

        if (_documents.TryGetValue(key, out Document? existing))
            return existing;

        if (!File.Exists(key))
            throw new FileNotFoundException("file not found", key);

        // Encoding is detected rather than assumed; see SourceFileReader.
        Document loaded = new(key, SourceFileReader.Read(key), fromBuffer: false);
        _documents[key] = loaded;
        return loaded;
    }

    public bool TryGetDocument(string path, out Document document)
    {
        try
        {
            document = GetDocument(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            document = null!;
            return false;
        }
    }

    /// <summary>True if a client has pushed a buffer for this path.</summary>
    public bool HasBuffer(string path)
        => _documents.TryGetValue(NormalisePath(path), out Document? d) && d.IsFromBuffer;

    private string NormalisePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is empty", nameof(path));

        // Relative paths resolve against the project root rather than the process working
        // directory, which is whatever the host IDE happened to start in.
        return System.IO.Path.IsPathRooted(path)
            ? System.IO.Path.GetFullPath(path)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(RootDirectory, path));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _documents.Clear();
    }
}
