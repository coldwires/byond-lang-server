using System;
using System.IO;
using System.Runtime.InteropServices;
using Dm.Core;

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
