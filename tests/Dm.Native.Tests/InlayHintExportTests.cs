using System;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Native;
using Xunit;

namespace Dm.Native.Tests;

/// <summary>
/// Drives <c>dm_inlay_hints</c> the way C does — the inferred-type annotations for untyped
/// locals, which are inference <c>dm.exe</c> does not do and exactly why they are worth showing.
/// </summary>
[Collection("handle table")] // shares the static HandleTable with the count-asserting tests
public unsafe class InlayHintExportTests
{
    private static readonly unsafe delegate* unmanaged[Cdecl]<byte*, IntPtr*, int> Open = &Exports.WorkspaceOpen;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, void> Close = &Exports.WorkspaceClose;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int, int, byte**, int> Hints =
        &Exports.InlayHints;

    private const int Utf16 = 1;
    private const int Ok = 0;
    private const int InvalidArg = 1;

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

    private static IntPtr OpenWith(string source, out string tempDirectory)
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "dm_hints_" + Guid.NewGuid().ToString("n"));
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
    public void An_untyped_local_hints_and_a_typed_one_does_not()
    {
        IntPtr ws = OpenWith(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item\n\tvar/obj/item/y = new\n\treturn x\n",
            out string dir);

        try
        {
            byte* filePath = Utf8("code.dm");
            byte* json;

            Assert.Equal(Ok, Hints(ws, filePath, 0, 100, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            Assert.Contains("\"label\":\": /obj/item\"", document);
            Assert.Contains("\"kind\":\"type\"", document);

            // One hint: `x` is inferred, `y` is written.
            Assert.Equal(2, document.Split("\"line\":").Length);
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
        IntPtr ws = OpenWith("/obj/item\n", out string dir);

        try
        {
            byte* filePath = Utf8("code.dm");
            byte* json = (byte*)1;

            Assert.Equal(InvalidArg, Hints(ws, filePath, 0, 100, 99, &json));
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
