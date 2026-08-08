using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Dm.Assets;

/// <summary>One icon state inside a <c>.dmi</c>.</summary>
public sealed class DmiState
{
    public DmiState(
        string name,
        int dirs,
        int frames,
        IReadOnlyList<double> delays,
        bool isMovement,
        bool rewind,
        int loop,
        IReadOnlyList<int> hotspot)
    {
        Name = name;
        Dirs = dirs;
        Frames = frames;
        Delays = delays;
        IsMovement = isMovement;
        Rewind = rewind;
        Loop = loop;
        Hotspot = hotspot;
    }

    /// <summary>
    /// The state's name, unescaped. Empty is the DEFAULT state and is entirely ordinary — 226 of
    /// 352 real icons carry one.
    /// </summary>
    public string Name { get; }

    /// <summary>1, 4 or 8 — how many facings the state draws.</summary>
    public int Dirs { get; }

    public int Frames { get; }

    /// <summary>Per-frame delays in ticks, empty for a single-frame state.</summary>
    public IReadOnlyList<double> Delays { get; }

    /// <summary>
    /// The movement variant of a state, which is why a name is not a key.
    /// </summary>
    /// <remarks>
    /// DM lets one name appear twice, once still and once with <c>movement = 1</c>, and picks
    /// between them by whether the atom is moving. 34 of 352 real icons do this, so a dictionary
    /// keyed by name silently loses half of those states.
    /// </remarks>
    public bool IsMovement { get; }

    /// <summary>Play forwards then backwards rather than looping round.</summary>
    public bool Rewind { get; }

    /// <summary>How many times an animation repeats; 0 means forever.</summary>
    public int Loop { get; }

    /// <summary>
    /// <c>x, y, frame</c> for a cursor icon's click point, empty when none was set.
    /// </summary>
    public IReadOnlyList<int> Hotspot { get; }

    public override string ToString() =>
        $"\"{Name}\" dirs={Dirs} frames={Frames}{(IsMovement ? " movement" : string.Empty)}";
}

/// <summary>The states in one <c>.dmi</c>, and the icon's cell size.</summary>
public sealed class DmiIcon
{
    public DmiIcon(int width, int height, IReadOnlyList<DmiState> states)
    {
        Width = width;
        Height = height;
        States = states;
    }

    /// <summary>Cell width in pixels, 0 when the file did not say.</summary>
    /// <remarks>
    /// 219 of 352 real icons carry <c>width</c>/<c>height</c> in the header and the rest leave it
    /// implicit, so 0 means "not stated" rather than "zero-sized". BYOND's default is 32x32; that
    /// default is not applied here, because a reader inventing it would be indistinguishable from
    /// a file that stated it.
    /// </remarks>
    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<DmiState> States { get; }
}

/// <summary>
/// Reads the icon states out of a <c>.dmi</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.dmi</c> is a PNG carrying a <c>zTXt</c> chunk whose keyword is <c>Description</c>. Inflated,
/// it is a plain text block that Dream Maker writes:
/// </para>
/// <code>
/// # BEGIN DMI
/// version = 4.0
/// width = 32
/// height = 32
/// state = "door_opening"
///     dirs = 1
///     frames = 6
///     delay = 2,2,2,2,2,2
///     loop = 1
/// # END DMI
/// </code>
/// <para>
/// Built against 352 real icons from three games rather than against the format description, which
/// is what turned up the cases below. <b>Nothing here throws on a malformed file</b>: three of
/// those 352 are zero bytes and one is a plain PNG saved with a <c>.dmi</c> extension, so a reader
/// that treats "not an icon" as an error would fail on a real project's own assets.
/// </para>
/// </remarks>
public static class DmiReader
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Reads a file from disk. False when it is not a DM icon.</summary>
    public static bool TryRead(string path, out DmiIcon icon)
    {
        ArgumentNullException.ThrowIfNull(path);

        icon = Empty;

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return false;
        }

        return TryRead(bytes, out icon);
    }

    /// <summary>
    /// Reads icon bytes already in hand. False when they are not a DM icon.
    /// </summary>
    /// <remarks>
    /// False covers every way a file can fail to be one — empty, not a PNG, a PNG with no
    /// <c>Description</c> chunk, a chunk that will not inflate, or text with no <c>BEGIN DMI</c>
    /// header. A client wanting to tell "no states" from "not an icon" has the return value; the
    /// distinction matters because a plain PNG named <c>.dmi</c> is a thing that happens.
    /// </remarks>
    public static bool TryRead(ReadOnlySpan<byte> bytes, out DmiIcon icon)
    {
        icon = Empty;

        if (!TryFindDescription(bytes, out string? description) || description is null)
            return false;

        return TryParse(description, out icon);
    }

    private static DmiIcon Empty { get; } = new(0, 0, Array.Empty<DmiState>());

    /// <summary>Walks the PNG chunks for the <c>zTXt</c> Dream Maker writes.</summary>
    private static bool TryFindDescription(ReadOnlySpan<byte> bytes, out string? description)
    {
        description = null;

        if (bytes.Length < 8 || !bytes[..8].SequenceEqual(PngSignature))
            return false;

        int at = 8;

        while (at + 8 <= bytes.Length)
        {
            int length = ReadInt32BigEndian(bytes[at..]);

            // A length that overruns the buffer is a truncated file, not a chunk.
            if (length < 0 || at + 12 + length > bytes.Length)
                return false;

            ReadOnlySpan<byte> kind = bytes.Slice(at + 4, 4);
            ReadOnlySpan<byte> body = bytes.Slice(at + 8, length);

            // zTXt is what Dream Maker writes. tEXt is read too: it is the same keyword and payload
            // uncompressed, and costs one branch to accept.
            bool compressed = kind.SequenceEqual("zTXt"u8);

            if (compressed || kind.SequenceEqual("tEXt"u8))
            {
                int nul = body.IndexOf((byte)0);

                if (nul > 0 && body[..nul].SequenceEqual("Description"u8))
                {
                    // zTXt puts a compression-method byte between the NUL and the deflate stream.
                    ReadOnlySpan<byte> payload = compressed ? body[(nul + 2)..] : body[(nul + 1)..];

                    return compressed
                        ? TryInflate(payload, out description)
                        : Latin1(payload, out description);
                }
            }

            if (kind.SequenceEqual("IDAT"u8))
                return false;   // metadata precedes the image data; past it there is none.

            at += 12 + length;
        }

        return false;
    }

    private static bool TryInflate(ReadOnlySpan<byte> payload, out string? text)
    {
        text = null;

        try
        {
            using MemoryStream input = new(payload.ToArray());
            using ZLibStream inflate = new(input, CompressionMode.Decompress);
            using MemoryStream output = new();

            inflate.CopyTo(output);

            return Latin1(output.ToArray(), out text);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// PNG text chunks are Latin-1 by the spec, so decode as that rather than as UTF-8.
    /// </summary>
    private static bool Latin1(ReadOnlySpan<byte> bytes, out string? text)
    {
        text = Encoding.Latin1.GetString(bytes);
        return true;
    }

    private static int ReadInt32BigEndian(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    /// <summary>Parses the inflated metadata block.</summary>
    private static bool TryParse(string description, out DmiIcon icon)
    {
        icon = Empty;

        if (!description.Contains("# BEGIN DMI", StringComparison.Ordinal))
            return false;

        int width = 0;
        int height = 0;
        List<DmiState> states = new();

        string? name = null;
        int dirs = 1;
        int frames = 1;
        List<double> delays = new();
        bool movement = false;
        bool rewind = false;
        int loop = 0;
        List<int> hotspot = new();

        void Flush()
        {
            if (name is null)
                return;

            states.Add(new DmiState(
                name, dirs, frames, delays.ToArray(), movement, rewind, loop, hotspot.ToArray()));

            dirs = 1;
            frames = 1;
            delays = new List<double>();
            movement = false;
            rewind = false;
            loop = 0;
            hotspot = new List<int>();
        }

        foreach (string raw in description.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line[0] == '#')
                continue;

            int equals = line.IndexOf('=');

            if (equals < 0)
                continue;

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            switch (key)
            {
                case "state":
                    Flush();
                    name = Unquote(value);
                    break;

                case "width":
                    width = ParseInt(value, width);
                    break;

                case "height":
                    height = ParseInt(value, height);
                    break;

                case "dirs":
                    dirs = ParseInt(value, 1);
                    break;

                case "frames":
                    frames = ParseInt(value, 1);
                    break;

                case "delay":
                    foreach (string part in value.Split(','))
                    {
                        if (double.TryParse(
                                part.Trim(),
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double delay))
                        {
                            delays.Add(delay);
                        }
                    }

                    break;

                case "movement":
                    movement = ParseInt(value, 0) != 0;
                    break;

                case "rewind":
                    rewind = ParseInt(value, 0) != 0;
                    break;

                case "loop":
                    loop = ParseInt(value, 0);
                    break;

                case "hotspot":
                    foreach (string part in value.Split(','))
                    {
                        if (int.TryParse(part.Trim(), out int component))
                            hotspot.Add(component);
                    }

                    break;
            }
        }

        Flush();

        icon = new DmiIcon(width, height, states);
        return true;
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out int parsed) ? parsed : fallback;

    /// <summary>
    /// Strips the quotes around a state name and undoes its backslash escapes.
    /// </summary>
    /// <remarks>
    /// Real icons need this: a font icon in one of the survey projects declares states named
    /// <c>\\</c> and <c>\"</c>, so a reader taking the text between the first and last quote
    /// returns a name no lookup will ever match.
    /// </remarks>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;

        string inner = value[1..^1];

        if (!inner.Contains('\\', StringComparison.Ordinal))
            return inner;

        StringBuilder name = new(inner.Length);

        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
                i++;

            name.Append(inner[i]);
        }

        return name.ToString();
    }
}
