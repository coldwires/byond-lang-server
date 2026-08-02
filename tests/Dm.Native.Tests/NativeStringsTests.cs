using Dm.Native;

namespace Dm.Native.Tests;

public unsafe class NativeStringsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("simple")]
    [InlineData("/obj/item/sword")]
    [InlineData("C:\\Users\\test\\game.dme")]
    [InlineData("naïve café")]          // 2-byte sequences
    [InlineData("日本語")]                // 3-byte sequences
    [InlineData("\U0001F600")]           // 4-byte sequence, surrogate pair on the managed side
    [InlineData("line\nbreak\ttab")]
    public void Round_trips_through_unmanaged_memory(string value)
    {
        byte* native = NativeStrings.Allocate(value);

        try
        {
            Assert.True(native is not null);
            Assert.Equal(value, NativeStrings.Read(native));
        }
        finally
        {
            NativeStrings.Free(native);
        }
    }

    [Fact]
    public void Allocated_text_is_null_terminated()
    {
        const string value = "abc";
        byte* native = NativeStrings.Allocate(value);

        try
        {
            Assert.Equal((byte)'a', native[0]);
            Assert.Equal((byte)'b', native[1]);
            Assert.Equal((byte)'c', native[2]);
            Assert.Equal(0, native[3]);
        }
        finally
        {
            NativeStrings.Free(native);
        }
    }

    [Fact]
    public void Allocate_returns_null_for_a_null_input()
    {
        Assert.True(NativeStrings.Allocate(null) is null);
    }

    [Fact]
    public void Read_returns_null_for_a_null_pointer()
    {
        Assert.Null(NativeStrings.Read(null));
    }

    [Fact]
    public void Free_accepts_null()
    {
        NativeStrings.Free(null);
    }
}
