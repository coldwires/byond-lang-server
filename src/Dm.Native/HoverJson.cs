using System.Text;
using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>Serialises a hover result for the ABI.</summary>
/// <remarks>
/// Hand-written for the same reason as <see cref="SymbolJson"/>: this assembly is a NativeAOT
/// target, so a reflection-based serializer is not available.
/// </remarks>
internal static class HoverJson
{
    /// <summary>Writes the result, or an empty object when nothing resolved.</summary>
    public static string Write(HoverResult? hover, SourceText text, PositionEncoding encoding)
    {
        if (hover is null)
            return "{}";

        LinePosition start = text.GetLinePosition(hover.Span.Start, encoding);
        LinePosition end = text.GetLinePosition(hover.Span.End, encoding);

        StringBuilder json = new();

        json.Append("{\"detail\":");
        SymbolJson.AppendString(json, hover.Detail);
        json.Append(",\"signature\":");
        SymbolJson.AppendString(json, hover.Signature);
        json.Append(",\"documentation\":");
        SymbolJson.AppendString(json, hover.Documentation);
        json.Append(",\"startLine\":").Append(start.Line);
        json.Append(",\"startChar\":").Append(start.Character);
        json.Append(",\"endLine\":").Append(end.Line);
        json.Append(",\"endChar\":").Append(end.Character);
        json.Append('}');

        return json.ToString();
    }
}
