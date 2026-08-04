using System;
using System.Collections.Generic;

namespace Dm.Native;

/// <summary>
/// Maps opaque native handles to managed objects.
/// </summary>
/// <remarks>
/// <para>
/// A handle is a monotonically increasing id, never reused. Zero is never issued, so a zeroed or
/// null handle is always invalid, and a handle held past its close is invalid because its id is gone
/// from the table — it cannot resolve to whatever was opened next. That matters because the
/// consumers are three independently written clients; a use-after-close in any of them should be a
/// clean error rather than memory corruption.
/// </para>
/// <para>
/// **This used to pack a generation and a slot index into the pointer**, which required 64-bit
/// pointers and silently did not have them on <c>win-x86</c>. The generation occupied the high 32
/// bits, the cast to <see cref="IntPtr"/> discarded them, and every call taking a handle returned
/// <c>DM_ERR_INVALID_HANDLE</c> — while <c>dm_workspace_open</c> succeeded, because it is the one
/// entry point that never unpacks. Closing failed the same way, so no slot was ever released and
/// every open leaked its workspace and all five caches. Found by the 32-bit client, not by us: the
/// unit tests and the smoke test both run 64-bit, where the packing works.
/// </para>
/// <para>
/// An id has no bit budget to get right on one architecture and wrong on another, which is why this
/// removes the class of bug rather than resizing it. The cost is a dictionary lookup where there was
/// an array index, which is noise beside anything a handle is used to ask for.
/// </para>
/// </remarks>
internal static class HandleTable
{
    private static readonly object Sync = new();
    private static readonly Dictionary<nint, object> Live = new();

    /// <summary>
    /// Ids start well above the values a confused client is likely to pass.
    /// </summary>
    /// <remarks>
    /// The packed scheme rejected small integers for free, because anything without a generation in
    /// its high bits could not have been issued. A bare counter would hand out 1, 2, 3 and make a
    /// stray <c>1</c> resolve to a live workspace. Biasing costs nothing and keeps garbage failing
    /// cleanly, which is most of why this table validates at all. Leaves ~850M ids on 32-bit.
    /// </remarks>
    private const nint FirstId = 0x4D4D0000;

    private static nint _next = FirstId - 1;

    /// <summary>Handles currently open. Lets a test assert that closing actually releases.</summary>
    public static int Count
    {
        get
        {
            lock (Sync)
                return Live.Count;
        }
    }

    public static IntPtr Alloc(object target)
    {
        lock (Sync)
        {
            // Saturating rather than wrapping: a wrapped id could collide with a live handle, which
            // is the one failure this table exists to prevent. On 32-bit that ceiling is 2^31 opens,
            // which no editor session approaches, and refusing to allocate is a clean error if one
            // ever did.
            if (_next == nint.MaxValue)
                throw new InvalidOperationException("handle space exhausted");

            nint id = ++_next;
            Live.Add(id, target);

            return id;
        }
    }

    public static bool TryGet<T>(IntPtr handle, out T value) where T : class
    {
        value = null!;

        lock (Sync)
        {
            if (!Live.TryGetValue(handle, out object? target) || target is not T typed)
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
        lock (Sync)
        {
            return Live.Remove(handle, out object? target) ? target : null;
        }
    }
}
