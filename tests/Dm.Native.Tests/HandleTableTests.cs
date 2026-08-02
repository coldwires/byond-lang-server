using Dm.Native;

namespace Dm.Native.Tests;

/// <summary>
/// The C++ smoke test covers the happy path and one stale handle. These cover the cases that
/// matter when three independently written clients are calling in: slot reuse, generation
/// mismatch, wrong-type resolution, and double release.
/// </summary>
public class HandleTableTests
{
    private sealed class Target
    {
        public Target(string name) => Name = name;

        public string Name { get; }
    }

    private sealed class OtherTarget
    {
    }

    [Fact]
    public void Alloc_returns_a_non_null_handle()
    {
        IntPtr handle = HandleTable.Alloc(new Target("a"));

        Assert.NotEqual(IntPtr.Zero, handle);
    }

    [Fact]
    public void TryGet_resolves_the_same_instance()
    {
        Target target = new("a");
        IntPtr handle = HandleTable.Alloc(target);

        Assert.True(HandleTable.TryGet(handle, out Target resolved));
        Assert.Same(target, resolved);
    }

    [Fact]
    public void Release_returns_the_target_and_invalidates_the_handle()
    {
        Target target = new("a");
        IntPtr handle = HandleTable.Alloc(target);

        Assert.Same(target, HandleTable.Release(handle));
        Assert.False(HandleTable.TryGet(handle, out Target _));
    }

    [Fact]
    public void Releasing_twice_returns_null_rather_than_throwing()
    {
        IntPtr handle = HandleTable.Alloc(new Target("a"));

        Assert.NotNull(HandleTable.Release(handle));
        Assert.Null(HandleTable.Release(handle));
    }

    /// <summary>
    /// The reason handles carry a generation. Without it a stale handle would resolve to whatever
    /// object later occupied the slot, which is a silent wrong answer rather than an error.
    /// </summary>
    [Fact]
    public void A_stale_handle_does_not_resolve_after_its_slot_is_reused()
    {
        Target first = new("first");
        IntPtr stale = HandleTable.Alloc(first);
        HandleTable.Release(stale);

        // Allocate until the freed slot is handed back out.
        List<IntPtr> allocated = new();
        IntPtr reused = IntPtr.Zero;

        for (int i = 0; i < 64; i++)
        {
            IntPtr handle = HandleTable.Alloc(new Target($"reuse-{i}"));
            allocated.Add(handle);

            if (LowWord(handle) == LowWord(stale))
            {
                reused = handle;
                break;
            }
        }

        Assert.NotEqual(IntPtr.Zero, reused);
        Assert.NotEqual(stale, reused);

        Assert.False(HandleTable.TryGet(stale, out Target _));
        Assert.True(HandleTable.TryGet(reused, out Target current));
        Assert.NotSame(first, current);

        foreach (IntPtr handle in allocated)
            HandleTable.Release(handle);
    }

    [Fact]
    public void TryGet_fails_when_the_target_is_a_different_type()
    {
        IntPtr handle = HandleTable.Alloc(new OtherTarget());

        Assert.False(HandleTable.TryGet(handle, out Target _));
    }

    [Theory]
    [InlineData(0L)]              // null handle
    [InlineData(1L)]              // index without a generation
    [InlineData(1L << 32)]        // generation without an index
    [InlineData(long.MaxValue)]   // low word casts to a negative index
    [InlineData(-1L)]
    public void TryGet_rejects_malformed_handles(long raw)
    {
        Assert.False(HandleTable.TryGet(new IntPtr(raw), out Target _));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    [InlineData(-1L)]
    public void Release_of_a_malformed_handle_returns_null(long raw)
    {
        Assert.Null(HandleTable.Release(new IntPtr(raw)));
    }

    private static uint LowWord(IntPtr handle) => (uint)(handle.ToInt64() & 0xFFFFFFFF);
}
