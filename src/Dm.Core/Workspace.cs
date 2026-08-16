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
    private readonly SourceCache _sources = new(PathComparer);
    private readonly ExpandedRunCache _runs = new(PathComparer);
    private readonly FileEffectCache _effects = new();
    private ObjectTree? _tree;
    private IReadOnlyList<(string File, ParseResult Parse)>? _parses;
    private MacroTable? _macros;

    // Compile-order position of every file the walk reached, directive-only files included - the
    // include graph's list rather than the parses, which skip a file that emitted no tokens, and a
    // defines-only header is exactly the file a macro comes from. Reset with the tree.
    private Dictionary<string, int>? _compileOrder;
    private Preprocessing.PragmaLevels? _suppressedWarnings;

    // The walk's OWN diagnostics - a #warn echo, an unterminated #if, a missing #include, an
    // unknown pragma name. No parse can hold them: they belong to the include walk rather than to
    // any one file's syntax, which is why they reached `dmc diagdiff` and neither shell until
    // 2026-08-16. Kept whole and filtered per file on the way out; see GetWalkDiagnostics.
    private IReadOnlyList<Diagnostics.Diagnostic>? _walkDiagnostics;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ParseResult, TreeContribution>
        _contributions = new();

    /// <summary>Single-file trees for documents the project does not include. See GetTreeFor.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ParseResult, ObjectTree>
        _standalone = new();

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
    /// Separate from <see cref="Open(string)"/> because the tree is built lazily, so a client can set these
    /// immediately after opening and still have them apply — and can change build flags later
    /// without reopening the project.
    /// </remarks>
    public void SetDefines(IReadOnlyList<string>? defines)
    {
        Defines = defines;
        DropDerived();
    }

    /// <summary>
    /// Caps every completion list, or 0 for no cap. Default 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default on purpose. A bare identifier offers 19,898 items on /tg/station and capping
    /// looks like the obvious fix, but a client that filters the list by the typed prefix — which
    /// <c>INTEGRATION.txt</c> §4 tells them to do — would then silently miss the item being typed
    /// toward. So the cap is the caller's to switch on, and
    /// <see cref="Services.CompletionResult.Truncated"/> tells them when local filtering stopped
    /// being safe.
    /// </para>
    /// <para>
    /// Filtering server-side instead would be sound but costs a call per keystroke, and a keystroke
    /// drops the tree — ~909 ms per character on /tg/station against one rebuild per trigger today.
    /// </para>
    /// </remarks>
    public int CompletionLimit { get; set; }

    /// <summary>
    /// Where <c>#include &lt;vendor/name&gt;</c> resolves from. Null uses BYOND's per-user default.
    /// </summary>
    /// <remarks>
    /// Exposed so a document link can resolve a library include the same way the walk does. The
    /// reference says the system lib dir is searched before the per-user one; we only check the
    /// user dir, which is the known gap recorded in PLAN §3.
    /// </remarks>
    public string? LibraryRoot
        => new IncludeOptions().ResolveLibraryRoot();

    /// <summary>Absolute path to the <c>.dme</c> this workspace was opened from.</summary>
    public string DmePath { get; }

    /// <summary>Absolute path to the directory containing the <c>.dme</c>.</summary>
    public string RootDirectory { get; }

    /// <summary>
    /// Reads a <c>.dmi</c>'s state names, given an ABSOLUTE path. Set by the hosting shell.
    /// </summary>
    /// <remarks>
    /// <c>Dm.Core</c> does not reference <c>Dm.Assets</c>, so the reader has to arrive from
    /// outside — the same seam as <c>IncludeOptions.SourceProvider</c>. Set it before the first
    /// query that builds the tree; unset, <c>icon_state</c> completion still reports its context
    /// and returns an empty list, which is honest rather than silent.
    /// </remarks>
    public Func<string, IReadOnlyList<string>>? IconStateReader { get; set; }

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

    /// <summary>
    /// Opens a workspace with <b>no</b> <c>.dme</c>: every file is its own single-file project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a host that has no project to point at — VS Code's single-file mode, a folder with no
    /// <c>.dme</c> in it, a scratch buffer. Without this the only honest answer was to refuse to
    /// open at all, which left an editor with no analysis rather than analysis of one file.
    /// </para>
    /// <para>
    /// There is no include walk, so there is no object tree beyond the BYOND builtins, and
    /// <see cref="IsFileInProject"/> is false for everything. <see cref="GetTreeFor"/> then does
    /// the real work: builtins plus the file being asked about.
    /// </para>
    /// </remarks>
    public static Workspace OpenStandalone(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("root directory is empty", nameof(rootDirectory));

        return new Workspace(string.Empty, System.IO.Path.GetFullPath(rootDirectory), null);
    }

    /// <summary>True when this workspace has a <c>.dme</c> and therefore a project to walk.</summary>
    public bool HasEnvironmentFile => DmePath.Length > 0;

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
        DropDerived();

        return document;
    }

    /// <summary>Drops a client buffer. Later reads for that path fall back to disk.</summary>
    public bool CloseBuffer(string path)
    {
        DropDerived();
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

        // No .dme means no include walk and so no project: the builtins are the whole tree, and
        // every file resolves through GetTreeFor as its own compilation unit.
        if (!HasEnvironmentFile)
        {
            _parses = System.Array.Empty<(string, ParseResult)>();
            _walkDiagnostics = System.Array.Empty<Diagnostics.Diagnostic>();
            _tree = tree;
            return tree;
        }

        // The preprocessed stream, not each file's own text, so a declaration produced by a macro is
        // the declaration it expands to rather than the macro's name. Reading per file cannot see
        // through `SUBSYSTEM_DEF(air)` or `VAR_PRIVATE/hidden` at all.
        IncludeOptions options = new()
        {
            Defines = Defines,

            // Pushed buffers are authoritative (PLAN.md §4). Without this the walk reads the file as
            // last saved, and every unsaved keystroke would be analysed against stale text.
            SourceProvider = path => _documents.TryGetValue(path, out Document? open)
                ? open.Text
                : _sources.Read(path),

            // Reuses the lex of every file that has not changed since the last rebuild. The one file
            // being typed in has a buffer, and its Document caches its own lex, so the work that is
            // genuinely new is done exactly once either way.
            LexProvider = (path, text) => _documents.TryGetValue(path, out Document? open) && ReferenceEquals(open.Text, text)
                ? open.Lex
                : _sources.Lex(path, text),

            // Replays a file that has not changed instead of walking it, which is where the bulk of
            // a rebuild goes once reading and lexing are cached.
            Effects = _effects,
        };

        PreprocessResult preprocessed = Preprocessor.Run(DmePath, options);
        _macros = preprocessed.Macros;
        _suppressedWarnings = preprocessed.Graph.Warnings;
        _walkDiagnostics = preprocessed.Diagnostics;

        Dictionary<string, int> order = new(PathComparer);

        for (int i = 0; i < preprocessed.Graph.Files.Count; i++)
            order.TryAdd(preprocessed.Graph.Files[i].Path, i);

        _compileOrder = order;

        // Reuses the token source and the parse of every file whose run came out identical, which
        // after an edit is nearly all of them.
        List<(string File, ParseResult Parse)> parses = new();

        foreach ((string file, TokenSource _, ParseResult parse) in
                 PreprocessedSplitter.SplitAndParse(preprocessed, _runs, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            parses.Add((file, parse));

            // A contribution is a pure function of (file, parse), so an unchanged file — the same
            // ParseResult instance out of the run cache — replays its recorded mutations instead
            // of re-walking its AST. The weak table drops entries when a parse is replaced, so an
            // edited file recomputes and old parses cost nothing to hold.
            if (!_contributions.TryGetValue(parse, out TreeContribution? contribution))
            {
                contribution = TypeTreeBuilder.Contribute(file, parse, cancellationToken);
                _contributions.Add(parse, contribution);
            }

            contribution.Apply(tree, cancellationToken);
        }

        _parses = parses;

        // The walk's pragma levels ride on the tree, so every binder caller gets them without a
        // signature change - see ObjectTree.SuppressedWarnings.
        tree.SuppressedWarnings = _suppressedWarnings;

        // Same reasoning for icon states, with the work split where the knowledge is: a resource
        // path is relative to the project, which only the workspace knows, and reading a .dmi needs
        // Dm.Assets, which only a shell has.
        tree.IconStates = IconStateReader is { } read
            ? resource => read(System.IO.Path.Combine(RootDirectory, resource.Replace('\\', '/')))
            : null;

        _tree = tree;
        return tree;
    }

    /// <summary>
    /// Every project file and its parse, in compile order. Builds the tree if it has not been
    /// built, since both come from the same walk.
    /// </summary>
    /// <remarks>
    /// The parses are already retained by the run cache, so keeping this list costs a list of
    /// references rather than a second copy of the trees. The reference index walks it: a
    /// project-wide question needs every file's AST, not the one under the caret.
    /// </remarks>
    public IReadOnlyList<(string File, ParseResult Parse)> GetProjectParses(
        CancellationToken cancellationToken = default)
    {
        if (_parses is null)
            GetObjectTree(cancellationToken);

        return _parses!;
    }

    /// <summary>Whether a tree exists right now — the readiness signal, costing nothing to ask.</summary>
    public bool IsTreeBuilt => _tree is not null;

    /// <summary>
    /// The include walk's own diagnostics for one file — a <c>#warn</c> echo, an unterminated
    /// <c>#if</c>, a missing <c>#include</c>, an unknown pragma name.
    /// </summary>
    /// <remarks>
    /// These belong to the walk rather than to any one file's syntax, so no <see cref="ParseResult"/>
    /// can carry them and a shell assembling <c>parse.Diagnostics + Binder.Bind</c> misses them
    /// entirely. That is what they did until 2026-08-16: <c>dmc diagdiff</c> counted them — which is
    /// why the zero-invented figure counts a set the editors never saw — and neither shell showed
    /// them.
    ///
    /// Attribution is the walk's own, backfilled at each file boundary, so the span indexes
    /// <paramref name="path"/>'s text and converts against that document like any other diagnostic.
    /// A diagnostic the walk could not attribute belongs to the <c>.dme</c>, which is where
    /// <c>diagdiff</c> puts it too — one set, three consumers, so they cannot disagree.
    ///
    /// <b>The lexer's own are excluded, because the parse already carries them.</b> The walk keeps a
    /// copy of every file's <c>lex.Diagnostics</c>, and since 2026-08-16 the parse carries them too,
    /// so a caller adding both lists would report every unterminated string twice. Filtered on the
    /// invariant — one report per site — rather than on a list of lexer ids, which the next id added
    /// to the lexer would silently fall out of.
    ///
    /// Builds the tree if it has not been built, since the walk is where these come from.
    /// </remarks>
    public IReadOnlyList<Diagnostics.Diagnostic> GetWalkDiagnostics(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !HasEnvironmentFile)
            return System.Array.Empty<Diagnostics.Diagnostic>();

        if (_walkDiagnostics is null)
            GetObjectTree(cancellationToken);

        if (_walkDiagnostics is not { Count: > 0 })
            return System.Array.Empty<Diagnostics.Diagnostic>();

        string key = NormalisePath(path);
        List<Diagnostics.Diagnostic>? mine = null;
        HashSet<(string, int, int)>? alreadyReported = null;

        foreach (Diagnostics.Diagnostic diagnostic in _walkDiagnostics)
        {
            // An unattributed one is the .dme's, so it surfaces on the .dme and nowhere else.
            // Hanging it on whichever document happened to ask would put it on the wrong file.
            string file = diagnostic.File ?? DmePath;

            if (!PathComparer.Equals(file, key))
                continue;

            // Built once, and only for a file that actually has walk diagnostics to weigh.
            if (alreadyReported is null)
            {
                alreadyReported = new();

                if (TryGetDocument(key, out Document document))
                {
                    foreach (Diagnostics.Diagnostic parsed in document.Parse.Diagnostics)
                        alreadyReported.Add((parsed.Id, parsed.Span.Start, parsed.Span.Length));
                }
            }

            if (alreadyReported.Contains((diagnostic.Id, diagnostic.Span.Start, diagnostic.Span.Length)))
                continue;

            (mine ??= new()).Add(diagnostic);
        }

        return (IReadOnlyList<Diagnostics.Diagnostic>?)mine
            ?? System.Array.Empty<Diagnostics.Diagnostic>();
    }

    // -- the .dme's tickmarks ----------------------------------------------

    /// <summary>
    /// The <c>.dme</c>'s text, honouring a pushed buffer.
    /// </summary>
    /// <remarks>
    /// Through the document store on purpose: an IDE editing tickmarks usually has the
    /// <c>.dme</c> open, often with unsaved changes, and editing the disk copy underneath it would
    /// lose them.
    /// </remarks>
    private SourceText? EnvironmentText()
        => HasEnvironmentFile && TryGetDocument(DmePath, out Document dme) ? dme.Text : null;

    /// <summary>
    /// The path as the <c>.dme</c> block spells it: relative to the project root, backslashes.
    /// </summary>
    private string RelativeToRoot(string path)
    {
        string full = NormalisePath(path);

        return System.IO.Path.GetRelativePath(RootDirectory, full).Replace('/', '\\');
    }

    /// <summary>
    /// Every file DreamMaker's own <c>// BEGIN_INCLUDE</c> block lists, in file order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The block, not the include graph: these are the entries DreamMaker maintains and the
    /// checkbox toggles, which is a different question from what the project actually compiles.
    /// A file can be included from another <c>.dm</c> and never appear here, and an entry inside
    /// an <c>#if</c> is skipped rather than guessed at.
    /// </para>
    /// <para>
    /// Empty when there is no <c>.dme</c>, no block, or nothing parseable in it — the same
    /// no-answer the tickmark calls give, since a caller wanting to draw a file list has nothing
    /// to draw either way.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> DmeEntries()
    {
        if (EnvironmentText() is not { } dme)
            return System.Array.Empty<string>();

        List<string> paths = new();

        foreach (DmeIncludeBlock.Entry entry in DmeIncludeBlock.Entries(dme))
            paths.Add(entry.Path);

        return paths;
    }

    /// <summary>Whether DreamMaker's include block lists this file.</summary>
    public bool IsFileTicked(string path)
        => EnvironmentText() is { } dme && DmeIncludeBlock.IsTicked(dme, RelativeToRoot(path));

    /// <summary>
    /// The edit that adds this file to the <c>.dme</c>'s block, or null with a reason.
    /// </summary>
    /// <remarks>
    /// Returned rather than applied: the caller owns the buffer. Offsets index the <c>.dme</c>'s
    /// text as this workspace currently sees it, so apply it against the same text — push the
    /// buffer first if you are holding unsaved changes.
    /// </remarks>
    public DmeEdit? TickFile(string path, out DmeEditRefusal refusal)
    {
        if (EnvironmentText() is not { } dme)
        {
            refusal = DmeEditRefusal.NoBlock;
            return null;
        }

        return DmeIncludeBlock.Tick(dme, RelativeToRoot(path), out refusal);
    }

    /// <summary>The edit that removes this file from the block, or null with a reason.</summary>
    public DmeEdit? UntickFile(string path, out DmeEditRefusal refusal)
    {
        if (EnvironmentText() is not { } dme)
        {
            refusal = DmeEditRefusal.NoBlock;
            return null;
        }

        return DmeIncludeBlock.Untick(dme, RelativeToRoot(path), out refusal);
    }

    /// <summary>
    /// The tree to answer questions about one file with: the project's, or a standalone one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file the <c>.dme</c> never includes is not part of the program, so the project tree knows
    /// nothing it declares — and answering from that tree makes an ordinary situation (a scratch
    /// file, a snippet, something written but not yet <c>#include</c>d) look like a broken editor:
    /// symbols from project files resolve while the file's own procs do not.
    /// </para>
    /// <para>
    /// So an outside file gets its own compilation unit — the BYOND builtins plus itself — and
    /// resolves correctly <i>as a single-file project</i>, which is what it is. It deliberately
    /// cannot see the project's declarations: the compiler would not either, and inventing that
    /// reach would be the one thing this codebase does not do.
    /// </para>
    /// <para>
    /// Cached against the <see cref="ParseResult"/> instance rather than the path, so an edit
    /// rebuilds it and an unchanged buffer does not. The weak table drops entries when a parse is
    /// replaced, so closed files cost nothing.
    /// </para>
    /// </remarks>
    public ObjectTree GetTreeFor(string path, CancellationToken cancellationToken = default)
    {
        if (IsFileInProject(path, cancellationToken))
            return GetObjectTree(cancellationToken);

        Document document = GetDocument(path);

        if (_standalone.TryGetValue(document.Parse, out ObjectTree? cached))
            return cached;

        ObjectTree tree = new();
        Builtins.Seed(tree);
        TypeTreeBuilder.AddFile(tree, document.Path, document.Parse);

        _standalone.Add(document.Parse, tree);
        return tree;
    }

    /// <summary>
    /// Whether the <c>.dme</c>'s include walk actually reaches this file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The answer to a confusion that has cost real debugging time: <see cref="SetBuffer"/> accepts
    /// any path and succeeds, but a buffer only joins the object tree if the walk asks for that
    /// path. A file outside the project therefore analyses fine for anything per-file — outline,
    /// classification, syntax diagnostics — while its own declarations resolve nowhere, which
    /// looks exactly like a broken buffer push from the client's side.
    /// </para>
    /// <para>
    /// Cheap by construction, and deliberately not a search: the walk already produced this list,
    /// so the question is free once a tree exists and forces one build if not. Asking it before
    /// choosing a <c>.dme</c> would be the expensive direction.
    /// </para>
    /// </remarks>
    public bool IsFileInProject(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !HasEnvironmentFile)
            return false;

        string key = NormalisePath(path);

        foreach ((string file, ParseResult _) in GetProjectParses(cancellationToken))
        {
            if (PathComparer.Equals(file, key))
                return true;
        }

        // The .dme itself is the root of the walk rather than an entry in it.
        return PathComparer.Equals(DmePath, key);
    }

    /// <summary>
    /// Drops every derived answer, so the next question rebuilds against what is on disk now.
    /// </summary>
    /// <remarks>
    /// The cached tree is invalidated by buffer and define changes but not by the disk moving
    /// underneath it — a git checkout, a branch switch, another editor. The per-file caches
    /// revalidate by write time and length during a rebuild, so this is cheap: only files that
    /// actually changed are re-read, re-walked and re-parsed. Pushed buffers stay authoritative.
    /// </remarks>
    public void Invalidate() => DropDerived();

    /// <summary>
    /// Drops everything derived from the walk, so the next question rebuilds it.
    /// </summary>
    /// <remarks>
    /// One method rather than the four identical blocks this replaced — buffer set, buffer close,
    /// define change, invalidate. They had already drifted: <c>_suppressedWarnings</c> was in none
    /// of them, harmless only because it is reassigned before anything reads it. A fifth field
    /// (<c>_walkDiagnostics</c>) would have been a fifth chance to forget one, which is the shape
    /// this project keeps paying for.
    /// </remarks>
    private void DropDerived()
    {
        _tree = null;
        _parses = null;
        _macros = null;
        _compileOrder = null;
        _suppressedWarnings = null;
        _walkDiagnostics = null;
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
    public Services.SemanticContext GetSemanticContext() => new(_tree, _macros?.Names);

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
    /// <see cref="GetMacroNamesFor"/> narrows that to what a given file could see, and is what a
    /// completion should ask.
    /// </remarks>
    public IReadOnlyCollection<string> GetMacroNames(CancellationToken cancellationToken = default)
        => GetMacroTable(cancellationToken)?.Names ?? System.Array.Empty<string>();

    /// <summary>
    /// The macros a file could see, for its completion list: those defined at or before it in
    /// compile order, the predefined seeds and <c>-D</c> injects, and <c>__MAIN__</c> only in the
    /// <c>.dme</c> itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The macro table is sequential state and the walk's end state is not what any one file saw:
    /// a name defined in a later file was offered in an earlier one, and <c>__MAIN__</c> - which the
    /// walk defines only while inside the <c>.dme</c> - was offered everywhere, since the walk ends
    /// back in the <c>.dme</c> with it defined. This is the M3 boundary snapshot in its cheapest
    /// form: file granularity, from the include graph's compile order, which is the same order the
    /// compiler's own table would have been in.
    /// </para>
    /// <para>
    /// File granularity, so two things are approximate: a macro defined in a file AFTER an
    /// <c>#include</c> it contains still counts as visible in the included file, and a macro
    /// <c>#undef</c>ed later is absent everywhere (it is absent from the end state). A file the
    /// walk never reached - out of the project, or standalone - gets the whole table, as before,
    /// since nothing says where it would sit.
    /// </para>
    /// </remarks>
    public IReadOnlyCollection<string> GetMacroNamesFor(string documentPath, CancellationToken cancellationToken = default)
    {
        MacroTable? table = GetMacroTable(cancellationToken);

        if (table is null)
            return System.Array.Empty<string>();

        string key = NormalisePath(documentPath);
        bool isMain = PathComparer.Equals(key, DmePath);

        if (_compileOrder is null || (!isMain && !_compileOrder.TryGetValue(key, out _)))
            return table.Names;

        int at = isMain ? int.MaxValue : _compileOrder[key];
        List<string> visible = new(table.Names.Count);

        foreach (string name in table.Names)
        {
            if (!table.TryGet(name, out MacroDefinition definition))
                continue;

            string? source = definition.Source.Path;

            // A seed or a -D inject carries a synthetic `<...>` source and is visible everywhere;
            // `__MAIN__` is the one seed that is not, since the walk undefines it on every include.
            if (source is null || source.StartsWith('<'))
            {
                if (isMain || name != "__MAIN__")
                    visible.Add(name);

                continue;
            }

            // Defined in the .dme itself, or in a file at or before this one in compile order. A
            // definition in a file the order does not hold cannot be placed and stays visible
            // rather than vanishing.
            if (PathComparer.Equals(source, DmePath)
                || !_compileOrder.TryGetValue(source, out int defined)
                || defined <= at)
            {
                visible.Add(name);
            }
        }

        return visible;
    }

    /// <summary>
    /// The walk's final macro table, for definition and hover on a macro name.
    /// </summary>
    /// <remarks>
    /// Builds the tree if it has not been built, since both come from the same walk. Like
    /// <see cref="GetMacroNames"/> this is the end state rather than what any one line saw, so a
    /// macro that was <c>#undef</c>ed is absent and a redefined one shows its last definition.
    /// </remarks>
    public MacroTable? GetMacroTable(CancellationToken cancellationToken = default)
    {
        if (_macros is null && HasEnvironmentFile)
            GetObjectTree(cancellationToken);

        return _macros;
    }

    /// <summary>
    /// Renames the symbol at a position: the provable edits, plus the sites rename refuses to
    /// guess about. The rules are <see cref="Services.RenameService"/>'s.
    /// </summary>
    /// <remarks>
    /// Instance-level because every piece lives here: the project parses, the macro table the
    /// resolution needs, and the per-file lex the string scan reads. A file outside the project
    /// renames within itself alone — the same boundary every other answer has in standalone mode.
    /// </remarks>
    public Services.RenameResult RenameAt(
        string file,
        int line,
        int character,
        string newName,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        Document document = GetDocument(file);
        ObjectTree tree = GetTreeFor(file, cancellationToken);

        IReadOnlyList<(string File, ParseResult Parse)> files =
            HasEnvironmentFile && IsFileInProject(file, cancellationToken)
                ? GetProjectParses(cancellationToken)
                : new[] { (NormalisePath(file), document.Parse) };

        return Services.RenameService.RenameAt(
            tree, files, document, line, character, newName, encoding, cancellationToken,
            GetMacroTable(cancellationToken),
            f => TryGetDocument(f, out Document lexed) ? lexed.Lex : null);
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

    /// <summary>The non-throwing <see cref="GetDocument"/>: false instead of an exception when the file cannot be read.</summary>
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

    /// <summary>
    /// Resolves a caller's path against the project root, the same way every text call does.
    /// </summary>
    /// <remarks>
    /// Public for the non-text assets — a <c>.dmi</c> is binary and must not become a
    /// <see cref="Document"/>, but a client naming one relatively expects it resolved against the
    /// project rather than against whatever directory the host process started in.
    /// </remarks>
    public string ResolvePath(string path) => NormalisePath(path);

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

    /// <summary>Releases the workspace's caches, pushed buffers included. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _documents.Clear();
    }
}
