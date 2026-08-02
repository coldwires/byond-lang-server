using System;
using System.Collections.Generic;

namespace Dm.Native;

/// <summary>
/// Maps opaque native handles to managed objects.
/// </summary>
/// <remarks>
/// A handle is <c>(generation &lt;&lt; 32) | (index + 1)</c>. Index 0 is never issued, so a zeroed
/// or null handle is always invalid.
///
/// Slots are recycled with an incremented generation, so a handle held past its close returns
/// <see cref="DmStatus.InvalidHandle"/> rather than resolving to whatever object later took the
/// slot. This matters because the consumers are three independently written clients; a use-after-close
/// in any of them should be a clean error, not memory corruption.
///
/// Requires 64-bit pointers. Only 64-bit RIDs are shipped.
/// </remarks>
internal static class HandleTable
{
    private struct Slot
    {
        public object? Target;
        public uint Generation;
        public bool InUse;
    }

    private static readonly object Sync = new();
    private static readonly List<Slot> Slots = new();
    private static readonly Stack<int> Free = new();

    public static IntPtr Alloc(object target)
    {
        lock (Sync)
        {
            int index;

            if (Free.Count > 0)
            {
                index = Free.Pop();
                Slot recycled = Slots[index];
                recycled.Target = target;
                recycled.InUse = true;
                Slots[index] = recycled;
            }
            else
            {
                index = Slots.Count;
                Slots.Add(new Slot { Target = target, Generation = 1, InUse = true });
            }

            return Pack(index, Slots[index].Generation);
        }
    }

    public static bool TryGet<T>(IntPtr handle, out T value) where T : class
    {
        value = null!;

        if (!Unpack(handle, out int index, out uint generation))
            return false;

        lock (Sync)
        {
            if (index < 0 || index >= Slots.Count)
                return false;

            Slot slot = Slots[index];
            if (!slot.InUse || slot.Generation != generation)
                return false;

            if (slot.Target is not T typed)
                return false;

            value = typed;
            return true;
        }
    }

    /// <summary>
    /// Releases a handle and returns the object it referenced, or null if the handle was already
    /// invalid. Double-close is therefore a no-op rather than an error.
    /// </summary>
    public static object? Release(IntPtr handle)
    {
        if (!Unpack(handle, out int index, out uint generation))
            return null;

        lock (Sync)
        {
            if (index < 0 || index >= Slots.Count)
                return null;

            Slot slot = Slots[index];
            if (!slot.InUse || slot.Generation != generation)
                return null;

            object? target = slot.Target;

            slot.Target = null;
            slot.InUse = false;
            // Wrapping is fine: a collision needs 2^32 reuses of one slot while a stale handle to
            // that exact generation is still held.
            slot.Generation = slot.Generation == uint.MaxValue ? 1 : slot.Generation + 1;
            Slots[index] = slot;

            Free.Push(index);
            return target;
        }
    }

    private static IntPtr Pack(int index, uint generation)
        => (IntPtr)(((long)generation << 32) | (uint)(index + 1));

    /// <summary>
    /// Splits a handle into slot index and generation, rejecting anything that cannot have been
    /// issued by <see cref="Alloc"/>.
    /// </summary>
    /// <remarks>
    /// The <c>low &gt; int.MaxValue</c> check is load-bearing. Clients pass arbitrary pointer
    /// values across the ABI, and without it a low word of 0xFFFFFFFF casts to -1 and yields a
    /// negative index, which reads past the start of the slot list.
    /// </remarks>
    private static bool Unpack(IntPtr handle, out int index, out uint generation)
    {
        index = -1;

        long raw = handle.ToInt64();
        uint low = (uint)(raw & 0xFFFFFFFF);
        generation = (uint)(raw >> 32);

        if (low == 0 || generation == 0 || low > int.MaxValue)
            return false;

        index = (int)low - 1;
        return true;
    }
}
