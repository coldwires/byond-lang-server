using System.Text;
using Dm.Core.Services;

namespace Dm.Native;

/// <summary>Serialises a signature-help result for the ABI.</summary>
/// <remarks>
/// Hand-written for the same reason as <see cref="SymbolJson"/>: this assembly is a NativeAOT
/// target, so a reflection-based serializer is not available.
/// </remarks>
internal static class SignatureJson
{
    /// <summary>Writes the result, or an empty object when no call encloses the position.</summary>
    public static string Write(SignatureHelpResult? help)
    {
        if (help is null)
            return "{}";

        StringBuilder json = new();

        json.Append("{\"detail\":");
        SymbolJson.AppendString(json, help.Detail);
        json.Append(",\"name\":");
        SymbolJson.AppendString(json, help.Name);
        json.Append(",\"label\":");
        SymbolJson.AppendString(json, help.Label);
        json.Append(",\"parameters\":[");

        for (int i = 0; i < help.Parameters.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            SymbolJson.AppendString(json, help.Parameters[i]);
        }

        json.Append("],\"activeParameter\":").Append(help.ActiveParameter);
        json.Append('}');

        return json.ToString();
    }
}
