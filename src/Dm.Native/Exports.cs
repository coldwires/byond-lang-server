using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Core;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
using Dm.Core.Services;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>
/// The C ABI. Every export here has a matching declaration in <c>abi/dm_core.h</c>.
/// </summary>
/// <remarks>
/// Three rules hold for every function in this file:
///
/// 1. No exception escapes. Each body catches and converts to a <see cref="DmStatus"/>; the message
///    is retrievable via <c>dm_last_error</c>. An exception crossing into native code terminates the
///    host process, which would take a client IDE down with it.
/// 2. Out-parameters are cleared before any work, so a client that ignores the status code reads
///    null rather than uninitialised stack.
/// 3. Returned strings are caller-owned and freed with <c>dm_free</c>.
///
/// The error handling is written out per function rather than factored into a helper taking a
/// delegate: a lambda cannot capture pointer locals, and it would allocate a closure on every call
/// to what becomes a per-keystroke path.
/// </remarks>
internal static unsafe class Exports
{
    [ThreadStatic]
    private static string? _lastError;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_abi_version")]
    public static int AbiVersion() => DmAbi.Packed;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_workspace_open")]
    public static int WorkspaceOpen(byte* dmePathUtf8, IntPtr* outWorkspace)
    {
        if (outWorkspace is null)
            return Fail(DmStatus.InvalidArgument, "out_workspace is null");

        *outWorkspace = IntPtr.Zero;

        try
        {
            string? path = NativeStrings.Read(dmePathUtf8);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "dme_path is null or empty");

            Workspace workspace = Workspace.Open(path);
            *outWorkspace = HandleTable.Alloc(workspace);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_workspace_close")]
    public static void WorkspaceClose(IntPtr workspace)
    {
        try
        {
            if (HandleTable.Release(workspace) is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_workspace_root")]
    public static int WorkspaceRoot(IntPtr workspace, byte** outRoot)
    {
        if (outRoot is null)
            return Fail(DmStatus.InvalidArgument, "out_root is null");

        *outRoot = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            *outRoot = NativeStrings.Allocate(ws.RootDirectory);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Defines macros for the project, as <c>dm.exe -D</c> does. Added in ABI 0.5.
    /// </summary>
    /// <remarks>
    /// Separate from opening because the object tree is built lazily: setting these straight after
    /// <c>dm_workspace_open</c> still applies to the first query, and a client can change build
    /// flags later without reopening. Passing null or a count of zero clears them.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_set_defines")]
    public static int SetDefines(IntPtr workspace, byte** defines, int count)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (count < 0)
                return Fail(DmStatus.InvalidArgument, "count is negative");

            if (defines is null || count == 0)
            {
                ws.SetDefines(null);
                return Ok();
            }

            List<string> parsed = new(count);

            for (int i = 0; i < count; i++)
            {
                string? define = NativeStrings.Read(defines[i]);

                if (string.IsNullOrWhiteSpace(define))
                    return Fail(DmStatus.InvalidArgument, $"define {i} is null or empty");

                parsed.Add(define);
            }

            ws.SetDefines(parsed);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_set_buffer")]
    public static int SetBuffer(IntPtr workspace, byte* filePath, byte* contentUtf8, int length)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            if (contentUtf8 is null)
                return Fail(DmStatus.InvalidArgument, "content is null");

            // A negative length means the caller passed a null-terminated string. An explicit
            // length is preferred: it avoids a scan, and DM source may legitimately contain a NUL
            // inside a string literal.
            string content = length >= 0
                ? Encoding.UTF8.GetString(contentUtf8, length)
                : NativeStrings.Read(contentUtf8) ?? string.Empty;

            ws.SetBuffer(path, content);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Drops every derived answer, so the next question rebuilds against current disk. Added in
    /// ABI 0.14.
    /// </summary>
    /// <remarks>
    /// The answer to files changing OUTSIDE the editor — a git checkout, a branch switch, another
    /// program saving. Cheap by construction: the per-file caches revalidate against write time
    /// and length during the rebuild, so only files that actually changed are reprocessed. Pushed
    /// buffers stay authoritative.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_invalidate")]
    public static int Invalidate(IntPtr workspace)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            ws.Invalidate();
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_close_buffer")]
    public static int CloseBuffer(IntPtr workspace, byte* filePath)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            ws.CloseBuffer(path);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Classifies an inclusive range of lines for syntax highlighting.
    /// </summary>
    /// <remarks>
    /// Results are packed into one contiguous block of <c>int32</c> triples — offset, length, kind
    /// — so a client copies the whole visible range in a single read rather than making three
    /// accessor calls per span. This runs on every scroll and every keystroke, so the per-span cost
    /// is what matters.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_classify_range")]
    public static int ClassifyRange(
        IntPtr workspace,
        byte* filePath,
        int startLine,
        int endLine,
        int encoding,
        IntPtr* outClassification)
    {
        if (outClassification is null)
            return Fail(DmStatus.InvalidArgument, "out_classification is null");

        *outClassification = IntPtr.Zero;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);
            IReadOnlyList<ClassifiedSpan> spans =
                ClassificationService.ClassifyLines(
                    document.Lex, startLine, endLine, ws.GetSemanticContext());

            *outClassification = HandleTable.Alloc(Pack(document.Text, spans, (PositionEncoding)encoding));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Returns the file's outline, and its syntax diagnostics, as a UTF-8 JSON document.
    /// </summary>
    /// <remarks>
    /// Serialized rather than handle-based: symbols carry names and details, so a packed block would
    /// need a string table on both sides of the boundary. An outline is rebuilt per edit rather than
    /// per scroll, so the per-item cost matters far less than it does for classification.
    ///
    /// The caller owns the returned buffer and releases it with <c>dm_free</c>.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_document_symbols")]
    public static int DocumentSymbols(
        IntPtr workspace,
        byte* filePath,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);
            ParseResult parse = document.Parse;

            IReadOnlyList<DocumentSymbol> symbols =
                DocumentSymbolService.GetSymbols(parse, includeParameters: false, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(
                SymbolJson.Write(symbols, parse.Diagnostics, document.Text, (PositionEncoding)encoding));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Every diagnostic for one file — syntax and semantic — as a UTF-8 JSON document. Added in
    /// ABI 0.13.
    /// </summary>
    /// <remarks>
    /// Diagnostics without the outline, and the only export carrying the binder's semantic set:
    /// `dm_document_symbols` ships syntax diagnostics beside the symbols, but a client drawing
    /// squiggles for a file no panel shows should not pay for the outline, and the semantic
    /// checks belong here rather than being bolted onto a call whose shape is frozen.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_diagnostics")]
    public static int Diagnostics(
        IntPtr workspace,
        byte* filePath,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);
            ParseResult parse = document.Parse;

            List<Diagnostic> all = new(parse.Diagnostics);
            all.AddRange(Binder.Bind(ws.GetObjectTree(), parse.Root, document.Path));

            StringBuilder json = new();
            json.Append("{\"diagnostics\":");
            SymbolJson.WriteDiagnostics(json, all, document.Text, (PositionEncoding)encoding);
            json.Append('}');

            *outJson = NativeStrings.Allocate(json.ToString());
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Returns what can be typed at a position, as a UTF-8 JSON document.
    /// </summary>
    /// <remarks>
    /// Serialized for the same reason as document symbols: entries carry names and details. The
    /// caller owns the buffer and releases it with <c>dm_free</c>.
    ///
    /// Building the answer needs the whole project, so the first call after an edit rebuilds the
    /// object tree. That cost is what M9 addresses.
    /// </remarks>
    /// <summary>
    /// Where the symbol at a position is declared. Added in ABI 0.6.
    /// </summary>
    /// <remarks>
    /// Returns every declaration rather than one. DM reopens types across files and overrides procs
    /// as a matter of course, so a single answer would be an arbitrary pick among several correct
    /// ones.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_definition_at")]
    public static int DefinitionAt(
        IntPtr workspace,
        byte* filePath,
        int line,
        int character,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);

            IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
                ws.GetObjectTree(), document, line, character, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(DefinitionJson.Write(ws, found, (PositionEncoding)encoding));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// The declaration behind the symbol at a position, for a tooltip. Added in ABI 0.7.
    /// </summary>
    /// <remarks>
    /// An empty JSON object rather than an error when nothing resolves: a pointer resting on a
    /// local, a keyword or whitespace is the common case, not a failure.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_hover_at")]
    public static int HoverAt(
        IntPtr workspace,
        byte* filePath,
        int line,
        int character,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);

            HoverResult? hover = HoverService.HoverAt(
                ws.GetObjectTree(), document, line, character, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(
                HoverJson.Write(hover, document.Text, (PositionEncoding)encoding));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Which call encloses a position, whose proc it is, and which parameter the caret sits in.
    /// Added in ABI 0.12.
    /// </summary>
    /// <remarks>
    /// An empty JSON object rather than an error when no call encloses the position: a caret
    /// outside any argument list is the ordinary case, not a failure. The enclosing call comes from
    /// a scan over the tokens, so the answer stays exact mid-keystroke on the <c>f(a,</c> prefixes
    /// the parser only sees through recovery.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_signature_at")]
    public static int SignatureAt(
        IntPtr workspace,
        byte* filePath,
        int line,
        int character,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);

            SignatureHelpResult? help = SignatureHelpService.SignatureAt(
                ws.GetObjectTree(), document, line, character, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(SignatureJson.Write(help));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Searches the whole project for symbols by name. Added in ABI 0.8.
    /// </summary>
    /// <remarks>
    /// Ranked and capped rather than exhaustive: a two-character query on a large project matches
    /// tens of thousands of symbols, and an unranked wall of them is useless to a picker.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_workspace_symbols")]
    public static int WorkspaceSymbols(
        IntPtr workspace,
        byte* query,
        int limit,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? needle = NativeStrings.Read(query);
            if (string.IsNullOrWhiteSpace(needle))
                return Fail(DmStatus.InvalidArgument, "query is null or empty");

            IReadOnlyList<WorkspaceSymbol> hits = WorkspaceSymbolService.Search(
                ws.GetObjectTree(),
                needle,
                limit > 0 ? limit : WorkspaceSymbolService.DefaultLimit);

            *outJson = NativeStrings.Allocate(
                WorkspaceSymbolJson.Write(ws, hits, (PositionEncoding)encoding));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Answers a bulk query about the object tree: a JSON request in, a JSON response out.
    /// </summary>
    /// <remarks>
    /// The panels beside an editor ask about a path rather than a caret, and they ask for a lot at
    /// once, so this is one export carrying a named query rather than an export per question. The
    /// same shapes become <c>dm/objectTree</c> and friends at M10, which is what keeps the two shells
    /// answering identically.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_query_json")]
    public static int QueryJsonExport(IntPtr workspace, byte* request, byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            string? text = NativeStrings.Read(request);
            if (string.IsNullOrWhiteSpace(text))
                return Fail(DmStatus.InvalidArgument, "request is null or empty");

            string? response = QueryJson.Answer(ws, text, out QueryError error);

            if (response is null)
            {
                return error == QueryError.NoSuchPath
                    ? Fail(DmStatus.NotFound, "no such type path in this workspace")
                    : Fail(DmStatus.InvalidArgument, "request is malformed or names an unknown query");
            }

            *outJson = NativeStrings.Allocate(response);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_complete_at")]
    public static int CompleteAt(
        IntPtr workspace,
        byte* filePath,
        int line,
        int character,
        int encoding,
        byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (encoding is not ((int)PositionEncoding.Utf8 or (int)PositionEncoding.Utf16))
                return Fail(DmStatus.InvalidArgument, $"unknown position encoding {encoding}");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);

            CompletionResult result = CompletionService.CompleteAt(
                ws.GetObjectTree(),
                document,
                line,
                character,
                ws.GetMacroNames(),
                ws.GetFileText,
                (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(CompletionJson.Write(result));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_classification_count")]
    public static int ClassificationCount(IntPtr classification)
        => HandleTable.TryGet(classification, out ClassificationBuffer buffer) ? buffer.Count : -1;

    /// <summary>
    /// Pointer to <c>3 * count</c> consecutive <c>int32</c> values. Valid until
    /// <c>dm_classification_free</c>.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_classification_data")]
    public static int* ClassificationData(IntPtr classification)
        => HandleTable.TryGet(classification, out ClassificationBuffer buffer) ? buffer.Data : null;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_classification_free")]
    public static void ClassificationFree(IntPtr classification)
    {
        try
        {
            if (HandleTable.Release(classification) is IDisposable disposable)
                disposable.Dispose();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    private static ClassificationBuffer Pack(
        SourceText text,
        IReadOnlyList<ClassifiedSpan> spans,
        PositionEncoding encoding)
    {
        ClassificationBuffer buffer = new(spans.Count);

        int* cursor = buffer.Data;
        foreach (ClassifiedSpan span in spans)
        {
            int start = encoding == PositionEncoding.Utf16
                ? span.Span.Start
                : text.GetUtf8Offset(span.Span.Start);

            int end = encoding == PositionEncoding.Utf16
                ? span.Span.End
                : text.GetUtf8Offset(span.Span.End);

            *cursor++ = start;
            *cursor++ = end - start;
            *cursor++ = (int)span.Kind;
        }

        return buffer;
    }

    /// <summary>
    /// Message for the last failure on the calling thread, or null. Caller frees with
    /// <c>dm_free</c>.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_last_error")]
    public static byte* LastError()
    {
        try
        {
            return NativeStrings.Allocate(_lastError);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_free")]
    public static void Free(void* ptr) => NativeStrings.Free(ptr);

    private static int Ok()
    {
        _lastError = null;
        return (int)DmStatus.Ok;
    }

    private static int Fail(DmStatus status, string message)
    {
        _lastError = message;
        return (int)status;
    }

    private static DmStatus Classify(Exception ex) => ex switch
    {
        ArgumentException => DmStatus.InvalidArgument,
        FileNotFoundException => DmStatus.NotFound,
        DirectoryNotFoundException => DmStatus.NotFound,
        OutOfMemoryException => DmStatus.OutOfMemory,
        _ => DmStatus.Internal,
    };
}
