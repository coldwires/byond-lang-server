using System.Text;
using Dm.Core.Text;

namespace Dm.Core.Tests.Text;

public class SourceFileReaderTests
{
    [Fact]
    public void Reads_plain_utf8()
    {
        string decoded = SourceFileReader.Decode(Encoding.UTF8.GetBytes("/mob/a\n"), out SourceEncoding encoding);

        Assert.Equal("/mob/a\n", decoded);
        Assert.Equal(SourceEncoding.Utf8, encoding);
    }

    [Fact]
    public void Strips_a_utf8_bom()
    {
        byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble();
        byte[] body = Encoding.UTF8.GetBytes("/mob/a");
        byte[] all = new byte[bytes.Length + body.Length];
        bytes.CopyTo(all, 0);
        body.CopyTo(all, bytes.Length);

        string decoded = SourceFileReader.Decode(all, out SourceEncoding encoding);

        Assert.Equal("/mob/a", decoded);
        Assert.Equal(SourceEncoding.Utf8Bom, encoding);
        Assert.DoesNotContain('﻿', decoded);
    }

    [Fact]
    public void Reads_utf16_with_a_bom()
    {
        byte[] bytes = new byte[] { 0xFF, 0xFE };
        byte[] body = Encoding.Unicode.GetBytes("/mob/a");
        byte[] all = new byte[bytes.Length + body.Length];
        bytes.CopyTo(all, 0);
        body.CopyTo(all, bytes.Length);

        Assert.Equal("/mob/a", SourceFileReader.Decode(all, out SourceEncoding encoding));
        Assert.Equal(SourceEncoding.Utf16Le, encoding);
    }

    /// <summary>
    /// Old DM predates UTF-8 being universal. Decoding a Windows-1252 file as UTF-8 turns every
    /// high byte into U+FFFD, which then lexes as an unrecognised character.
    /// </summary>
    [Fact]
    public void Falls_back_to_windows_1252_for_invalid_utf8()
    {
        // 0x95 is a bullet in Windows-1252 and an invalid lead byte in UTF-8.
        byte[] bytes = { (byte)'a', 0x95, (byte)'b' };

        string decoded = SourceFileReader.Decode(bytes, out SourceEncoding encoding);

        Assert.Equal(SourceEncoding.Windows1252, encoding);
        Assert.Equal("a•b", decoded);
        Assert.DoesNotContain('�', decoded);
    }

    [Theory]
    [InlineData(0x91, '‘')]  // left single quote
    [InlineData(0x92, '’')]  // right single quote, the one in "don't"
    [InlineData(0x93, '“')]  // left double quote
    [InlineData(0x94, '”')]  // right double quote
    [InlineData(0x96, '–')]  // en dash
    [InlineData(0x97, '—')]  // em dash
    public void Maps_the_windows_1252_punctuation_range(int raw, char expected)
    {
        // This range is the whole reason for not simply using Latin-1, which leaves these as
        // unused control codes.
        string decoded = SourceFileReader.Decode(new[] { (byte)raw }, out _);

        Assert.Equal(expected, decoded[0]);
    }

    [Fact]
    public void Ascii_is_identical_under_both_paths()
    {
        byte[] bytes = Encoding.ASCII.GetBytes("/mob/test\n\tvar/hp = 1\n");

        Assert.Equal("/mob/test\n\tvar/hp = 1\n", SourceFileReader.Decode(bytes, out SourceEncoding encoding));
        Assert.Equal(SourceEncoding.Utf8, encoding);
    }

    [Fact]
    public void Reads_a_file_from_disk()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/mob/a\n");

        SourceText text = SourceFileReader.Read(path);

        Assert.Equal("/mob/a\n", text.Content);
        Assert.Equal(path, text.Path);
    }

    // -- writing it back ---------------------------------------------------

    /// <summary>
    /// Read, change nothing, write: the bytes must be the ones that were there.
    /// </summary>
    /// <remarks>
    /// <b>Written after `dmc format --write` failed exactly this on a real game.</b> mlaas has one
    /// Windows-1252 file — an NPC named <c>Pärt</c> — and writing UTF-8 back over it converted the
    /// file. `dm.exe` then compiled the converted bytes with 0 errors, so nothing anywhere reported
    /// that a name in the running game had changed. The encoding a file is in is not the writer's
    /// to decide.
    /// </remarks>
    /// <remarks>
    /// The parameter is <c>object</c> and cast in the body because <c>SourceEncoding</c> is
    /// internal: a public test method cannot name it, and making the class internal would leave
    /// xunit silently not running it.
    /// </remarks>
    [Theory]
    [InlineData(SourceEncoding.Utf8)]
    [InlineData(SourceEncoding.Utf8Bom)]
    [InlineData(SourceEncoding.Utf16Le)]
    [InlineData(SourceEncoding.Utf16Be)]
    [InlineData(SourceEncoding.Windows1252)]
    public void A_write_of_unchanged_text_reproduces_the_file_byte_for_byte(object encodingValue)
    {
        SourceEncoding encoding = (SourceEncoding)encodingValue;

        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "npc.dm");

        // Every case carries the same non-ASCII character, which is the only thing that can tell
        // these encodings apart on disk.
        byte[] original = BytesFor(encoding, "/mob/npc\n\tname = \"Pärt\"\n");
        File.WriteAllBytes(path, original);

        SourceText text = SourceFileReader.Read(path, out SourceEncoding detected);
        Assert.Equal(encoding, detected);

        SourceFileReader.Write(path, text.Content, detected);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    /// <summary>An edit changes the edited bytes and nothing about the encoding.</summary>
    [Fact]
    public void A_write_after_an_edit_stays_in_the_files_own_encoding()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "npc.dm");

        File.WriteAllBytes(path, BytesFor(SourceEncoding.Windows1252, "/mob/npc\n\tname=\"Pärt\"\n"));

        SourceText text = SourceFileReader.Read(path, out SourceEncoding encoding);
        SourceFileReader.Write(path, text.Content.Replace("name=", "name = "), encoding);

        byte[] written = File.ReadAllBytes(path);

        // 0xE4 is `ä` in Windows-1252 and the lead byte of a two-byte sequence in UTF-8, so its
        // presence as a single byte is the whole assertion.
        Assert.Contains((byte)0xE4, written);
        Assert.Equal("/mob/npc\n\tname = \"Pärt\"\n", SourceFileReader.Decode(written, out SourceEncoding after));
        Assert.Equal(SourceEncoding.Windows1252, after);
    }

    /// <summary>
    /// A character the file's encoding cannot hold is a refusal rather than a replacement
    /// character written into somebody's source.
    /// </summary>
    [Fact]
    public void A_write_refuses_a_character_the_encoding_cannot_hold()
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "npc.dm");

        File.WriteAllBytes(path, BytesFor(SourceEncoding.Windows1252, "/mob/npc\n"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => SourceFileReader.Write(path, "/mob/npc\n\tname = \"日\"\n", SourceEncoding.Windows1252));

        Assert.Contains("Windows-1252", error.Message);
        Assert.Equal("/mob/npc\n", SourceFileReader.Read(path).Content);
    }

    /// <summary>Line endings are part of the text, so a write returns what was read.</summary>
    [Theory]
    [InlineData("/mob/a\r\n\tvar/hp = 1\r\n")]
    [InlineData("/mob/a\n\tvar/hp = 1\n")]
    [InlineData("/mob/a\r\tvar/hp = 1\r")]
    public void A_write_does_not_normalise_line_endings(string content)
    {
        using TempDirectory temp = new();
        string path = Path.Combine(temp.Path, "a.dm");
        byte[] original = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, original);

        SourceText text = SourceFileReader.Read(path, out SourceEncoding encoding);
        SourceFileReader.Write(path, text.Content, encoding);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    /// <summary>The bytes a file in this encoding would hold, written the way a real file is.</summary>
    private static byte[] BytesFor(SourceEncoding encoding, string content) => encoding switch
    {
        SourceEncoding.Utf8 => new UTF8Encoding(false).GetBytes(content),
        SourceEncoding.Utf8Bom => new UTF8Encoding(true).GetPreamble()
            .Concat(new UTF8Encoding(false).GetBytes(content)).ToArray(),
        SourceEncoding.Utf16Le => new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes(content)).ToArray(),
        SourceEncoding.Utf16Be => new byte[] { 0xFE, 0xFF }
            .Concat(Encoding.BigEndianUnicode.GetBytes(content)).ToArray(),
        SourceEncoding.Windows1252 => Encoding.Latin1.GetBytes(content),
        _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
    };
}
