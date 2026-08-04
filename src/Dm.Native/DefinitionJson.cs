using System.Collections.Generic;
using System.Text;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>Serialises go-to-definition results for the ABI.</summary>
/// <remarks>
/// Hand-written for the same reason as <see cref="SymbolJson"/>: this assembly is a NativeAOT
/// target, so a reflection-based serializer is not available.
///
/// Positions are converted here rather than in the service, because line and column depend on the
/// encoding the client asked for and on the text of the file the definition <i>lands in</i> — which
/// is often not the file the query came from.
/// </remarks>
internal static class DefinitionJson
{
    public static string Write(
        Workspace workspace,
        IReadOnlyList<DefinitionLocation> locations,
        PositionEncoding encoding)
    {
        StringBuilder json = new();
        json.Append("{\"definitions\":[");

        bool first = true;

        foreach (DefinitionLocation location in locations)
        {
            // A definition in a file we cannot read is not worth reporting: the client could not
            // navigate to it anyway, and a location with no line numbers is worse than none.
            if (!workspace.TryGetDocument(location.File, out Document document))
                continue;

            SourceText text = document.Text;

            LinePosition start = text.GetLinePosition(location.Span.Start, encoding);
            LinePosition end = text.GetLinePosition(location.Span.End, encoding);
            LinePosition nameStart = text.GetLinePosition(location.NameSpan.Start, encoding);
            LinePosition nameEnd = text.GetLinePosition(location.NameSpan.End, encoding);

            if (!first)
                json.Append(',');

            first = false;

            json.Append("{\"file\":");
            SymbolJson.AppendString(json, location.File);
            json.Append(",\"detail\":");
            SymbolJson.AppendString(json, location.Detail);
            json.Append(",\"startLine\":").Append(start.Line);
            json.Append(",\"startChar\":").Append(start.Character);
            json.Append(",\"endLine\":").Append(end.Line);
            json.Append(",\"endChar\":").Append(end.Character);
            json.Append(",\"selStartLine\":").Append(nameStart.Line);
            json.Append(",\"selStartChar\":").Append(nameStart.Character);
            json.Append(",\"selEndLine\":").Append(nameEnd.Line);
            json.Append(",\"selEndChar\":").Append(nameEnd.Character);
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }
}
