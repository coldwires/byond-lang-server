using System;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Native;
using Xunit;

namespace Dm.Native.Tests;

/// <summary>
/// Drives <c>dm_tree_ready</c> and <c>dm_build_tree</c> the way C does — the readiness signal both
/// IDE integrations were inferring from a call taking a long time, and the warm-at-open call that
/// lets a client pay the cold cost at a moment of its choosing.
/// </summary>
[Collection("handle table")] // shares the static HandleTable with the count-asserting tests
public unsafe class ReadinessExportTests
{
    private static readonly unsafe delegate* unmanaged[Cdecl]<byte*, IntPtr*, int> Open = &Exports.WorkspaceOpen;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, void> Close = &Exports.WorkspaceClose;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int, int> Push = &Exports.SetBuffer;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, int> Ready = &Exports.TreeReady;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, int> Build = &Exports.BuildTree;

    private const int Ok = 0;
    private const int InvalidHandle = 2;

    private static byte* Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);

        for (int i = 0; i < bytes.Length; i++)
            buffer[i] = bytes[i];

        return buffer;
    }

    private static IntPtr OpenWith(string source, out string tempDirectory)
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "dm_ready_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        string dme = Path.Combine(tempDirectory, "test.dme");
        File.WriteAllText(dme, "#include \"code.dm\"\n");
        File.WriteAllText(Path.Combine(tempDirectory, "code.dm"), source);

        IntPtr workspace;
        byte* dmePath = Utf8(dme);

        try
        {
            Assert.Equal(Ok, Open(dmePath, &workspace));
        }
        finally
        {
            NativeMemory.Free(dmePath);
        }

        return workspace;
    }

    [Fact]
    public void Building_flips_the_signal_and_a_warm_tree_is_a_no_op()
    {
        IntPtr ws = OpenWith("/mob/guy\n\tvar/health = 1\n", out string dir);

        try
        {
            // Open does no parsing, so a fresh workspace reports no tree.
            Assert.Equal(0, Ready(ws));

            Assert.Equal(Ok, Build(ws));
            Assert.Equal(1, Ready(ws));

            // Warming a warm tree costs nothing and stays Ok.
            Assert.Equal(Ok, Build(ws));
            Assert.Equal(1, Ready(ws));
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A buffer change drops the tree, and the signal is how a client learns that.</summary>
    [Fact]
    public void A_buffer_push_drops_the_signal()
    {
        IntPtr ws = OpenWith("/mob/guy\n\tvar/health = 1\n", out string dir);

        try
        {
            Assert.Equal(Ok, Build(ws));
            Assert.Equal(1, Ready(ws));

            byte* path = Utf8("code.dm");
            byte* content = Utf8("/mob/guy\n\tvar/health = 2\n");

            Assert.Equal(Ok, Push(ws, path, content, -1));
            NativeMemory.Free(path);
            NativeMemory.Free(content);

            Assert.Equal(0, Ready(ws));
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_closed_handle_answers_in_each_calls_own_convention()
    {
        IntPtr ws = OpenWith("/mob/guy\n", out string dir);
        Close(ws);
        Directory.Delete(dir, true);

        // The boolean is -1, the status call the usual invalid-handle error.
        Assert.Equal(-1, Ready(ws));
        Assert.Equal(InvalidHandle, Build(ws));
    }
}
