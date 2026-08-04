using System.Text;
using Dm.Core.Services;

namespace Dm.Native;

/// <summary>Serialises a completion list for the ABI.</summary>
/// <remarks>
/// Hand-written for the same reason as <see cref="SymbolJson"/>: this assembly is a NativeAOT
/// target, so a reflection-based serializer is not available. The string escaping is shared with it.
/// </remarks>
internal static class CompletionJson
{
    public static string Write(CompletionResult result)
    {
        StringBuilder json = new();

        json.Append("{\"context\":\"").Append(result.Context.ToString()).Append("\",\"items\":[");

        for (int i = 0; i < result.Items.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            CompletionItem item = result.Items[i];

            json.Append("{\"name\":");
            SymbolJson.AppendString(json, item.Name);
            json.Append(",\"detail\":");
            SymbolJson.AppendString(json, item.Detail);
            json.Append(",\"kind\":").Append((int)item.Kind);
            json.Append(",\"builtin\":").Append(item.IsBuiltin ? "true" : "false");
            json.Append(",\"documentation\":");
            SymbolJson.AppendString(json, item.Documentation);
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }
}
