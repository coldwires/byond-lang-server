using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Assets;
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
            workspace.IconStateReader = DmiReader.StateNames;
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

    /// <summary>
    /// Inferred-type annotations for untyped locals in a line range. Added in ABI 0.16.
    /// </summary>
    /// <remarks>
    /// DM code is full of <c>var/x = new /obj/item</c> and the type is exactly what a reader does
    /// not have — the compiler never checks it, so nothing forces the author to write it. Each
    /// hint carries the same inference completion rides on, rendered after the name.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_inlay_hints")]
    public static int InlayHints(
        IntPtr workspace,
        byte* filePath,
        int startLine,
        int endLine,
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

            System.Collections.Generic.IReadOnlyList<InlayHint> hints = InlayHintService.HintsFor(
                ws.GetTreeFor(path), document, startLine, endLine, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(InlayHintJson.Write(hints));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Opens a workspace with no <c>.dme</c>: every file is its own single-file project.
    /// Added in ABI 0.20.
    /// </summary>
    /// <remarks>
    /// For a host with no project to point at — a single file, a folder with no <c>.dme</c>.
    /// Everything per-file still works, and each file resolves against the builtins plus itself.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_workspace_open_standalone")]
    public static int WorkspaceOpenStandalone(byte* rootDirectoryUtf8, IntPtr* outWorkspace)
    {
        if (outWorkspace is null)
            return Fail(DmStatus.InvalidArgument, "out_workspace is null");

        *outWorkspace = IntPtr.Zero;

        try
        {
            string? root = NativeStrings.Read(rootDirectoryUtf8);
            if (string.IsNullOrWhiteSpace(root))
                return Fail(DmStatus.InvalidArgument, "root directory is null or empty");

            Workspace standalone = Workspace.OpenStandalone(root);
            standalone.IconStateReader = DmiReader.StateNames;
            *outWorkspace = HandleTable.Alloc(standalone);
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>Whether DreamMaker's include block lists this file. Added in ABI 0.20.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_dme_is_ticked")]
    public static int DmeIsTicked(IntPtr workspace, byte* filePath)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return -1;

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return -1;

            return ws.IsFileTicked(path) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Fail(DmStatus.Internal, ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// The edit that adds a file to the <c>.dme</c>'s include block. Added in ABI 0.20.
    /// </summary>
    /// <remarks>
    /// Returns the edit rather than writing the file, because the <c>.dme</c> is usually open in
    /// the editor that asked and often dirty. Push it as a buffer first if you hold unsaved
    /// changes: offsets index the text this workspace currently sees.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_dme_tick")]
    public static int DmeTick(IntPtr workspace, byte* filePath, byte** outJson)
        => DmeEditExport(workspace, filePath, outJson, ticking: true);

    /// <summary>The edit that removes a file from the include block. Added in ABI 0.20.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_dme_untick")]
    public static int DmeUntick(IntPtr workspace, byte* filePath, byte** outJson)
        => DmeEditExport(workspace, filePath, outJson, ticking: false);

    /// <summary>
    /// Every file DreamMaker's own include block lists, in file order. Added in ABI 0.31.
    /// </summary>
    /// <remarks>
    /// The BLOCK, not the include graph — what the checkbox maintains, which is a different
    /// question from what the project compiles. An empty list is the answer when there is no
    /// <c>.dme</c>, no block, or nothing parseable in it, not an error.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_dme_entries")]
    public static int DmeEntries(IntPtr workspace, byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            StringBuilder json = new();
            json.Append("{\"entries\":[");

            bool first = true;

            foreach (string entry in ws.DmeEntries())
            {
                if (!first)
                    json.Append(',');

                first = false;
                SymbolJson.AppendString(json, entry);
            }

            json.Append("]}");

            *outJson = NativeStrings.Allocate(json.ToString());
            return (int)DmStatus.Ok;
        }
        catch (Exception ex)
        {
            return Fail(DmStatus.Internal, ex.Message);
        }
    }

    private static int DmeEditExport(IntPtr workspace, byte* filePath, byte** outJson, bool ticking)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Dm.Core.Includes.DmeEdit? edit = ticking
                ? ws.TickFile(path, out Dm.Core.Includes.DmeEditRefusal refusal)
                : ws.UntickFile(path, out refusal);

            *outJson = NativeStrings.Allocate(EditorJson.WriteDmeEdit(edit, refusal));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Where the TYPE of the symbol at a position is declared. Added in ABI 0.19.
    /// </summary>
    /// <remarks>
    /// One hop past <c>dm_definition_at</c>: on <c>var/mob/test/M</c> that goes to the variable,
    /// this goes to <c>/mob/test</c>. Only a written type is followed — an inferred one would send
    /// a caret into a guess.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_type_definition_at")]
    public static int TypeDefinitionAt(
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

            IReadOnlyList<DefinitionLocation> found = DefinitionService.TypeDefinitionAt(
                ws.GetTreeFor(path), document, line, character, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(DefinitionJson.Write(ws, found, (PositionEncoding)encoding));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Foldable regions for a file. Added in ABI 0.19.
    /// </summary>
    /// <remarks>
    /// Built from the AST rather than from indentation: DM's two block syntaxes nest freely, so
    /// folding by leading whitespace drops everything written inside braces. Needs no object tree.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_folding_ranges")]
    public static int FoldingRanges(IntPtr workspace, byte* filePath, byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            Document document = ws.GetDocument(path);

            *outJson = NativeStrings.Allocate(
                EditorJson.WriteFolding(FoldingService.RangesFor(document)));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Resolved <c>#include</c> targets in a file, for clickable navigation. Added in ABI 0.19.
    /// </summary>
    /// <remarks>
    /// Per file and off the token stream, so a link works before the project has been walked and a
    /// directive inside a comment is correctly not one. An unresolved include yields no link.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_document_links")]
    public static int DocumentLinks(IntPtr workspace, byte* filePath, int encoding, byte** outJson)
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

            *outJson = NativeStrings.Allocate(EditorJson.WriteLinks(
                DocumentLinkService.LinksFor(document, ws.LibraryRoot),
                document.Text,
                (PositionEncoding)encoding));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// The icon states in a <c>.dmi</c>. Added in ABI 0.24, and M8.
    /// </summary>
    /// <remarks>
    /// Reads from disk rather than through a buffer: a <c>.dmi</c> is a PNG, and the pushed-buffer
    /// rule is about text a client is editing. A file that is not an icon answers DM_OK with
    /// <c>isDmi: false</c> rather than an error, because three of one real project's own
    /// <c>.dmi</c> files are zero bytes and a client should be able to say so without a failure
    /// path.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_icon_states")]
    public static int IconStates(IntPtr workspace, byte* filePath, byte** outJson)
    {
        if (outJson is null)
            return Fail(DmStatus.InvalidArgument, "out_json is null");

        *outJson = null;

        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return Fail(DmStatus.InvalidArgument, "file is null or empty");

            string resolved = ws.ResolvePath(path);

            if (!File.Exists(resolved))
                return Fail(DmStatus.NotFound, $"no such file: {resolved}");

            bool isDmi = DmiReader.TryRead(resolved, out DmiIcon icon);

            *outJson = NativeStrings.Allocate(IconJson.Write(isDmi, icon));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// The colours written in a file, with the text to write for each. Added in ABI 0.23.
    /// </summary>
    /// <remarks>
    /// Per file and off the token stream, like folding and links, so a swatch appears before the
    /// project has been walked and a colour inside a comment is correctly not one. Components are
    /// 0-255, which is what DM writes and reads; the presentations ride along because there are at
    /// most two and computing them is arithmetic on a colour already in hand.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_document_colors")]
    public static int DocumentColors(IntPtr workspace, byte* filePath, int encoding, byte** outJson)
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

            *outJson = NativeStrings.Allocate(EditorJson.WriteColors(
                ColorService.ColorsIn(document),
                document.Text,
                (PositionEncoding)encoding));

            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Whether the <c>.dme</c>'s include walk reaches this file. Added in ABI 0.19.
    /// </summary>
    /// <remarks>
    /// 1 in the project, 0 outside it, -1 on a bad handle. A pushed buffer for a path the project
    /// does not include succeeds and then resolves nothing, which is indistinguishable from a bug
    /// in the client — this is how a client tells the two apart and says so.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_file_in_project")]
    public static int FileInProject(IntPtr workspace, byte* filePath)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return -1;

            string? path = NativeStrings.Read(filePath);
            if (string.IsNullOrWhiteSpace(path))
                return -1;

            return ws.IsFileInProject(path) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Fail(DmStatus.Internal, ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Whether the object tree exists right now. Added in ABI 0.15.
    /// </summary>
    /// <remarks>
    /// The readiness signal both IDE integrations were inferring from a call taking a long time:
    /// the first tree-backed query after an edit rebuilds, and a client that knows a build is
    /// coming can say "indexing" instead of freezing. Costs nothing to ask — it reads a field.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_tree_ready")]
    public static int TreeReady(IntPtr workspace)
    {
        try
        {
            return HandleTable.TryGet(workspace, out Workspace ws)
                ? ws.IsTreeBuilt ? 1 : 0
                : -1;
        }
        catch (Exception ex)
        {
            Fail(DmStatus.Internal, ex.Message);
            return -1;
        }
    }

    /// <summary>
    /// Builds the object tree now, blocking until it exists. Added in ABI 0.15.
    /// </summary>
    /// <remarks>
    /// The warm-at-open call: an IDE can pay the cold cost at a moment of its choosing — a splash
    /// screen, a background thread at startup — instead of on the user's first completion. A warm
    /// tree makes this a no-op, so calling it defensively is free.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_build_tree")]
    public static int BuildTree(IntPtr workspace)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            ws.GetObjectTree();
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
            all.AddRange(Binder.Bind(ws.GetTreeFor(path), parse.Root, document.Path));

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
                ws.GetTreeFor(path), document, line, character, (PositionEncoding)encoding,
                macros: ws.GetMacroTable());

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
                ws.GetTreeFor(path), document, line, character, (PositionEncoding)encoding,
                macros: ws.GetMacroTable());

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
                ws.GetTreeFor(path), document, line, character, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(SignatureJson.Write(help));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Renames the symbol at a position: the provable edits, plus the sites rename refuses to
    /// guess about. Added in ABI 0.27.
    /// </summary>
    /// <remarks>
    /// A refusal is <c>DM_OK</c> with a <c>refusal</c> word rather than an error, because "this
    /// cannot be renamed" is an answer. The <c>uncertain</c> list is the point of the call: `:`
    /// accesses, untyped receivers and string dispatch can hold live sites no resolver can prove,
    /// and applying the edits without showing that list is how a game breaks silently.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_rename_at")]
    public static int RenameAt(
        IntPtr workspace,
        byte* filePath,
        int line,
        int character,
        int encoding,
        byte* newName,
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

            string? replacement = NativeStrings.Read(newName);
            if (string.IsNullOrEmpty(replacement))
                return Fail(DmStatus.InvalidArgument, "new_name is null or empty");

            RenameResult result = ws.RenameAt(
                path, line, character, replacement, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(RenameJson.Write(ws, result, (PositionEncoding)encoding));
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
                ws.GetTreeFor(path),
                document,
                line,
                character,
                ws.GetMacroNames(),
                ws.GetFileText,
                (PositionEncoding)encoding,
                default,
                ws.CompletionLimit);

            *outJson = NativeStrings.Allocate(CompletionJson.Write(result));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// Caps every completion list for this workspace, or 0 for no cap. Added in ABI 0.18.
    /// </summary>
    /// <remarks>
    /// Off by default, because a capped list is unsafe for a client that filters by the typed
    /// prefix locally — it would silently miss the item being typed toward. Switch it on and read
    /// <c>truncated</c> on the response to know when that happened.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_set_completion_limit")]
    public static int SetCompletionLimit(IntPtr workspace, int limit)
    {
        try
        {
            if (!HandleTable.TryGet(workspace, out Workspace ws))
                return Fail(DmStatus.InvalidHandle, "workspace handle is invalid or closed");

            if (limit < 0)
                return Fail(DmStatus.InvalidArgument, "limit must be zero or positive");

            ws.CompletionLimit = limit;
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// The completion list with no documentation attached. Added in ABI 0.17.
    /// </summary>
    /// <remarks>
    /// A bare identifier on /tg/station offers 19,898 items and the user reads one. Measured, this
    /// cuts 12.7% of the payload and no measurable time — the lookups run over cached text. Pair
    /// with <see cref="CompleteResolve"/> on the highlighted item.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_complete_brief")]
    public static int CompleteBrief(
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

            CompletionResult result = CompletionService.CompleteBriefAt(
                ws.GetObjectTree(), document, line, character, ws.GetMacroNames(),
                (PositionEncoding)encoding, default, ws.CompletionLimit);

            *outJson = NativeStrings.Allocate(CompletionJson.Write(result));
            return Ok();
        }
        catch (Exception ex)
        {
            return Fail(Classify(ex), ex.Message);
        }
    }

    /// <summary>
    /// The documentation for one item of the list a position offers. Added in ABI 0.17.
    /// </summary>
    /// <remarks>
    /// Stateless: the position and the item's name identify it, so nothing is retained between the
    /// list call and this one. DM has no overloads, so a name at a position is unambiguous.
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) }, EntryPoint = "dm_complete_resolve")]
    public static int CompleteResolve(
        IntPtr workspace,
        byte* filePath,
        int line,
        int character,
        byte* itemName,
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

            string? name = NativeStrings.Read(itemName);
            if (string.IsNullOrWhiteSpace(name))
                return Fail(DmStatus.InvalidArgument, "name is null or empty");

            Document document = ws.GetDocument(path);

            string documentation = CompletionService.ResolveDocumentation(
                ws.GetObjectTree(), document, line, character, name, ws.GetMacroNames(),
                ws.GetFileText, (PositionEncoding)encoding);

            *outJson = NativeStrings.Allocate(CompletionJson.WriteDocumentation(documentation));
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
