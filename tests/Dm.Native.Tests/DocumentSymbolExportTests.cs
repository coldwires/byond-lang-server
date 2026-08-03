using System;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Core;
using Dm.Native;
using Xunit;

namespace Dm.Native.Tests;

/// <summary>
/// Drives <c>dm_document_symbols</c> the way C does — raw pointers, out-params, caller-owned
/// buffers. The C++ smoke test covers the same ground against the published binary; this runs in
/// <c>dotnet test</c>, so a boundary regression is caught without a NativeAOT publish.
/// </summary>
public unsafe class DocumentSymbolExportTests
{
    // Taken as function pointers because [UnmanagedCallersOnly] methods cannot be called directly
    // from managed code — which is the point: this exercises the same entry a C caller uses.
    private static readonly unsafe delegate* unmanaged<byte*, IntPtr*, int> Open = &Exports.WorkspaceOpen;
    private static readonly unsafe delegate* unmanaged<IntPtr, void> Close = &Exports.WorkspaceClose;
    private static readonly unsafe delegate* unmanaged<IntPtr, byte*, byte*, int, int> Push = &Exports.SetBuffer;
    private static readonly unsafe delegate* unmanaged<IntPtr, byte*, int, byte**, int> Symbols =
        &Exports.DocumentSymbols;

    private const int Utf16 = 1;
    private const int Ok = 0;
    private const int InvalidArg = 1;
    private const int InvalidHandle = 2;

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
        tempDirectory = Path.Combine(Path.GetTempPath(), "dm_symbols_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        string dme = Path.Combine(tempDirectory, "test.dme");
        File.WriteAllText(dme, "#include \"test.dm\"\n");

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
    public void Returns_the_outline_as_json()
    {
        IntPtr ws = OpenWith("outline.dm", "/obj/item\n\tvar/hp = 1\n\tproc/use()\n\t\treturn\n", out string dir);

        try
        {
            byte* filePath = Utf8("outline.dm");
            byte* json;

            Assert.Equal(Ok, Symbols(ws, filePath, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            Assert.Contains("\"symbols\":", document);
            Assert.Contains("\"diagnostics\":[]", document);
            Assert.Contains("\"name\":\"item\"", document);
            Assert.Contains("\"name\":\"hp\"", document);
            Assert.Contains("\"name\":\"use\"", document);

            // The proc's kind is DM_SYMBOL_PROC, and members nest under the type.
            Assert.Contains("\"kind\":2", document);
            Assert.Contains("\"children\":[{", document);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Syntax errors ride along, so a client can draw squiggles from the same call.</summary>
    [Fact]
    public void Reports_syntax_diagnostics()
    {
        IntPtr ws = OpenWith("broken.dm", "/obj/a\n$$$ ???\n/obj/b\n", out string dir);

        try
        {
            byte* filePath = Utf8("broken.dm");
            byte* json;

            Assert.Equal(Ok, Symbols(ws, filePath, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            Assert.Contains("\"id\":\"DM", document);
            Assert.Contains("\"message\":", document);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A name holding a quote or a backslash has to survive as JSON. DM allows backslash escapes
    /// inside identifiers, so this is reachable from real code rather than only from a hostile test.
    /// </summary>
    [Fact]
    public void Escapes_names_that_would_break_json()
    {
        IntPtr ws = OpenWith("escaped.dm", "/mob/verb/\\~Admin_Chat(T as text)\n\treturn\n", out string dir);

        try
        {
            byte* filePath = Utf8("escaped.dm");
            byte* json;

            Assert.Equal(Ok, Symbols(ws, filePath, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            // The backslash is escaped rather than emitted raw, which would end the string early.
            Assert.DoesNotContain("\"name\":\"\\~", document);
            Assert.Contains("\\\\", document);
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
        IntPtr ws = OpenWith("outline.dm", "/obj/item\n", out string dir);

        try
        {
            byte* filePath = Utf8("outline.dm");
            byte* json = (byte*)1;

            Assert.Equal(InvalidArg, Symbols(ws, filePath, 99, &json));
            Assert.True(json is null, "the out-param must be cleared before any work");

            NativeMemory.Free(filePath);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A closed handle returns a clean error rather than resolving to a recycled slot.</summary>
    [Fact]
    public void Rejects_a_stale_workspace_handle()
    {
        IntPtr ws = OpenWith("outline.dm", "/obj/item\n", out string dir);
        Close(ws);

        try
        {
            byte* filePath = Utf8("outline.dm");
            byte* json;

            Assert.Equal(InvalidHandle, Symbols(ws, filePath, Utf16, &json));
            Assert.True(json is null);

            NativeMemory.Free(filePath);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
