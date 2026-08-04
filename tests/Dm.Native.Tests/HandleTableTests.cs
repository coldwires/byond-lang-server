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
    public void A_stale_handle_never_resolves_however_many_follow_it()
    {
        Target first = new("first");
        IntPtr stale = HandleTable.Alloc(first);
        HandleTable.Release(stale);

        // Ids are never reused, so no later handle can collide with the stale one and it can never
        // start resolving again. This used to be a slot-reuse test, which asserted the mechanism
        // rather than the contract.
        List<IntPtr> allocated = new();

        for (int i = 0; i < 64; i++)
        {
            IntPtr handle = HandleTable.Alloc(new Target($"later-{i}"));

            Assert.NotEqual(stale, handle);
            Assert.False(HandleTable.TryGet(stale, out Target _));

            allocated.Add(handle);
        }

        foreach (IntPtr handle in allocated)
            HandleTable.Release(handle);
    }

    /// <remarks>
    /// The half of the x86 handle bug that was not a wrong answer but a leak: Release could fail to
    /// find the handle it had just issued and return without clearing anything, so every workspace
    /// ever opened stayed live along with its documents and all five caches.
    /// </remarks>
    [Fact]
    public void Closing_actually_releases()
    {
        int before = HandleTable.Count;

        IntPtr handle = HandleTable.Alloc(new Target("released"));
        Assert.Equal(before + 1, HandleTable.Count);

        Assert.NotNull(HandleTable.Release(handle));
        Assert.Equal(before, HandleTable.Count);

        // Double close stays a no-op, and still releases nothing a second time.
        Assert.Null(HandleTable.Release(handle));
        Assert.Equal(before, HandleTable.Count);
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

}
