using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Dm.Native;

/// <summary>
/// UTF-8 string marshalling across the C ABI.
/// </summary>
/// <remarks>
/// Strings handed out are copied into unmanaged memory and owned by the caller, who frees them
/// with <c>dm_free</c>. A pointer into managed memory is never returned: the GC may move or
/// collect it, and no client can be expected to reason about that.
/// </remarks>
internal static unsafe class NativeStrings
{
    /// <summary>Reads a null-terminated UTF-8 string. Returns null for a null pointer.</summary>
    public static string? Read(byte* utf8)
        => utf8 is null ? null : Marshal.PtrToStringUTF8((IntPtr)utf8);

    /// <summary>
    /// Copies a string into caller-owned unmanaged memory as null-terminated UTF-8.
    /// Returns null for a null input.
    /// </summary>
    public static byte* Allocate(string? value)
    {
        if (value is null)
            return null;

        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)byteCount + 1);

        fixed (char* chars = value)
        {
            Encoding.UTF8.GetBytes(chars, value.Length, buffer, byteCount);
        }

        buffer[byteCount] = 0;
        return buffer;
    }

    public static void Free(void* ptr)
    {
        if (ptr is not null)
            NativeMemory.Free(ptr);
    }
}
