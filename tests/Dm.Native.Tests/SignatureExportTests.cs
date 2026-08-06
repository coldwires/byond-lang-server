using System;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Native;
using Xunit;

namespace Dm.Native.Tests;

/// <summary>
/// Drives <c>dm_signature_at</c> the way C does — raw pointers, out-params, caller-owned buffers.
/// The C++ smoke test covers the same ground against the published binary; this runs in
/// <c>dotnet test</c>, so a boundary regression is caught without a NativeAOT publish.
/// </summary>
[Collection("handle table")] // shares the static HandleTable with the count-asserting tests
public unsafe class SignatureExportTests
{
    private static readonly unsafe delegate* unmanaged[Cdecl]<byte*, IntPtr*, int> Open = &Exports.WorkspaceOpen;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, void> Close = &Exports.WorkspaceClose;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*, int, int> Push = &Exports.SetBuffer;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int, int, byte**, int> Signature =
        &Exports.SignatureAt;

    private const int Utf16 = 1;
    private const int Ok = 0;
    private const int InvalidArg = 1;

    private const string Source =
        "/mob/guy\n\tproc/heal(mob/target, amount as num, silent = 0)\n\t\treturn\n" +
        "/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal(g, 5)\n";

    private static byte* Utf8(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)bytes.Length);

        for (int i = 0; i < bytes.Length; i++)
            buffer[i] = bytes[i];

        return buffer;
    }

    private static string ReadAndFree(byte* utf8)
    {
        string value = Marshal.PtrToStringUTF8((IntPtr)utf8) ?? string.Empty;
        NativeStrings.Free(utf8);
        return value;
    }

    /// <summary>Opens a workspace over a temp .dme and pushes one buffer into it.</summary>
    private static IntPtr OpenWith(string file, string source, out string tempDirectory)
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "dm_signature_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        string dme = Path.Combine(tempDirectory, "test.dme");
        File.WriteAllText(dme, $"#include \"{file}\"\n");

        // The object tree is built from the include graph, which walks files the .dme reaches on
        // disk; the pushed buffer then overrides the content. A buffer for a file that exists
        // nowhere is enough for the outline but not for tree-backed answers.
        File.WriteAllText(Path.Combine(tempDirectory, file), source);

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

        byte* filePath = Utf8(file);
        byte[] utf8 = Encoding.UTF8.GetBytes(source);

        fixed (byte* content = utf8)
        {
            Assert.Equal(Ok, Push(workspace, filePath, content, utf8.Length));
        }

        NativeMemory.Free(filePath);
        return workspace;
    }

    [Fact]
    public void Returns_the_enclosing_call_and_active_parameter()
    {
        IntPtr ws = OpenWith("sig.dm", Source, out string dir);

        try
        {
            byte* filePath = Utf8("sig.dm");
            byte* json;

            // Line 5 is `\tg.heal(g, 5)`; character 11 sits in the second argument.
            Assert.Equal(Ok, Signature(ws, filePath, 5, 11, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            Assert.Contains("\"detail\":\"/mob/guy/heal\"", document);
            Assert.Contains("\"name\":\"heal\"", document);

            // A parameter's type renders resolved - `/mob/target` for source reading `mob/target` -
            // exactly as completion and hover render the same proc.
            Assert.Contains("\"label\":\"heal(/mob/target, amount as num, silent = 0)\"", document);
            Assert.Contains("\"parameters\":[\"/mob/target\",\"amount as num\",\"silent = 0\"]", document);
            Assert.Contains("\"activeParameter\":1", document);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A caret outside any argument list is the ordinary case: an empty object, DM_OK.</summary>
    [Fact]
    public void Returns_an_empty_object_outside_a_call()
    {
        IntPtr ws = OpenWith("sig.dm", Source, out string dir);

        try
        {
            byte* filePath = Utf8("sig.dm");
            byte* json;

            Assert.Equal(Ok, Signature(ws, filePath, 4, 0, Utf16, &json));
            NativeMemory.Free(filePath);

            Assert.Equal("{}", ReadAndFree(json));
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Rejects_an_unknown_encoding_and_clears_the_out_param()
    {
        IntPtr ws = OpenWith("sig.dm", Source, out string dir);

        try
        {
            byte* filePath = Utf8("sig.dm");
            byte* json = (byte*)1;

            Assert.Equal(InvalidArg, Signature(ws, filePath, 5, 11, 99, &json));
            Assert.True(json is null, "the out-param must be cleared before any work");

            NativeMemory.Free(filePath);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }
}
