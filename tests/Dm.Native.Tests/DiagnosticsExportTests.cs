using System;
using System.Runtime.InteropServices;
using System.Text;
using Dm.Native;
using Xunit;

namespace Dm.Native.Tests;

/// <summary>
/// Drives <c>dm_diagnostics</c> the way C does. The semantic set — the binder's checks — crosses
/// the ABI only through this export, so this is the boundary test for the capability the matrix
/// used to mark as a gap.
/// </summary>
[Collection("handle table")] // shares the static HandleTable with the count-asserting tests
public unsafe class DiagnosticsExportTests
{
    private static readonly unsafe delegate* unmanaged[Cdecl]<byte*, IntPtr*, int> Open = &Exports.WorkspaceOpen;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, void> Close = &Exports.WorkspaceClose;
    private static readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, byte*, int, byte**, int> Diagnostics =
        &Exports.Diagnostics;

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

    /// <summary>Opens a workspace over a temp .dme whose one file is on disk, as the tree needs.</summary>
    private static IntPtr OpenWith(string file, string source, out string tempDirectory)
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "dm_diagnostics_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        string dme = Path.Combine(tempDirectory, "test.dme");
        File.WriteAllText(dme, $"#include \"{file}\"\n");
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

        return workspace;
    }

    [Fact]
    public void The_semantic_set_crosses_the_boundary()
    {
        IntPtr ws = OpenWith(
            "diag.dm",
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\treturn g.nowhere\n",
            out string dir);

        try
        {
            byte* filePath = Utf8("diag.dm");
            byte* json;

            Assert.Equal(Ok, Diagnostics(ws, filePath, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            Assert.Contains("\"id\":\"DM0400\"", document);
            Assert.Contains("\"severity\":\"error\"", document);
            Assert.Contains("nowhere", document);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// The walk's own diagnostics cross the boundary too. No <c>ParseResult</c> holds them, so an
    /// export assembling parse plus binder misses them however complete both halves are — which is
    /// what this one did until 2026-08-16, while <c>dmc diagdiff</c> counted them the whole time.
    /// Tested here rather than only managed-side because the previous boundary defect of this shape
    /// (the inlay <c>parameter</c> kind crossing as <c>"unknown"</c>) was invisible to every test
    /// that stopped short of the ABI.
    /// </summary>
    [Fact]
    public void The_walks_own_diagnostics_cross_the_boundary()
    {
        IntPtr ws = OpenWith("diag.dm", "/proc/f()\n\treturn 1\n#warn the walk says this\n", out string dir);

        try
        {
            byte* filePath = Utf8("diag.dm");
            byte* json;

            Assert.Equal(Ok, Diagnostics(ws, filePath, Utf16, &json));
            NativeMemory.Free(filePath);

            string document = ReadAndFree(json);

            Assert.Contains("the walk says this", document);

            // On its own line rather than collapsed onto the .dme at line 0, which is what made
            // every one of these a guaranteed miss against dm.exe before the attribution landed.
            Assert.Contains("\"startLine\":2", document);
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_clean_file_answers_an_empty_array()
    {
        IntPtr ws = OpenWith("diag.dm", "/mob/guy\n\tvar/health = 1\n", out string dir);

        try
        {
            byte* filePath = Utf8("diag.dm");
            byte* json;

            Assert.Equal(Ok, Diagnostics(ws, filePath, Utf16, &json));
            NativeMemory.Free(filePath);

            Assert.Equal("{\"diagnostics\":[]}", ReadAndFree(json));
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Opens a workspace over a temp .dme including each file in order.</summary>
    private static IntPtr OpenWithFiles(out string tempDirectory, params (string File, string Source)[] files)
    {
        tempDirectory = Path.Combine(Path.GetTempPath(), "dm_diagnostics_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        StringBuilder dmeText = new();
        foreach ((string file, string source) in files)
        {
            dmeText.Append($"#include \"{file}\"\n");
            File.WriteAllText(Path.Combine(tempDirectory, file), source);
        }

        string dme = Path.Combine(tempDirectory, "test.dme");
        File.WriteAllText(dme, dmeText.ToString());

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

    private static string DiagnosticsOf(IntPtr ws, string file)
    {
        byte* filePath = Utf8(file);
        byte* json;

        Assert.Equal(Ok, Diagnostics(ws, filePath, Utf16, &json));
        NativeMemory.Free(filePath);

        return ReadAndFree(json);
    }

    /// <summary>
    /// A macro is not an undefined var. dm.exe compiles all three of these clean. The export bound
    /// each file's own text, where a macro is still its bare name, while the tree and
    /// <c>dmc diagdiff</c> read the expanded stream, so every macro use crossed as DM0400 and the
    /// zero-invented figure, measured through diagdiff, could not see it (found 2026-11-06 by an
    /// IDE pushing a project whose <c>#define MAX_HP</c> sat in an earlier include).
    /// </summary>
    [Fact]
    public void A_macro_in_the_same_file_is_not_an_undefined_var()
    {
        IntPtr ws = OpenWith("diag.dm", "#define X 1\n/mob/guy\n\tvar/health = X\n", out string dir);

        try
        {
            Assert.Equal("{\"diagnostics\":[]}", DiagnosticsOf(ws, "diag.dm"));
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_macro_from_an_earlier_include_is_not_an_undefined_var()
    {
        IntPtr ws = OpenWithFiles(out string dir,
            ("defs.dm", "#define MAX_HP 100\n"),
            ("mob.dm", "/mob/player\n\tvar/hp = MAX_HP\n/mob/proc/f()\n\treturn MAX_HP\n"));

        try
        {
            Assert.Equal("{\"diagnostics\":[]}", DiagnosticsOf(ws, "mob.dm"));
        }
        finally
        {
            Close(ws);
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Binding the expanded stream must still report against the file as typed: the real error
    /// after a macro on the same line keeps its own column.
    /// </summary>
    [Fact]
    public void A_real_error_after_a_macro_keeps_its_column()
    {
        IntPtr ws = OpenWith("diag.dm", "#define X 1\n/proc/f()\n\treturn X + nowhere\n", out string dir);

        try
        {
            string document = DiagnosticsOf(ws, "diag.dm");

            Assert.Contains("\"id\":\"DM0400\"", document);
            Assert.Contains("nowhere: undefined var", document);
            Assert.DoesNotContain("X: undefined var", document);
            // "\treturn X + " is 12 characters, so "nowhere" spans 12..19 on line 2.
            Assert.Contains("\"startLine\":2,\"startChar\":12,\"endLine\":2,\"endChar\":19", document);
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
        IntPtr ws = OpenWith("diag.dm", "/mob/guy\n", out string dir);

        try
        {
            byte* filePath = Utf8("diag.dm");
            byte* json = (byte*)1;

            Assert.Equal(InvalidArg, Diagnostics(ws, filePath, 99, &json));
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
