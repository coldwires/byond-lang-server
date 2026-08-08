using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Dm.Assets;
using Xunit;

namespace Dm.Core.Tests.Assets;

/// <summary>
/// The <c>.dmi</c> reader, against icons built here rather than vendored.
/// </summary>
/// <remarks>
/// A real <c>.dmi</c> is someone's game art, so these assemble the only part that matters — a PNG
/// header, a <c>zTXt</c> chunk holding the metadata block, and an <c>IEND</c>. Every shape asserted
/// below was taken from a survey of 352 real icons across three games; the reader and an
/// independent script agree on all 4,186 states in them.
/// </remarks>
public class DmiReaderTests
{
    /// <summary>Wraps a metadata block in the PNG structure Dream Maker writes.</summary>
    private static byte[] Icon(string description, bool compressed = true)
    {
        using MemoryStream png = new();

        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        using (MemoryStream body = new())
        {
            body.Write(Encoding.ASCII.GetBytes("Description"));
            body.WriteByte(0);

            if (compressed)
            {
                body.WriteByte(0);      // compression method: deflate

                using MemoryStream raw = new(Encoding.Latin1.GetBytes(description));
                using ZLibStream deflate = new(body, CompressionMode.Compress, leaveOpen: true);

                raw.CopyTo(deflate);
            }
            else
            {
                body.Write(Encoding.Latin1.GetBytes(description));
            }

            WriteChunk(png, compressed ? "zTXt" : "tEXt", body.ToArray());
        }

        WriteChunk(png, "IEND", Array.Empty<byte>());

        return png.ToArray();

        static void WriteChunk(Stream png, string kind, byte[] body)
        {
            png.Write(new[]
            {
                (byte)(body.Length >> 24), (byte)(body.Length >> 16),
                (byte)(body.Length >> 8), (byte)body.Length,
            });

            png.Write(Encoding.ASCII.GetBytes(kind));
            png.Write(body);
            png.Write(new byte[4]);     // CRC, which the reader does not check
        }
    }

    private static DmiIcon Read(string description)
    {
        Assert.True(DmiReader.TryRead(Icon(description), out DmiIcon icon));
        return icon;
    }

    private const string Simple =
        "# BEGIN DMI\nversion = 4.0\nwidth = 32\nheight = 32\n" +
        "state = \"door\"\n\tdirs = 4\n\tframes = 1\n" +
        "# END DMI\n";

    [Fact]
    public void A_state_carries_its_name_dirs_and_frames()
    {
        DmiIcon icon = Read(Simple);

        DmiState state = Assert.Single(icon.States);
        Assert.Equal("door", state.Name);
        Assert.Equal(4, state.Dirs);
        Assert.Equal(1, state.Frames);
    }

    [Fact]
    public void The_header_carries_the_cell_size()
    {
        DmiIcon icon = Read(Simple);

        Assert.Equal(32, icon.Width);
        Assert.Equal(32, icon.Height);
    }

    /// <summary>
    /// 133 of 352 real icons state no size. Reporting BYOND's 32x32 default would be
    /// indistinguishable from a file that said so.
    /// </summary>
    [Fact]
    public void A_missing_size_is_zero_rather_than_a_guessed_default()
    {
        DmiIcon icon = Read("# BEGIN DMI\nversion = 4.0\nstate = \"a\"\n\tdirs = 1\n\tframes = 1\n# END DMI\n");

        Assert.Equal(0, icon.Width);
        Assert.Equal(0, icon.Height);
    }

    /// <summary>An empty name is the DEFAULT state, and 226 of 352 real icons have one.</summary>
    [Fact]
    public void An_empty_name_is_the_default_state_and_not_an_error()
    {
        DmiIcon icon = Read("# BEGIN DMI\nversion = 4.0\nstate = \"\"\n\tdirs = 1\n\tframes = 1\n# END DMI\n");

        Assert.Equal(string.Empty, Assert.Single(icon.States).Name);
    }

    /// <summary>
    /// The case that makes a name-keyed dictionary wrong: one name, twice, told apart by
    /// `movement`. 34 of 352 real icons do this.
    /// </summary>
    [Fact]
    public void One_name_may_appear_twice_distinguished_by_movement()
    {
        DmiIcon icon = Read(
            "# BEGIN DMI\nversion = 4.0\n" +
            "state = \"\"\n\tdirs = 4\n\tframes = 1\n" +
            "state = \"\"\n\tdirs = 4\n\tframes = 4\n\tdelay = 2,2,2,2\n\tmovement = 1\n" +
            "# END DMI\n");

        Assert.Equal(2, icon.States.Count);
        Assert.False(icon.States[0].IsMovement);
        Assert.True(icon.States[1].IsMovement);
        Assert.Equal(4, icon.States[1].Frames);
    }

    [Fact]
    public void Delays_are_read_per_frame()
    {
        DmiIcon icon = Read(
            "# BEGIN DMI\nversion = 4.0\nstate = \"spark\"\n\tdirs = 1\n\tframes = 3\n" +
            "\tdelay = 1,2.5,1\n# END DMI\n");

        Assert.Equal(new double[] { 1, 2.5, 1 }, Assert.Single(icon.States).Delays);
    }

    [Fact]
    public void Loop_rewind_and_hotspot_are_read()
    {
        DmiIcon icon = Read(
            "# BEGIN DMI\nversion = 4.0\nstate = \"door\"\n\tdirs = 1\n\tframes = 2\n" +
            "\tdelay = 2,2\n\tloop = 3\n\trewind = 1\n\thotspot = 13,1,1\n# END DMI\n");

        DmiState state = Assert.Single(icon.States);
        Assert.Equal(3, state.Loop);
        Assert.True(state.Rewind);
        Assert.Equal(new[] { 13, 1, 1 }, state.Hotspot);
    }

    /// <summary>
    /// A font icon in one of the survey projects declares states named <c>\</c> and <c>"</c>.
    /// Taking the text between the first and last quote returns a name nothing will match.
    /// </summary>
    [Fact]
    public void An_escaped_name_is_unescaped()
    {
        DmiIcon icon = Read(
            "# BEGIN DMI\nversion = 4.0\n" +
            "state = \"\\\\\"\n\tdirs = 1\n\tframes = 1\n" +
            "state = \"\\\"\"\n\tdirs = 1\n\tframes = 1\n" +
            "# END DMI\n");

        Assert.Equal(2, icon.States.Count);
        Assert.Equal("\\", icon.States[0].Name);
        Assert.Equal("\"", icon.States[1].Name);
    }

    /// <summary>Dream Maker writes zTXt; tEXt is the same payload uncompressed and costs a branch.</summary>
    [Fact]
    public void An_uncompressed_text_chunk_is_read_too()
    {
        Assert.True(DmiReader.TryRead(Icon(Simple, compressed: false), out DmiIcon icon));
        Assert.Equal("door", Assert.Single(icon.States).Name);
    }

    /// <summary>
    /// Three of one project's own 166 .dmi files are ZERO BYTES. A reader that throws on those
    /// fails on a real game's assets, so "not an icon" is a return value rather than an exception.
    /// </summary>
    [Fact]
    public void An_empty_file_is_not_an_icon_and_does_not_throw()
    {
        Assert.False(DmiReader.TryRead(Array.Empty<byte>(), out DmiIcon icon));
        Assert.Empty(icon.States);
    }

    [Fact]
    public void Bytes_that_are_not_a_png_are_not_an_icon()
    {
        Assert.False(DmiReader.TryRead(Encoding.ASCII.GetBytes("GIF89a and then some"), out _));
    }

    /// <summary>
    /// A plain PNG saved with a .dmi extension - one exists in the survey - has no metadata and is
    /// reported as not an icon rather than as an icon with no states.
    /// </summary>
    [Fact]
    public void A_png_with_no_description_chunk_is_not_an_icon()
    {
        byte[] png = Icon(Simple);

        // Rename the chunk in place, leaving a structurally valid PNG with no metadata.
        int at = IndexOf(png, Encoding.ASCII.GetBytes("zTXt"));
        Encoding.ASCII.GetBytes("pHYs").CopyTo(png, at);

        Assert.False(DmiReader.TryRead(png, out _));

        static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= haystack.Length; i++)
            {
                bool hit = true;

                for (int j = 0; j < needle.Length && hit; j++)
                    hit = haystack[i + j] == needle[j];

                if (hit)
                    return i;
            }

            return -1;
        }
    }

    /// <summary>Text that inflates but is not a DMI block is not an icon either.</summary>
    [Fact]
    public void A_description_without_the_begin_marker_is_not_an_icon()
    {
        Assert.False(DmiReader.TryRead(Icon("just a caption someone wrote"), out _));
    }

    /// <summary>A chunk length past the end of the buffer is a truncated file, not a chunk.</summary>
    [Fact]
    public void A_truncated_file_does_not_throw()
    {
        byte[] png = Icon(Simple);

        Assert.False(DmiReader.TryRead(png.AsSpan(0, 20), out _));
    }
}
