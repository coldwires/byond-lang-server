using System.Text;
using Dm.Core.Services;

namespace Dm.Native;

/// <summary>Serialises inlay hints for the ABI.</summary>
/// <remarks>
/// Hand-written like every other writer here: this assembly is a NativeAOT target, so a
/// reflection-based serializer is not available. The string escaping is shared with
/// <see cref="SymbolJson"/>.
/// </remarks>
internal static class InlayHintJson
{
    public static string Write(System.Collections.Generic.IReadOnlyList<InlayHint> hints)
    {
        StringBuilder json = new();
        json.Append("{\"hints\":[");

        for (int i = 0; i < hints.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            InlayHint hint = hints[i];

            json.Append("{\"line\":").Append(hint.Position.Line);
            json.Append(",\"char\":").Append(hint.Position.Character);
            json.Append(",\"label\":");
            SymbolJson.AppendString(json, hint.Label);

            // A word rather than a number, same as diagnostic severity: shipping the enum's
            // integer invites a client to decode it with a different table.
            json.Append(",\"kind\":\"").Append(hint.Kind == InlayHintKind.Type ? "type" : "unknown").Append('"');
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }
}
