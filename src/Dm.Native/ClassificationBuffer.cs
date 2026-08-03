using System;
using System.Runtime.InteropServices;

namespace Dm.Native;

/// <summary>
/// Classification results packed into unmanaged memory for the client to read directly.
/// </summary>
/// <remarks>
/// Layout is <c>count</c> consecutive triples of <c>int32</c>: offset, length, kind. A client
/// copies the whole block in one read.
///
/// The block is unmanaged rather than a pinned managed array because it outlives the call: the
/// client reads it after <c>dm_classify_range</c> returns and frees it explicitly. Handing out a
/// pointer into the managed heap would leave the GC free to move it in between.
/// </remarks>
internal sealed unsafe class ClassificationBuffer : IDisposable
{
    private int* _data;

    public ClassificationBuffer(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "count must not be negative");

        Count = count;

        // A zero-length allocation would return a pointer the client must still not dereference.
        // Allocating one slot keeps Data non-null so callers do not need a special case.
        nuint bytes = (nuint)Math.Max(count * 3, 1) * sizeof(int);
        _data = (int*)NativeMemory.Alloc(bytes);
    }

    public int Count { get; }

    public int* Data => _data;

    public void Dispose()
    {
        if (_data is null)
            return;

        NativeMemory.Free(_data);
        _data = null;
    }
}
