using System;
using System.IO;
using System.Text;

namespace Dm.Core.Text;

/// <summary>
/// Reads a source file, detecting its encoding.
/// </summary>
/// <remarks>
/// DM predates any convention of UTF-8 everywhere. Files written in the 2000s are commonly
/// Windows-1252, and decoding one as UTF-8 turns every byte in the 0x80–0xFF range into U+FFFD —
/// which then lexes as an unrecognised character. Real archives contain both.
///
/// Detection order: byte-order mark, then a strict UTF-8 decode, then Windows-1252. Strict UTF-8
/// is a reliable discriminator because the multi-byte sequence rules are restrictive enough that
/// Windows-1252 text almost never forms valid UTF-8 by accident.
/// </remarks>
public static class SourceFileReader
{
    /// <summary>
    /// Windows-1252 differs from Latin-1 only in 0x80–0x9F, where Latin-1 has unused control codes
    /// and Windows-1252 has punctuation. That range is exactly what appears in real files — curly
    /// quotes, em dashes, bullets — so mapping it is what makes the fallback correct rather than
    /// merely non-throwing. Encoded as a table so no encoding-provider package is needed, which
    /// keeps Dm.Core dependency-free and AOT-clean.
    /// </summary>
    private static readonly char[] Windows1252Punctuation =
    {
        '€', '', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '', 'Ž', '',
        '', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '', 'ž', 'Ÿ',
    };

    public static SourceText Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes = File.ReadAllBytes(path);
        return SourceText.From(Decode(bytes, out _), path);
    }

    /// <summary>Decodes bytes and reports which encoding was used.</summary>
    public static string Decode(byte[] bytes, out SourceEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
        {
            encoding = SourceEncoding.Utf8Bom;
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (StartsWith(bytes, 0xFF, 0xFE))
        {
            encoding = SourceEncoding.Utf16Le;
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (StartsWith(bytes, 0xFE, 0xFF))
        {
            encoding = SourceEncoding.Utf16Be;
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            string utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);

            encoding = SourceEncoding.Utf8;
            return utf8;
        }
        catch (DecoderFallbackException)
        {
            encoding = SourceEncoding.Windows1252;
            return DecodeWindows1252(bytes);
        }
    }

    private static string DecodeWindows1252(byte[] bytes)
    {
        char[] chars = new char[bytes.Length];

        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F
                ? Windows1252Punctuation[b - 0x80]
                : (char)b;
        }

        return new string(chars);
    }

    private static bool StartsWith(byte[] bytes, params byte[] prefix)
    {
        if (bytes.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (bytes[i] != prefix[i])
                return false;
        }

        return true;
    }
}

public enum SourceEncoding
{
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Windows1252,
}
