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
}
