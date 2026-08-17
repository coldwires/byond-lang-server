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
internal static class SourceFileReader
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
        => Read(path, out _);

    /// <summary>Reads a file and reports the encoding it turned out to be in.</summary>
    /// <remarks>
    /// The overload exists for anything that intends to write the file back: decoding one encoding
    /// and encoding another is how a tool silently rewrites bytes it was never asked to touch. See
    /// <see cref="Write"/>.
    /// </remarks>
    public static SourceText Read(string path, out SourceEncoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes = File.ReadAllBytes(path);
        return SourceText.From(Decode(bytes, out encoding), path);
    }

    /// <summary>
    /// Writes text back in the encoding it was read in, byte-order mark included.
    /// </summary>
    /// <param name="path">The file to overwrite.</param>
    /// <param name="text">The whole file's new content.</param>
    /// <param name="encoding">What <see cref="Read(string, out SourceEncoding)"/> reported.</param>
    /// <exception cref="InvalidOperationException">
    /// A character cannot be written in <paramref name="encoding"/>. Refusing is the point: the
    /// alternative is a replacement character in somebody's source file.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>This exists because the obvious spelling is wrong.</b> `File.WriteAllText` writes UTF-8,
    /// so reading a Windows-1252 file and writing it back converts it — one real game has exactly
    /// one such file, an NPC named <c>Pärt</c>, and the compiler accepts the converted bytes
    /// without a word while the name in the running game becomes two characters. That is the
    /// round-trip `INTEGRATION.txt` §5 tells clients to get right, and a tool of ours got it wrong
    /// first.
    /// </para>
    /// <para>
    /// Line endings need no handling here and are not normalised anywhere: they are part of the
    /// text, so whatever was read comes back.
    /// </para>
    /// </remarks>
    public static void Write(string path, string text, SourceEncoding encoding)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        byte[] bytes = encoding switch
        {
            SourceEncoding.Utf8 => new UTF8Encoding(false).GetBytes(text),
            SourceEncoding.Utf8Bom => Prefixed(new byte[] { 0xEF, 0xBB, 0xBF }, new UTF8Encoding(false).GetBytes(text)),
            SourceEncoding.Utf16Le => Prefixed(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(text)),
            SourceEncoding.Utf16Be => Prefixed(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(text)),
            SourceEncoding.Windows1252 => EncodeWindows1252(text, path),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
        };

        File.WriteAllBytes(path, bytes);
    }

    private static byte[] Prefixed(byte[] mark, byte[] body)
    {
        byte[] all = new byte[mark.Length + body.Length];
        mark.CopyTo(all, 0);
        body.CopyTo(all, mark.Length);
        return all;
    }

    /// <summary>The inverse of <see cref="DecodeWindows1252"/>, refusing what it cannot represent.</summary>
    private static byte[] EncodeWindows1252(string text, string path)
    {
        byte[] bytes = new byte[text.Length];

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (c <= 0x7F || (c >= 0xA0 && c <= 0xFF))
            {
                bytes[i] = (byte)c;
                continue;
            }

            int punctuation = Array.IndexOf(Windows1252Punctuation, c);

            if (punctuation >= 0)
            {
                bytes[i] = (byte)(0x80 + punctuation);
                continue;
            }

            throw new InvalidOperationException(
                $"{path} is Windows-1252 and cannot hold U+{(int)c:X4}; refusing to write rather than replace it.");
        }

        return bytes;
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

internal enum SourceEncoding
{
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
    Windows1252,
}
