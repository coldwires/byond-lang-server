using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Core;
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

    [UnmanagedCallersOnly(EntryPoint = "dm_abi_version")]
    public static int AbiVersion() => DmAbi.Packed;

    [UnmanagedCallersOnly(EntryPoint = "dm_workspace_open")]
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

    [UnmanagedCallersOnly(EntryPoint = "dm_workspace_close")]
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

    [UnmanagedCallersOnly(EntryPoint = "dm_workspace_root")]
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

    [UnmanagedCallersOnly(EntryPoint = "dm_set_buffer")]
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

    [UnmanagedCallersOnly(EntryPoint = "dm_close_buffer")]
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
    [UnmanagedCallersOnly(EntryPoint = "dm_classify_range")]
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
                ClassificationService.ClassifyLines(document.Lex, startLine, endLine);

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
    [UnmanagedCallersOnly(EntryPoint = "dm_document_symbols")]
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
    /// Returns what can be typed at a position, as a UTF-8 JSON document.
    /// </summary>
    /// <remarks>
    /// Serialized for the same reason as document symbols: entries carry names and details. The
    /// caller owns the buffer and releases it with <c>dm_free</c>.
    ///
    /// Building the answer needs the whole project, so the first call after an edit rebuilds the
    /// object tree. That cost is what M9 addresses.
    /// </remarks>
    [UnmanagedCallersOnly(EntryPoint = "dm_complete_at")]
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
                ws.GetObjectTree(), document, line, character, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(CompletionJson.Write(result));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    [UnmanagedCallersOnly(EntryPoint = "dm_classification_count")]
    public static int ClassificationCount(IntPtr classification)
        => HandleTable.TryGet(classification, out ClassificationBuffer buffer) ? buffer.Count : -1;

    /// <summary>
    /// Pointer to <c>3 * count</c> consecutive <c>int32</c> values. Valid until
    /// <c>dm_classification_free</c>.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "dm_classification_data")]
    public static int* ClassificationData(IntPtr classification)
        => HandleTable.TryGet(classification, out ClassificationBuffer buffer) ? buffer.Data : null;

    [UnmanagedCallersOnly(EntryPoint = "dm_classification_free")]
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
    [UnmanagedCallersOnly(EntryPoint = "dm_last_error")]
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

    [UnmanagedCallersOnly(EntryPoint = "dm_free")]
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
