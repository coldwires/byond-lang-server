using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dm.Assets;

namespace Dm.Native;

/// <summary>
/// Serialises a <c>.dmi</c>'s icon states for the ABI.
/// </summary>
/// <remarks>
/// Hand-written like every other writer here, because this assembly is a NativeAOT target.
/// </remarks>
internal static class IconJson
{
    /// <summary>
    /// Writes the states, or the fact that the file is not an icon.
    /// </summary>
    /// <remarks>
    /// <c>isDmi</c> is the whole reason this is not just an array. A zero-byte <c>.dmi</c> and a
    /// plain PNG saved under that extension both exist in real projects, and "no states" is a
    /// different thing for a client to draw than "not an icon" — one is an empty icon, the other
    /// is a broken asset worth telling the user about.
    /// </remarks>
    public static string Write(bool isDmi, DmiIcon icon)
    {
        StringBuilder json = new();

        json.Append("{\"isDmi\":").Append(isDmi ? "true" : "false");
        json.Append(",\"width\":").Append(icon.Width);
        json.Append(",\"height\":").Append(icon.Height);
        json.Append(",\"states\":[");

        for (int i = 0; i < icon.States.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            DmiState state = icon.States[i];

            json.Append("{\"name\":");
            SymbolJson.AppendString(json, state.Name);
            json.Append(",\"dirs\":").Append(state.Dirs);
            json.Append(",\"frames\":").Append(state.Frames);
            json.Append(",\"movement\":").Append(state.IsMovement ? "true" : "false");
            json.Append(",\"rewind\":").Append(state.Rewind ? "true" : "false");
            json.Append(",\"loop\":").Append(state.Loop);

            AppendNumbers(json, ",\"delays\":", state.Delays);
            AppendIntegers(json, ",\"hotspot\":", state.Hotspot);

            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private static void AppendNumbers(StringBuilder json, string key, IReadOnlyList<double> values)
    {
        json.Append(key).Append('[');

        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append(values[i].ToString("0.###", CultureInfo.InvariantCulture));
        }

        json.Append(']');
    }

    private static void AppendIntegers(StringBuilder json, string key, IReadOnlyList<int> values)
    {
        json.Append(key).Append('[');

        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            json.Append(values[i]);
        }

        json.Append(']');
    }
}
