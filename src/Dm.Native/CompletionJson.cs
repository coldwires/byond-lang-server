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

        json.Append("{\"context\":\"").Append(result.Context.ToString()).Append('"');

        // Reported rather than inferred, as subtypesOf and references do — and here it also says
        // whether filtering the list locally is still safe.
        json.Append(",\"truncated\":").Append(result.Truncated ? "true" : "false");
        json.Append(",\"items\":[");

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
            json.Append(",\"inferred\":").Append(item.Inferred ? "true" : "false");
            json.Append(",\"documentation\":");
            SymbolJson.AppendString(json, item.Documentation);
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    /// <summary>One resolved item's documentation, as its own document.</summary>
    /// <remarks>
    /// An object rather than a bare string so the shape can gain fields — the same reason every
    /// other response here is one. Empty documentation is a normal answer, not a failure.
    /// </remarks>
    public static string WriteDocumentation(string documentation)
    {
        StringBuilder json = new();
        json.Append("{\"documentation\":");
        SymbolJson.AppendString(json, documentation);
        json.Append('}');
        return json.ToString();
    }
}
