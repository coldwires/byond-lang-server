using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using Dm.Assets;
using Dm.Core;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Lsp;

/// <summary>
/// The LSP shell over <c>Dm.Core</c> — the same services the C ABI exports, spoken over stdio.
/// </summary>
/// <remarks>
/// <para>
/// Requests are handled serially on one thread, which is the workspace's documented contract.
/// Positions are LSP's: zero-based lines and UTF-16 code units, which is exactly what every
/// service takes as <see cref="PositionEncoding.Utf16"/> — the M0 constraint paying off.
/// </para>
/// <para>
/// Document sync is full-text. A keystroke costs one buffer push and, on the next question, an
/// incremental rebuild that M9 priced at ~10 ms on a real game; incremental sync is an
/// optimisation to take when a profile asks for it.
/// </para>
/// </remarks>
internal sealed class LspServer
{
    private readonly Stream _output;

    private Workspace? _workspace;
    private bool _shutdownRequested;

    // $/cancelRequest bookkeeping. The reader thread calls RequestCancel while this thread is
    // busy, so the three fields are guarded; everything else in the server stays single-threaded.
    private readonly object _cancelGate = new();
    private readonly HashSet<string> _cancelledRequests = new();
    private string? _runningRequest;
    private CancellationTokenSource? _runningCancellation;

    public LspServer(Stream output) => _output = output;

    public bool Exited { get; private set; }

    public void Dispatch(JsonDocument message)
    {
        JsonElement root = message.RootElement;
        string method = root.TryGetProperty("method", out JsonElement m) ? m.GetString() ?? "" : "";
        bool isRequest = root.TryGetProperty("id", out JsonElement id);
        JsonElement params_ = root.TryGetProperty("params", out JsonElement p) ? p : default;

        // A message with an id and no method is the client's RESPONSE to a request this server
        // sent (workDoneProgress/create). Fire-and-forget: answering it with "method not
        // supported" — which the default case would do — is protocol noise.
        if (method.Length == 0)
            return;

        try
        {
            switch (method)
            {
                case "initialize":
                    Respond(id, json => WriteInitializeResult(json, params_));
                    break;

                case "initialized":
                case "$/setTrace":
                case "workspace/didChangeConfiguration":
                    break;

                // The reader thread normally intercepts cancels ahead of the queue; this handles
                // one that reaches the dispatcher anyway, such as under an in-process test.
                case "$/cancelRequest":
                    if (params_.ValueKind == JsonValueKind.Object
                        && params_.TryGetProperty("id", out JsonElement cancelId))
                    {
                        RequestCancel(cancelId.GetRawText());
                    }

                    break;

                case "shutdown":
                    _shutdownRequested = true;
                    Respond(id, json => json.WriteNullValue());
                    break;

                case "exit":
                    Exited = true;
                    Environment.ExitCode = _shutdownRequested ? 0 : 1;
                    break;

                case "textDocument/didOpen":
                {
                    string path = PathOf(params_.GetProperty("textDocument"));
                    string text = params_.GetProperty("textDocument").GetProperty("text").GetString() ?? "";
                    _workspace?.SetBuffer(path, text);
                    PublishDiagnostics(path);
                    break;
                }

                case "textDocument/didChange":
                {
                    string path = PathOf(params_.GetProperty("textDocument"));
                    JsonElement changes = params_.GetProperty("contentChanges");

                    // Full sync: the last change carries the whole document.
                    string? text = null;
                    foreach (JsonElement change in changes.EnumerateArray())
                        text = change.GetProperty("text").GetString();

                    if (text is not null)
                        _workspace?.SetBuffer(path, text);

                    PublishDiagnostics(path);
                    break;
                }

                case "textDocument/didClose":
                {
                    string path = PathOf(params_.GetProperty("textDocument"));
                    _workspace?.CloseBuffer(path);

                    // Diagnostics now describe what is on disk, which may differ from the buffer
                    // just closed.
                    PublishDiagnostics(path);
                    break;
                }

                case "textDocument/completion":
                    RespondCancellable(id, (json, cancel) => WriteCompletion(json, params_, cancel));
                    break;

                case "textDocument/hover":
                    RespondCancellable(id, (json, cancel) => WriteHover(json, params_, cancel));
                    break;

                case "textDocument/signatureHelp":
                    RespondCancellable(id, (json, cancel) => WriteSignatureHelp(json, params_, cancel));
                    break;

                case "textDocument/definition":
                    RespondCancellable(id, (json, cancel) => WriteDefinition(json, params_, cancel));
                    break;

                case "textDocument/documentSymbol":
                    RespondCancellable(id, (json, cancel) => WriteDocumentSymbols(json, params_, cancel));
                    break;

                case "textDocument/inlayHint":
                    RespondCancellable(id, (json, cancel) => WriteInlayHints(json, params_, cancel));
                    break;

                case "textDocument/documentColor":
                    RespondCancellable(id, (json, cancel) => WriteDocumentColors(json, params_, cancel));
                    return;

                case "textDocument/colorPresentation":
                    RespondCancellable(id, (json, cancel) => WriteColorPresentations(json, params_, cancel));
                    return;

                case "textDocument/foldingRange":
                    RespondCancellable(id, (json, cancel) => WriteFoldingRanges(json, params_, cancel));
                    break;

                case "textDocument/documentLink":
                    RespondCancellable(id, (json, cancel) => WriteDocumentLinks(json, params_, cancel));
                    break;

                case "completionItem/resolve":
                    RespondCancellable(id, (json, cancel) => WriteCompletionResolve(json, params_, cancel));
                    break;

                case "textDocument/semanticTokens/full":
                    RespondCancellable(id, (json, _) => WriteSemanticTokens(json, params_));
                    break;

                case "workspace/symbol":
                    RespondCancellable(id, (json, cancel) => WriteWorkspaceSymbols(json, params_, cancel));
                    break;

                case "textDocument/references":
                    RespondCancellable(id, (json, cancel) => WriteReferences(json, params_, cancel));
                    break;

                case "textDocument/implementation":
                    RespondCancellable(id, (json, cancel) => WriteImplementations(json, params_, cancel));
                    break;

                case "textDocument/typeDefinition":
                    RespondCancellable(id, (json, cancel) => WriteTypeDefinition(json, params_, cancel));
                    break;

                case "textDocument/documentHighlight":
                    RespondCancellable(id, (json, cancel) => WriteDocumentHighlight(json, params_, cancel));
                    break;

                case "dm/references":
                    RespondCancellable(id, (json, cancel) => WriteReferencesByPath(json, params_, cancel));
                    break;

                case "dm/tickFile":
                    RespondCancellable(id, (json, cancel) => WriteDmeEdit(json, params_, ticking: true));
                    break;

                case "dm/untickFile":
                    RespondCancellable(id, (json, cancel) => WriteDmeEdit(json, params_, ticking: false));
                    break;

                case "dm/fileInProject":
                    RespondCancellable(id, (json, cancel) => WriteFileInProject(json, params_, cancel));
                    break;

                case "dm/ancestorsOf":
                    RespondCancellable(id, (json, cancel) => WriteAncestorsOf(json, params_, cancel));
                    break;

                case "dm/objectTree":
                    RespondCancellable(id, (json, cancel) => WriteObjectTree(json, params_, cancel));
                    break;

                case "dm/subtypesOf":
                    RespondCancellable(id, (json, cancel) => WriteSubtypesOf(json, params_, cancel));
                    break;

                case "dm/members":
                    RespondCancellable(id, (json, cancel) => WriteMembers(json, params_, cancel));
                    break;

                case "dm/iconStates":
                    RespondCancellable(id, (json, _) => WriteIconStates(json, params_));
                    break;

                default:
                    if (isRequest)
                        Rpc.RespondError(_output, id, -32601, $"method not supported: {method}");

                    break;
            }
        }
        catch (Exception ex)
        {
            // The shell's version of the ABI's no-exception rule: a request that fails answers
            // with an error instead of killing the server, and a notification failure is logged
            // to stderr, the one channel that cannot corrupt the protocol stream.
            if (isRequest)
                Rpc.RespondError(_output, id, -32603, ex.Message);
            else
                Console.Error.WriteLine($"dm-lsp: {method}: {ex}");
        }
    }

    private void Respond(JsonElement id, Action<Utf8JsonWriter> result)
        => Rpc.Respond(_output, id, result);

    private int _serverRequestId = 1_000_000;

    /// <summary>
    /// The object tree, announcing a build when none exists yet — the first query after open or
    /// after an edit is the one that pays for the whole project, and a client that cannot see
    /// that shows a frozen UI instead of "indexing".
    /// </summary>
    /// <param name="forFile">
    /// When given, the tree to answer about <i>that file</i> — the project's if the walk reaches
    /// it, otherwise a single-file one of the builtins plus itself. A scratch file, a snippet, or
    /// something written but not yet <c>#include</c>d then resolves correctly as what it is,
    /// instead of showing project symbols while its own procs resolve nowhere.
    /// </param>
    private ObjectTree TreeAnnouncingBuild(
        Workspace ws, CancellationToken cancel = default, string? forFile = null)
    {
        if (ws.IsTreeBuilt)
            return forFile is null ? ws.GetObjectTree(cancel) : ws.GetTreeFor(forFile, cancel);

        int id = _serverRequestId++;
        string token = $"dm/build/{id}";

        // Server-initiated progress needs the client to accept the token first. The response is
        // ignored by the dispatcher; a client that refuses simply never renders the bar.
        Rpc.Request(_output, id, "window/workDoneProgress/create", json =>
        {
            json.WriteStartObject();
            json.WriteString("token", token);
            json.WriteEndObject();
        });

        Rpc.Notify(_output, "$/progress", json =>
        {
            json.WriteStartObject();
            json.WriteString("token", token);
            json.WriteStartObject("value");
            json.WriteString("kind", "begin");
            json.WriteString("title", "DM: building the object tree");
            json.WriteEndObject();
            json.WriteEndObject();
        });

        try
        {
            return forFile is null ? ws.GetObjectTree(cancel) : ws.GetTreeFor(forFile, cancel);
        }
        finally
        {
            Rpc.Notify(_output, "$/progress", json =>
            {
                json.WriteStartObject();
                json.WriteString("token", token);
                json.WriteStartObject("value");
                json.WriteString("kind", "end");
                json.WriteEndObject();
                json.WriteEndObject();
            });
        }
    }

    /// <summary>
    /// Marks a request cancelled. Called by the reader thread, which sees a
    /// <c>$/cancelRequest</c> while this thread is still working through the queue — the only
    /// arrangement under which honouring one is possible at all: delivered in order, a cancel
    /// always arrives after the request it names has been answered.
    /// </summary>
    /// <param name="rawId">The target request id, as raw JSON text — ids may be numbers or strings.</param>
    public void RequestCancel(string rawId)
    {
        lock (_cancelGate)
        {
            if (rawId == _runningRequest)
            {
                _runningCancellation?.Cancel();
                return;
            }

            // Most cancels name a request that was already answered; the set would grow without
            // bound remembering them. A cancel this stale targets nothing that can still run.
            if (_cancelledRequests.Count > 256)
                _cancelledRequests.Clear();

            _cancelledRequests.Add(rawId);
        }
    }

    /// <summary>
    /// Answers a request, honouring cancellation: one cancelled while queued is answered
    /// <c>-32800</c> without running, and one cancelled mid-flight aborts at the service's next
    /// token check and answers the same.
    /// </summary>
    /// <remarks>
    /// Safe to abort mid-writer: <see cref="Rpc.Write"/> buffers the whole body before emitting a
    /// byte, so a throw inside the callback sends nothing and the error frame that follows is the
    /// only output.
    /// </remarks>
    private void RespondCancellable(JsonElement id, Action<Utf8JsonWriter, CancellationToken> result)
    {
        string key = id.GetRawText();
        CancellationToken cancel;

        lock (_cancelGate)
        {
            if (_cancelledRequests.Remove(key))
            {
                Rpc.RespondError(_output, id, -32800, "request cancelled");
                return;
            }

            _runningRequest = key;
            _runningCancellation = new CancellationTokenSource();
            cancel = _runningCancellation.Token;
        }

        try
        {
            Rpc.Respond(_output, id, json => result(json, cancel));
        }
        catch (OperationCanceledException)
        {
            Rpc.RespondError(_output, id, -32800, "request cancelled");
        }
        catch (NoSuchPathException)
        {
            // The LSP spelling of the ABI's DM_ERR_NOT_FOUND, so the two shells agree that a
            // missing path is an error while an empty listing is an answer.
            Rpc.RespondError(_output, id, -32803, "no such type path in this workspace");
        }
        finally
        {
            lock (_cancelGate)
            {
                _runningRequest = null;
                _runningCancellation?.Dispose();
                _runningCancellation = null;
            }
        }
    }

    /// <summary>A dm/* request naming a path the tree does not have.</summary>
    private sealed class NoSuchPathException : Exception
    {
    }

    // -- lifecycle -----------------------------------------------------------

    private void WriteInitializeResult(Utf8JsonWriter json, JsonElement params_)
    {
        string? root = RootDirectoryOf(params_);
        string? dme = FindDme(params_, root);

        if (dme is not null)
        {
            _workspace = Workspace.Open(dme);
            _workspace.IconStateReader = DmiReader.StateNames;

            if (DefinesOf(params_) is { Count: > 0 } defines)
                _workspace.SetDefines(defines);
        }
        else if (root is not null)
        {
            // No project to point at — a folder with no .dme, or single-file mode. Analysis stays
            // ON: every file becomes its own compilation unit of the builtins plus itself, which
            // is far better than the nothing this used to return. Cross-file resolution is what
            // is lost, and dm/fileInProject reports every file as outside a project.
            _workspace = Workspace.OpenStandalone(root);
            _workspace.IconStateReader = DmiReader.StateNames;

            Console.Error.WriteLine(
                $"dm-lsp: no .dme under {root}; analysing each file on its own. "
                + "Point dm.environmentFile at one for cross-file resolution.");
        }
        else
        {
            Console.Error.WriteLine("dm-lsp: no workspace root and no .dme; analysis is off.");
        }

        json.WriteStartObject();
        json.WriteStartObject("capabilities");
        json.WriteString("positionEncoding", "utf-16");
        json.WriteNumber("textDocumentSync", 1); // full
        json.WriteStartObject("completionProvider");
        json.WriteStartArray("triggerCharacters");
        json.WriteStringValue(".");
        json.WriteStringValue(":");
        json.WriteStringValue("/");
        json.WriteEndArray();

        // Documentation is filled in on resolve. A bare identifier on a large project offers tens
        // of thousands of items and the user reads one, so sending every doc comment up front is
        // work for text nobody looks at.
        json.WriteBoolean("resolveProvider", true);
        json.WriteEndObject();
        json.WriteBoolean("hoverProvider", true);
        json.WriteStartObject("signatureHelpProvider");
        json.WriteStartArray("triggerCharacters");
        json.WriteStringValue("(");
        json.WriteStringValue(",");
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteBoolean("definitionProvider", true);
        json.WriteBoolean("referencesProvider", true);

        // What overrides this proc - the reference index's `override` kind, which is the safety
        // question before changing a proc's behaviour in an override-heavy tree.
        json.WriteBoolean("implementationProvider", true);
        json.WriteBoolean("typeDefinitionProvider", true);
        json.WriteBoolean("documentLinkProvider", true);
        json.WriteBoolean("foldingRangeProvider", true);
        json.WriteBoolean("colorProvider", true);
        json.WriteBoolean("documentHighlightProvider", true);
        json.WriteBoolean("documentSymbolProvider", true);
        json.WriteBoolean("workspaceSymbolProvider", true);
        json.WriteBoolean("inlayHintProvider", true);
        json.WriteStartObject("semanticTokensProvider");
        json.WriteStartObject("legend");
        json.WriteStartArray("tokenTypes");

        foreach (string tokenType in SemanticTokenTypes)
            json.WriteStringValue(tokenType);

        json.WriteEndArray();
        json.WriteStartArray("tokenModifiers");
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteBoolean("full", true);
        json.WriteEndObject();
        json.WriteEndObject();
        json.WriteStartObject("serverInfo");
        json.WriteString("name", "dm-lsp");
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static string? RootDirectoryOf(JsonElement params_)
    {
        if (params_.TryGetProperty("rootUri", out JsonElement rootUri)
            && rootUri.ValueKind == JsonValueKind.String)
        {
            return UriToPath(rootUri.GetString()!);
        }

        if (params_.TryGetProperty("rootPath", out JsonElement rootPath)
            && rootPath.ValueKind == JsonValueKind.String)
        {
            return rootPath.GetString();
        }

        return null;
    }

    /// <summary>
    /// The project file: <c>initializationOptions.environmentFile</c> when the client says, else
    /// the first <c>.dme</c> in the workspace root. Real projects keep it at the top — a `.dme`
    /// IS the project — so no recursive search.
    /// </summary>
    private static string? FindDme(JsonElement params_, string? root)
    {
        if (params_.TryGetProperty("initializationOptions", out JsonElement options)
            && options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("environmentFile", out JsonElement configured)
            && configured.ValueKind == JsonValueKind.String)
        {
            string file = configured.GetString()!;

            if (!Path.IsPathRooted(file) && root is not null)
                file = Path.Combine(root, file);

            return File.Exists(file) ? file : null;
        }

        if (root is null || !Directory.Exists(root))
            return null;

        string[] found = Directory.GetFiles(root, "*.dme", SearchOption.TopDirectoryOnly);
        return found.Length > 0 ? found[0] : null;
    }

    private static IReadOnlyList<string>? DefinesOf(JsonElement params_)
    {
        if (!params_.TryGetProperty("initializationOptions", out JsonElement options)
            || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("defines", out JsonElement defines)
            || defines.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> result = new();

        foreach (JsonElement define in defines.EnumerateArray())
        {
            if (define.ValueKind == JsonValueKind.String)
                result.Add(define.GetString()!);
        }

        return result;
    }

    // -- diagnostics -----------------------------------------------------------

    /// <summary>
    /// Pushes the file's syntax diagnostics and the binder's semantic ones in one report — the
    /// same document the ABI promises them through.
    /// </summary>
    private void PublishDiagnostics(string path)
    {
        if (_workspace is not Workspace ws)
            return;

        Document document = ws.GetDocument(path);
        ParseResult parse = document.Parse;

        List<Diagnostic> all = new(parse.Diagnostics);
        all.AddRange(Binder.Bind(TreeAnnouncingBuild(ws), parse.Root, document.Path));

        Rpc.Notify(_output, "textDocument/publishDiagnostics", json =>
        {
            json.WriteStartObject();
            json.WriteString("uri", UriOf(path));
            json.WriteStartArray("diagnostics");

            foreach (Diagnostic diagnostic in all)
            {
                json.WriteStartObject();
                WriteRange(json, document.Text, diagnostic.Span);
                json.WriteNumber("severity", diagnostic.Severity switch
                {
                    DiagnosticSeverity.Error => 1,
                    DiagnosticSeverity.Warning => 2,
                    _ => 3,
                });
                json.WriteString("code", diagnostic.Id);
                json.WriteString("source", "dm");
                json.WriteString("message", diagnostic.Message);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    // -- language features -----------------------------------------------------

    private void WriteCompletion(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (RequirePosition(params_, out string path, out int line, out int character) is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        Document document = ws.GetDocument(path);

        // Brief: documentation is filled in by completionItem/resolve, so the keystroke path does
        // not read a file per item for text the user will not look at.
        CompletionResult result = CompletionService.CompleteBriefAt(
            TreeAnnouncingBuild(ws, cancel),
            document,
            line,
            character,
            ws.GetMacroNames(cancel),
            PositionEncoding.Utf16,
            cancel,
            ws.CompletionLimit);

        // A CompletionList rather than a bare array, so isIncomplete can be stated. It is false
        // unless a limit cut the list: with no cap the list is complete and VS Code filters it
        // locally, which keeps the cost at one rebuild per trigger instead of one per keystroke.
        json.WriteStartObject();
        json.WriteBoolean("isIncomplete", result.Truncated);
        json.WriteStartArray("items");

        int order = 0;

        foreach (CompletionItem item in result.Items)
        {
            json.WriteStartObject();
            json.WriteString("label", item.Name);

            // Our order is the ranking - scope distance, nearest first - and sortText is how a
            // server pins it in VS Code, which otherwise sorts by its own score.
            json.WriteString("sortText", order.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            order++;

            json.WriteNumber("kind", item.Kind switch
            {
                CompletionKind.Type => 7,       // Class
                CompletionKind.Variable => 5,   // Field
                CompletionKind.Proc => 2,       // Method
                CompletionKind.Verb => 3,       // Function
                CompletionKind.Parameter => 6,  // Variable
                CompletionKind.Local => 6,      // Variable
                CompletionKind.Macro => 21,     // Constant
                _ => 14,                        // Keyword
            });

            if (item.Detail.Length > 0)
                json.WriteString("detail", item.Detail);

            // What resolve needs to answer about this item, handed back to us verbatim. Stateless:
            // the position and the name identify the symbol, so nothing is retained server-side.
            json.WriteStartObject("data");
            json.WriteString("uri", UriOf(document.Path));
            json.WriteNumber("line", line);
            json.WriteNumber("character", character);
            json.WriteString("name", item.Name);
            json.WriteEndObject();

            // Not an LSP field. True when the item rides on inference dm.exe does not do, so a
            // client can badge or filter what the build would refuse; spec-only clients ignore it.
            if (item.Inferred)
            {
                json.WriteBoolean("inferred", true);

                // WHY, so a client can say "you wrote `as num`, and dm.exe still will not check
                // members through it" rather than telling an author their own words were a guess.
                json.WriteString("typeFrom", item.TypeSource switch
                {
                    TypeSource.Initializer => "initializer",
                    TypeSource.Assignment => "assignment",
                    TypeSource.InputFilter => "as",
                    TypeSource.BareTypeName => "bareTypeName",
                    _ => "none",
                });
            }

            // Nor are these, and they are named as the C ABI names them so one client can serve
            // both. `detail` is left alone rather than having the type folded into it: a client
            // that wants `fatigue: num` in labelDetails composes it, and one that does not keeps
            // the owner path it already renders. Omitted when empty, which is the common case.
            if (item.DeclaredType.Length > 0)
                json.WriteString("type", item.DeclaredType);

            if (item.InitialValue.Length > 0)
                json.WriteString("value", item.InitialValue);

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void WriteHover(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (RequirePosition(params_, out string path, out int line, out int character) is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        Document document = ws.GetDocument(path);

        HoverResult? hover = HoverService.HoverAt(
            TreeAnnouncingBuild(ws, cancel, path), document, line, character, PositionEncoding.Utf16,
            cancel, ws.GetMacroTable(cancel));

        if (hover is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteStartObject("contents");
        json.WriteString("kind", "markdown");

        string value = $"```dm\n{hover.Signature}\n```";

        if (hover.Detail.Length > 0)
            value = $"`{hover.Detail}`\n\n{value}";

        if (hover.Documentation.Length > 0)
            value += $"\n\n{hover.Documentation}";

        // A builtin has no declaration to open, so the reference section is the closest thing to
        // one. Markdown rather than a nonstandard field: hover contents are already markdown here,
        // and a link in them works in every client without one line of client code.
        if (hover.Reference.Length > 0)
            value += $"\n\n[DM Reference]({hover.Reference})";

        json.WriteString("value", value);
        json.WriteEndObject();
        WriteRange(json, document.Text, hover.Span);
        json.WriteEndObject();
    }

    private void WriteSignatureHelp(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (RequirePosition(params_, out string path, out int line, out int character) is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        Document document = ws.GetDocument(path);

        SignatureHelpResult? help = SignatureHelpService.SignatureAt(
            TreeAnnouncingBuild(ws, cancel, path), document, line, character, PositionEncoding.Utf16);

        if (help is null)
        {
            json.WriteNullValue();
            return;
        }

        json.WriteStartObject();
        json.WriteStartArray("signatures");
        json.WriteStartObject();
        json.WriteString("label", help.Label);
        json.WriteStartArray("parameters");

        foreach (string parameter in help.Parameters)
        {
            json.WriteStartObject();
            json.WriteString("label", parameter);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndArray();
        json.WriteNumber("activeSignature", 0);
        json.WriteNumber("activeParameter", help.ActiveParameter);
        json.WriteEndObject();
    }

    private void WriteDefinition(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (RequirePosition(params_, out string path, out int line, out int character) is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        Document document = ws.GetDocument(path);

        IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
            TreeAnnouncingBuild(ws, cancel, path), document, line, character, PositionEncoding.Utf16,
            cancel, ws.GetMacroTable(cancel));

        json.WriteStartArray();

        foreach (DefinitionLocation location in found)
        {
            if (!ws.TryGetDocument(location.File, out Document target))
                continue;

            json.WriteStartObject();
            json.WriteString("uri", UriOf(location.File));
            WriteRange(json, target.Text, location.NameSpan);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private void WriteDocumentSymbols(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);

        IReadOnlyList<DocumentSymbol> symbols = DocumentSymbolService.GetSymbols(
            document.Parse, includeParameters: false, PositionEncoding.Utf16, cancel);

        json.WriteStartArray();

        foreach (DocumentSymbol symbol in symbols)
            WriteDocumentSymbol(json, symbol);

        json.WriteEndArray();
    }

    /// <summary>
    /// Fills in the documentation for the item the user highlighted.
    /// </summary>
    /// <remarks>
    /// The response must be the WHOLE item, not just the new field: the client replaces the item
    /// it sent with what comes back. Everything but documentation is echoed unchanged.
    /// </remarks>
    private void WriteCompletionResolve(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        json.WriteStartObject();

        // Echo every field the client sent, so nothing is lost by resolving.
        foreach (JsonProperty property in params_.EnumerateObject())
        {
            if (!string.Equals(property.Name, "documentation", StringComparison.Ordinal))
                property.WriteTo(json);
        }

        if (_workspace is Workspace ws
            && params_.TryGetProperty("data", out JsonElement data)
            && data.TryGetProperty("uri", out JsonElement uri)
            && data.TryGetProperty("name", out JsonElement name)
            && data.TryGetProperty("line", out JsonElement line)
            && data.TryGetProperty("character", out JsonElement character))
        {
            string path = UriToPath(uri.GetString() ?? string.Empty);

            if (ws.TryGetDocument(path, out Document document))
            {
                string documentation = CompletionService.ResolveDocumentation(
                    TreeAnnouncingBuild(ws, cancel),
                    document,
                    line.GetInt32(),
                    character.GetInt32(),
                    name.GetString() ?? string.Empty,
                    ws.GetMacroNames(cancel),
                    ws.GetFileText,
                    PositionEncoding.Utf16,
                    cancel);

                if (documentation.Length > 0)
                {
                    json.WriteStartObject("documentation");
                    json.WriteString("kind", "plaintext");
                    json.WriteString("value", documentation);
                    json.WriteEndObject();
                }
            }
        }

        json.WriteEndObject();
    }

    /// <summary>Where the type of the symbol at a position is declared.</summary>
    private void WriteTypeDefinition(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (RequirePosition(params_, out string path, out int line, out int character) is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        Document document = ws.GetDocument(path);

        IReadOnlyList<DefinitionLocation> found = DefinitionService.TypeDefinitionAt(
            TreeAnnouncingBuild(ws, cancel, path), document, line, character, PositionEncoding.Utf16, cancel);

        json.WriteStartArray();

        foreach (DefinitionLocation location in found)
        {
            if (location.File.Length == 0 || !ws.TryGetDocument(location.File, out Document target))
                continue;

            json.WriteStartObject();
            json.WriteString("uri", UriOf(location.File));
            WriteRange(json, target.Text, location.NameSpan);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// Whether the project's include walk reaches a file, so a client can say so.
    /// </summary>
    /// <remarks>
    /// A file the <c>.dme</c> does not include analyses fine per-file while its own declarations
    /// resolve nowhere — indistinguishable from a broken push unless the client can ask.
    /// </remarks>
    private void WriteFileInProject(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        json.WriteStartObject();

        if (_workspace is Workspace ws && params_.TryGetProperty("textDocument", out JsonElement doc))
        {
            string path = PathOf(doc);

            json.WriteString("file", path);
            json.WriteBoolean("inProject", ws.IsFileInProject(path, cancel));
            json.WriteString("environmentFile", ws.DmePath);
        }
        else
        {
            json.WriteBoolean("inProject", false);
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// The edit that ticks or unticks a file in the <c>.dme</c>, plus the range to replace.
    /// </summary>
    /// <remarks>
    /// Positions come back as an LSP range so the client can apply a `WorkspaceEdit` directly —
    /// which is what keeps this safe against a `.dme` the user has open with unsaved changes.
    /// </remarks>
    private void WriteDmeEdit(Utf8JsonWriter json, JsonElement params_, bool ticking)
    {
        json.WriteStartObject();

        if (_workspace is not Workspace ws || !params_.TryGetProperty("textDocument", out JsonElement doc))
        {
            json.WriteString("refusal", "noBlock");
            json.WriteEndObject();
            return;
        }

        string path = PathOf(doc);

        DmeEdit? edit = ticking
            ? ws.TickFile(path, out DmeEditRefusal refusal)
            : ws.UntickFile(path, out refusal);

        json.WriteString("refusal", refusal switch
        {
            DmeEditRefusal.None => "none",
            DmeEditRefusal.NoBlock => "noBlock",
            DmeEditRefusal.Conditional => "conditional",
            _ => "noChange",
        });

        if (edit is not null && ws.TryGetDocument(ws.DmePath, out Document dme))
        {
            json.WriteString("uri", UriOf(ws.DmePath));
            WriteRange(json, dme.Text, edit.Span);
            json.WriteString("text", edit.Replacement);
        }

        json.WriteEndObject();
    }

    /// <summary>Foldable regions — the AST and the token stream, no object tree.</summary>
    private void WriteFoldingRanges(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);

        json.WriteStartArray();

        foreach (FoldingRange range in FoldingService.RangesFor(document, cancel))
        {
            json.WriteStartObject();
            json.WriteNumber("startLine", range.StartLine);
            json.WriteNumber("endLine", range.EndLine);

            if (range.Kind == FoldKind.Comment)
                json.WriteString("kind", "comment");

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// The icon states in a <c>.dmi</c> — the LSP half of M8.
    /// </summary>
    /// <remarks>
    /// Custom because LSP has no notion of a binary asset. Takes a <c>uri</c> like every other
    /// document-shaped method, and answers the same shape <c>dm_icon_states</c> does so one client
    /// can serve both. A file that is not an icon answers <c>isDmi: false</c> rather than an error.
    /// </remarks>
    private void WriteIconStates(Utf8JsonWriter json, JsonElement params_)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = ws.ResolvePath(UriToPath(params_.GetProperty("uri").GetString() ?? ""));

        // The same -32803 a missing type path answers, through the same mechanism: a file that is
        // not there is an error, while a file that is not an icon is an answer.
        if (!File.Exists(path))
            throw new NoSuchPathException();

        bool isDmi = DmiReader.TryRead(path, out DmiIcon icon);

        json.WriteStartObject();
        json.WriteBoolean("isDmi", isDmi);
        json.WriteNumber("width", icon.Width);
        json.WriteNumber("height", icon.Height);
        json.WriteStartArray("states");

        foreach (DmiState state in icon.States)
        {
            json.WriteStartObject();
            json.WriteString("name", state.Name);
            json.WriteNumber("dirs", state.Dirs);
            json.WriteNumber("frames", state.Frames);
            json.WriteBoolean("movement", state.IsMovement);
            json.WriteBoolean("rewind", state.Rewind);
            json.WriteNumber("loop", state.Loop);

            json.WriteStartArray("delays");

            foreach (double delay in state.Delays)
                json.WriteNumberValue(delay);

            json.WriteEndArray();
            json.WriteStartArray("hotspot");

            foreach (int component in state.Hotspot)
                json.WriteNumberValue(component);

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// The colours written in a file, for the swatch an editor draws in the gutter.
    /// </summary>
    /// <remarks>
    /// LSP wants each component as a float from 0 to 1; DM writes 0-255, so the division happens
    /// here at the boundary rather than in the service, which keeps the core speaking DM's units.
    /// </remarks>
    private void WriteDocumentColors(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);

        json.WriteStartArray();

        foreach (ColorInformation color in ColorService.ColorsIn(document, cancel))
        {
            json.WriteStartObject();
            WriteRange(json, document.Text, color.Span);

            json.WriteStartObject("color");
            json.WriteNumber("red", color.Red / 255.0);
            json.WriteNumber("green", color.Green / 255.0);
            json.WriteNumber("blue", color.Blue / 255.0);
            json.WriteNumber("alpha", color.Alpha / 255.0);
            json.WriteEndObject();

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// What to write when the user picks a colour from the swatch.
    /// </summary>
    /// <remarks>
    /// The request carries the colour and the range, not the original text, so the form it was
    /// written in has to be recovered by finding the colour our own scan reports at that range. A
    /// miss means the buffer moved under the request, and offering both spellings is the right
    /// answer there rather than none.
    /// </remarks>
    private void WriteColorPresentations(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);

        JsonElement color = params_.GetProperty("color");

        // Round rather than truncate on the way back IN: the picker hands us a float it computed
        // from a 0-255 value, so 0.5 came from 128 and truncating would return 127 and drift a
        // shade darker on every edit. Truncation is DM's rule for what an author WROTE, which is a
        // different question - see ColorService.
        int red = Component(color, "red");
        int green = Component(color, "green");
        int blue = Component(color, "blue");
        int alpha = Component(color, "alpha");

        JsonElement range = params_.GetProperty("range");
        int start = OffsetOf(document, range.GetProperty("start"));
        int end = OffsetOf(document, range.GetProperty("end"));

        ColorForm form = ColorForm.Literal;

        foreach (ColorInformation existing in ColorService.ColorsIn(document, cancel))
        {
            if (existing.Span.Start == start && existing.Span.End == end)
            {
                form = existing.Form;
                break;
            }
        }

        ColorInformation replacement =
            new(TextSpan.FromBounds(start, end), red, green, blue, alpha, form);

        json.WriteStartArray();

        foreach (string presentation in ColorService.PresentationsFor(replacement))
        {
            json.WriteStartObject();
            json.WriteString("label", presentation);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        static int Component(JsonElement color, string name)
        {
            double value = color.TryGetProperty(name, out JsonElement part) ? part.GetDouble() : 1.0;
            return Math.Clamp((int)Math.Round(value * 255.0), 0, 255);
        }
    }

    private static int OffsetOf(Document document, JsonElement position)
        => document.Text.GetOffset(
            position.GetProperty("line").GetInt32(),
            position.GetProperty("character").GetInt32(),
            PositionEncoding.Utf16);

    /// <summary>Clickable <c>#include</c> targets — needs no object tree, only this file's tokens.</summary>
    private void WriteDocumentLinks(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);

        json.WriteStartArray();

        foreach (DocumentLink link in DocumentLinkService.LinksFor(document, ws.LibraryRoot, cancel))
        {
            json.WriteStartObject();
            WriteRange(json, document.Text, link.Span);
            json.WriteString("target", UriOf(link.Target));
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private void WriteInlayHints(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);

        JsonElement range = params_.GetProperty("range");
        int startLine = range.GetProperty("start").GetProperty("line").GetInt32();
        int endLine = range.GetProperty("end").GetProperty("line").GetInt32();

        IReadOnlyList<InlayHint> hints = InlayHintService.HintsFor(
            TreeAnnouncingBuild(ws, cancel), document, startLine, endLine,
            PositionEncoding.Utf16, cancel);

        json.WriteStartArray();

        foreach (InlayHint hint in hints)
        {
            json.WriteStartObject();
            json.WriteStartObject("position");
            json.WriteNumber("line", hint.Position.Line);
            json.WriteNumber("character", hint.Position.Character);
            json.WriteEndObject();
            json.WriteString("label", hint.Label);
            // LSP numbers these Type=1, Parameter=2; ours start at 0, so the two tables cannot be
            // shared and hardcoding one was fine only while there was a single kind.
            json.WriteNumber("kind", hint.Kind == InlayHintKind.Parameter ? 2 : 1);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteDocumentSymbol(Utf8JsonWriter json, DocumentSymbol symbol)
    {
        json.WriteStartObject();
        json.WriteString("name", symbol.Name.Length > 0 ? symbol.Name : "(unnamed)");

        if (symbol.Detail.Length > 0)
            json.WriteString("detail", symbol.Detail);

        // Not an LSP field; clients that know it (dm-patch's ask) read the enclosing type without
        // string-slicing hover details, and spec-only clients ignore it.
        if (symbol.Owner.Length > 0)
            json.WriteString("owner", symbol.Owner);

        json.WriteNumber("kind", LspSymbolKind(symbol.Kind));

        json.WriteStartObject("range");
        WritePosition(json, "start", symbol.Start);
        WritePosition(json, "end", symbol.End);
        json.WriteEndObject();

        json.WriteStartObject("selectionRange");
        WritePosition(json, "start", symbol.SelectionStart);
        WritePosition(json, "end", symbol.SelectionEnd);
        json.WriteEndObject();

        if (symbol.Children.Count > 0)
        {
            json.WriteStartArray("children");

            foreach (DocumentSymbol child in symbol.Children)
                WriteDocumentSymbol(json, child);

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    /// <summary>
    /// The legend advertised at initialize. Index order is the contract; the encoder writes these
    /// indices, so the two must move together.
    /// </summary>
    private static readonly string[] SemanticTokenTypes =
        { "comment", "keyword", "number", "string", "operator", "macro", "type", "function", "property" };

    /// <summary>
    /// A classification kind's index into <see cref="SemanticTokenTypes"/>, or -1 for the kinds
    /// that stay the default foreground.
    /// </summary>
    /// <remarks>
    /// A bare identifier maps to nothing on purpose — the classifier calls it an identifier
    /// precisely when it does not know more, and colouring it "variable" would claim knowledge M6
    /// deliberately under-claims. Punctuation and lex errors are likewise left to the theme's
    /// default; the squiggle, not a colour, is what reports an error.
    /// </remarks>
    private static int SemanticTokenType(ClassificationKind kind) => kind switch
    {
        ClassificationKind.Comment => 0,
        ClassificationKind.Keyword => 1,
        ClassificationKind.Number => 2,
        ClassificationKind.String => 3,
        ClassificationKind.Resource => 3,
        ClassificationKind.Operator => 4,
        ClassificationKind.InterpolationDelimiter => 4,
        ClassificationKind.PreprocessorDirective => 5,
        ClassificationKind.MacroName => 5,
        ClassificationKind.TypeName => 6,
        ClassificationKind.ProcName => 7,
        ClassificationKind.VarName => 8,
        _ => -1,
    };

    /// <summary>
    /// The whole file's classification, in LSP's relative encoding: five integers per token,
    /// lines and start characters as deltas from the previous token.
    /// </summary>
    /// <remarks>
    /// A span from the classifier may cross lines — a <c>{" multiline "}</c> string or a block
    /// comment is one span — while VS Code highlights only the first line of a multi-line
    /// semantic token. Each span is therefore split at line boundaries here, using the line's
    /// content span so a token never covers the terminator.
    /// </remarks>
    private void WriteSemanticTokens(Utf8JsonWriter json, JsonElement params_)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(path);
        SourceText text = document.Text;

        IReadOnlyList<ClassifiedSpan> spans = ClassificationService.ClassifyLines(
            document.Lex, 0, text.LineCount - 1, ws.GetSemanticContext());

        json.WriteStartObject();
        json.WriteStartArray("data");

        int previousLine = 0;
        int previousCharacter = 0;

        foreach (ClassifiedSpan span in spans)
        {
            int tokenType = SemanticTokenType(span.Kind);
            if (tokenType < 0 || span.Span.Length == 0)
                continue;

            int firstLine = text.GetLineIndex(span.Span.Start);
            int lastLine = text.GetLineIndex(span.Span.End - 1);

            for (int line = firstLine; line <= lastLine; line++)
            {
                TextSpan content = text.GetLineSpan(line);
                int from = Math.Max(span.Span.Start, content.Start);
                int to = Math.Min(span.Span.End, content.End);

                if (to <= from)
                    continue;

                int character = from - content.Start;

                json.WriteNumberValue(line - previousLine);
                json.WriteNumberValue(line == previousLine ? character - previousCharacter : character);
                json.WriteNumberValue(to - from);
                json.WriteNumberValue(tokenType);
                json.WriteNumberValue(0);

                previousLine = line;
                previousCharacter = character;
            }
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void WriteWorkspaceSymbols(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string query = params_.GetProperty("query").GetString() ?? "";

        if (query.Length == 0)
        {
            json.WriteStartArray();
            json.WriteEndArray();
            return;
        }

        IReadOnlyList<WorkspaceSymbol> hits = WorkspaceSymbolService.Search(
            TreeAnnouncingBuild(ws, cancel), query, WorkspaceSymbolService.DefaultLimit);

        json.WriteStartArray();

        foreach (WorkspaceSymbol hit in hits)
        {
            if (!ws.TryGetDocument(hit.File, out Document target))
                continue;

            json.WriteStartObject();
            json.WriteString("name", hit.Name);
            json.WriteNumber("kind", LspSymbolKind(hit.Kind));

            if (hit.Detail.Length > 0)
                json.WriteString("containerName", hit.Detail);

            json.WriteStartObject("location");
            json.WriteString("uri", UriOf(hit.File));
            WriteRange(json, target.Text, hit.NameSpan);
            json.WriteEndObject();

            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    // -- references -------------------------------------------------------------

    /// <summary>The references of the symbol at a position, or null when nothing there is one.</summary>
    private ReferenceListing? ReferencesAtPosition(Workspace ws, JsonElement params_, CancellationToken cancel)
    {
        if (RequirePosition(params_, out string path, out int line, out int character) is null)
            return null;

        return ReferenceService.At(
            TreeAnnouncingBuild(ws, cancel),
            ws.GetProjectParses(cancel),
            ws.GetDocument(path),
            line,
            character,
            PositionEncoding.Utf16,
            ReferenceService.DefaultLimit,
            cancel);
    }

    private void WriteReferences(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        ReferenceListing? found = ReferencesAtPosition(ws, params_, cancel);

        json.WriteStartArray();

        if (found is not null)
        {
            foreach (Reference reference in found.References)
            {
                if (!ws.TryGetDocument(reference.File, out Document target))
                    continue;

                json.WriteStartObject();
                json.WriteString("uri", UriOf(reference.File));
                WriteRange(json, target.Text, reference.Span);
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
    }

    /// <summary>
    /// What overrides the proc at a position — LSP's <c>textDocument/implementation</c>.
    /// </summary>
    /// <remarks>
    /// The reference index already answers this: an <see cref="ReferenceKind.Override"/> hit is a
    /// declaration overriding the target, which is the safety question an author asks before
    /// changing a proc's behaviour. So this is the same query as references with one filter, not a
    /// second index — go-to-definition walks the chain up, this walks it down.
    /// </remarks>
    private void WriteImplementations(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        ReferenceListing? found = ReferencesAtPosition(ws, params_, cancel);

        json.WriteStartArray();

        if (found is not null)
        {
            foreach (Reference reference in found.References)
            {
                if (reference.Kind != ReferenceKind.Override)
                    continue;

                if (!ws.TryGetDocument(reference.File, out Document target))
                    continue;

                json.WriteStartObject();
                json.WriteString("uri", UriOf(reference.File));
                WriteRange(json, target.Text, reference.Span);
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
    }

    private void WriteDocumentHighlight(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string asked = PathOf(params_.GetProperty("textDocument"));
        Document document = ws.GetDocument(asked);

        ReferenceListing? found = ReferencesAtPosition(ws, params_, cancel);

        json.WriteStartArray();

        if (found is not null)
        {
            foreach (Reference reference in found.References)
            {
                // Highlight covers this document alone; reference identity by the cached
                // Document, so path spelling cannot split them.
                if (!ws.TryGetDocument(reference.File, out Document target)
                    || !ReferenceEquals(target, document))
                {
                    continue;
                }

                json.WriteStartObject();
                WriteRange(json, target.Text, reference.Span);
                json.WriteNumber("kind", reference.Kind == ReferenceKind.Write ? 3 : 2); // Write : Read
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
    }

    /// <summary>The path-shaped form, mirroring dm_query_json's "references" field for field.</summary>
    private void WriteReferencesByPath(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string target = StringParam(params_, "path", string.Empty);

        ReferenceListing listing = ReferenceService.Find(
            TreeAnnouncingBuild(ws, cancel),
            ws.GetProjectParses(cancel),
            target,
            IntParam(params_, "limit", ReferenceService.DefaultLimit),
            cancel);

        json.WriteStartObject();
        json.WriteString("query", "references");
        json.WriteString("path", target);
        json.WriteBoolean("truncated", listing.Truncated);
        json.WriteStartArray("references");

        foreach (Reference reference in listing.References)
        {
            if (!ws.TryGetDocument(reference.File, out Document target_))
                continue;

            json.WriteStartObject();
            json.WriteString("file", reference.File);
            json.WriteString("uri", UriOf(reference.File));
            json.WriteString("kind", reference.Kind switch
            {
                ReferenceKind.Write => "write",
                ReferenceKind.Call => "call",
                ReferenceKind.Override => "override",
                _ => "read",
            });
            json.WriteString("inside", reference.Inside);
            WriteRange(json, target_.Text, reference.Span);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void WriteAncestorsOf(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = StringParam(params_, "path", "/");
        ObjectTree tree = TreeAnnouncingBuild(ws, cancel);

        if (tree.Find(path) is not { } type)
            throw new NoSuchPathException();

        json.WriteStartObject();
        json.WriteString("query", "ancestorsOf");
        json.WriteString("path", type.Path.Text);
        json.WriteStartArray("ancestors");

        foreach (var step in tree.InheritanceChain(type))
        {
            if (ReferenceEquals(step, type))
                continue;

            if (TreeQueryService.Browse(tree, step.Path.Text, depth: 0, includeBuiltins: true, cancel) is { } node)
                WriteTreeNode(json, node);
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    // -- dm/* bulk queries ------------------------------------------------------
    //
    // The custom methods LSP cannot express: a tree panel asks about a PATH rather than a caret.
    // Shapes mirror dm_query_json's responses field for field — the answers come from the same
    // TreeQueryService, so the two shells stay describable by one schema (abi/schema/).

    private void WriteObjectTree(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        TreeNode? node = TreeQueryService.Browse(
            TreeAnnouncingBuild(ws, cancel),
            StringParam(params_, "path", "/"),
            Math.Max(0, IntParam(params_, "depth", TreeQueryService.DefaultDepth)),
            BoolParam(params_, "includeBuiltins", true),
            cancel);

        if (node is null)
            throw new NoSuchPathException();

        json.WriteStartObject();
        json.WriteString("query", "objectTree");
        json.WritePropertyName("node");
        WriteTreeNode(json, node);
        json.WriteEndObject();
    }

    private void WriteSubtypesOf(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        string path = StringParam(params_, "path", "/");

        SubtypeListing? listing = TreeQueryService.Subtypes(
            TreeAnnouncingBuild(ws, cancel),
            path,
            IntParam(params_, "limit", TreeQueryService.DefaultSubtypeLimit),
            BoolParam(params_, "includeBuiltins", true),
            cancel);

        if (listing is null)
            throw new NoSuchPathException();

        json.WriteStartObject();
        json.WriteString("query", "subtypesOf");
        json.WriteString("path", path);
        json.WriteBoolean("truncated", listing.Truncated);
        json.WriteStartArray("types");

        foreach (TreeNode node in listing.Types)
            WriteTreeNode(json, node);

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void WriteMembers(Utf8JsonWriter json, JsonElement params_, CancellationToken cancel)
    {
        if (_workspace is not Workspace ws)
        {
            json.WriteNullValue();
            return;
        }

        TypeMembers? members = TreeQueryService.Members(
            TreeAnnouncingBuild(ws, cancel),
            StringParam(params_, "path", "/"),
            BoolParam(params_, "inherited", true),
            BoolParam(params_, "includeBuiltins", true),
            cancel);

        if (members is null)
            throw new NoSuchPathException();

        json.WriteStartObject();
        json.WriteString("query", "members");
        json.WriteString("path", members.Path);
        json.WriteStartArray("vars");

        foreach (MemberEntry member in members.Vars)
            WriteMemberEntry(json, member);

        json.WriteEndArray();
        json.WriteStartArray("procs");

        foreach (MemberEntry member in members.Procs)
            WriteMemberEntry(json, member);

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteTreeNode(Utf8JsonWriter json, TreeNode node)
    {
        json.WriteStartObject();
        json.WriteString("path", node.Path);
        json.WriteString("name", node.Name);
        json.WriteBoolean("declared", node.Declared);
        json.WriteBoolean("builtin", node.Builtin);

        if (node.ParentType is null)
            json.WriteNull("parentType");
        else
            json.WriteString("parentType", node.ParentType);

        json.WriteNumber("childCount", node.ChildCount);
        json.WriteNumber("varCount", node.VarCount);
        json.WriteNumber("procCount", node.ProcCount);
        json.WriteStartArray("children");

        foreach (TreeNode child in node.Children)
            WriteTreeNode(json, child);

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteMemberEntry(Utf8JsonWriter json, MemberEntry member)
    {
        json.WriteStartObject();
        json.WriteString("name", member.Name);
        json.WriteString("detail", member.Detail);
        json.WriteNumber("kind", (int)member.Kind);
        json.WriteBoolean("builtin", member.Builtin);
        json.WriteBoolean("inherited", member.Inherited);
        json.WriteString("owner", member.Owner);
        json.WriteString("file", member.File);
        json.WriteEndObject();
    }

    private static string StringParam(JsonElement params_, string name, string fallback)
        => params_.ValueKind == JsonValueKind.Object
            && params_.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : fallback;

    private static int IntParam(JsonElement params_, string name, int fallback)
        => params_.ValueKind == JsonValueKind.Object
            && params_.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : fallback;

    private static bool BoolParam(JsonElement params_, string name, bool fallback)
        => params_.ValueKind == JsonValueKind.Object
            && params_.TryGetProperty(name, out JsonElement value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.ValueKind == JsonValueKind.True
                : fallback;

    // -- plumbing ---------------------------------------------------------------

    private Workspace? RequirePosition(JsonElement params_, out string path, out int line, out int character)
    {
        path = PathOf(params_.GetProperty("textDocument"));
        JsonElement position = params_.GetProperty("position");
        line = position.GetProperty("line").GetInt32();
        character = position.GetProperty("character").GetInt32();

        return _workspace;
    }

    private static string PathOf(JsonElement textDocument)
        => UriToPath(textDocument.GetProperty("uri").GetString() ?? "");

    /// <summary>
    /// A file URI to a local path, surviving VS Code's spelling.
    /// </summary>
    /// <remarks>
    /// VS Code percent-encodes the drive colon and lowercases the letter —
    /// <c>file:///c%3A/Users/...</c> — and for that form .NET's <c>Uri.LocalPath</c> answers
    /// <c>/c:/Users/...</c> rather than a Windows path. That single slash made
    /// <c>Directory.Exists</c> false, so no <c>.dme</c> was ever found and the workspace silently
    /// never opened: empty outline, no diagnostics, and completion falling back to the editor's
    /// word list. This is the M0 URI-normalisation constraint, paid late.
    /// </remarks>
    internal static string UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || !parsed.IsFile)
            return uri;

        string path = parsed.LocalPath;

        // `/c:/Users/...` -> `c:\Users\...`
        if (path.Length >= 3 && (path[0] == '/' || path[0] == '\\') && path[2] == ':')
            path = path.Substring(1);

        return Path.DirectorySeparatorChar == '\\' ? path.Replace('/', '\\') : path;
    }

    private static string UriOf(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static void WriteRange(Utf8JsonWriter json, SourceText text, TextSpan span)
    {
        LinePosition start = text.GetLinePosition(span.Start, PositionEncoding.Utf16);
        LinePosition end = text.GetLinePosition(span.End, PositionEncoding.Utf16);

        json.WriteStartObject("range");
        WritePosition(json, "start", start);
        WritePosition(json, "end", end);
        json.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter json, string name, LinePosition position)
    {
        json.WriteStartObject(name);
        json.WriteNumber("line", position.Line);
        json.WriteNumber("character", position.Character);
        json.WriteEndObject();
    }

    /// <summary>Our symbol kinds mapped onto LSP's table.</summary>
    private static int LspSymbolKind(SymbolKind kind) => kind switch
    {
        SymbolKind.Type => 5,      // Class
        SymbolKind.Variable => 8,  // Field
        SymbolKind.Proc => 6,      // Method
        SymbolKind.Verb => 12,     // Function
        SymbolKind.Parameter => 13, // Variable
        _ => 13,
    };
}
