using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Text;
using System.Threading;
using Dm.Core.Includes;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
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
    private IReadOnlyCollection<string>? _macroNames;
    private bool _disposed;

    private Workspace(string dmePath, string rootDirectory, IReadOnlyList<string>? defines)
    {
        DmePath = dmePath;
        RootDirectory = rootDirectory;
        Defines = defines;
        _documents = new Dictionary<string, Document>(PathComparer);
    }

    /// <summary>
    /// Macros defined before the walk, in <c>dm.exe -D</c> spelling.
    /// </summary>
    /// <remarks>
    /// Pass whatever the project's build passes. The flags decide which <c>#ifdef</c> branches
    /// exist, so a workspace opened without them describes a different program from the one the
    /// build produces — /tg/station needs <c>CBT</c>.
    /// </remarks>
    public IReadOnlyList<string>? Defines { get; private set; }

    /// <summary>
    /// Replaces the injected defines and drops the cached tree.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Open"/> because the tree is built lazily, so a client can set these
    /// immediately after opening and still have them apply — and can change build flags later
    /// without reopening the project.
    /// </remarks>
    public void SetDefines(IReadOnlyList<string>? defines)
    {
        Defines = defines;
        _tree = null;
        _macroNames = null;
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
    public static Workspace Open(string dmePath) => Open(dmePath, null);

    /// <summary>Opens a workspace, defining macros before the walk as <c>dm.exe -D</c> would.</summary>
    public static Workspace Open(string dmePath, IReadOnlyList<string>? defines)
    {
        if (string.IsNullOrWhiteSpace(dmePath))
            throw new ArgumentException("dme path is empty", nameof(dmePath));

        string full = System.IO.Path.GetFullPath(dmePath);

        if (!File.Exists(full))
            throw new FileNotFoundException("dme not found", full);

        string? dir = System.IO.Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir))
            throw new ArgumentException("dme path has no parent directory", nameof(dmePath));

        return new Workspace(full, dir, defines);
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
        _macroNames = null;

        return document;
    }

    /// <summary>Drops a client buffer. Later reads for that path fall back to disk.</summary>
    public bool CloseBuffer(string path)
    {
        _tree = null;
        _macroNames = null;
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

        // The preprocessed stream, not each file's own text, so a declaration produced by a macro is
        // the declaration it expands to rather than the macro's name. Reading per file cannot see
        // through `SUBSYSTEM_DEF(air)` or `VAR_PRIVATE/hidden` at all.
        IncludeOptions options = new()
        {
            Defines = Defines,

            // Pushed buffers are authoritative (PLAN.md §4). Without this the walk reads the file as
            // last saved, and every unsaved keystroke would be analysed against stale text.
            SourceProvider = path => _documents.TryGetValue(path, out Document? open) ? open.Text : null,
        };

        PreprocessResult preprocessed = Preprocessor.Run(DmePath, options);
        _macroNames = preprocessed.Macros.Names;

        foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(preprocessed, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TypeTreeBuilder.AddFile(tree, file, DeclarationParser.Parse(source), cancellationToken);
        }

        _tree = tree;
        return tree;
    }

    /// <summary>
    /// The semantic context for classification, using only what is already built.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> build the tree. Classification runs on every scroll and every
    /// keystroke, and a whole-project walk on the paint path would be a serious regression. Type
    /// names therefore stay lexical until something else — a completion, a symbol query — has built
    /// a tree, and light up from then on.
    /// </remarks>
    public Services.SemanticContext GetSemanticContext() => new(_tree, _macroNames);

    /// <summary>
    /// Reads a file through the document store, for services that need text from another file.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="TryGetDocument"/> so a pushed buffer wins and results are cached,
    /// which is what keeps attaching documentation to a few hundred completion items cheap.
    /// </remarks>
    public SourceText? GetFileText(string file)
        => TryGetDocument(file, out Document document) ? document.Text : null;

    /// <summary>
    /// Every macro the project defines, for a completion list.
    /// </summary>
    /// <remarks>
    /// Builds the tree if it has not been built, since both come from the same walk. The names are
    /// the walk's end state rather than what any one line saw — see <see cref="IncludeGraph.Macros"/>.
    /// </remarks>
    public IReadOnlyCollection<string> GetMacroNames(CancellationToken cancellationToken = default)
    {
        if (_macroNames is null)
            GetObjectTree(cancellationToken);

        return _macroNames ?? System.Array.Empty<string>();
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
