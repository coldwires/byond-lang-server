using System.Collections.Generic;
using System.Text;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>Serialises workspace symbol hits for the ABI.</summary>
/// <remarks>
/// Positions are converted here because each hit lands in its own file, and line and column depend
/// on that file's text as well as on the encoding the caller asked for.
/// </remarks>
internal static class WorkspaceSymbolJson
{
    public static string Write(
        Workspace workspace, IReadOnlyList<WorkspaceSymbol> hits, PositionEncoding encoding)
    {
        StringBuilder json = new();
        json.Append("{\"symbols\":[");

        bool first = true;

        foreach (WorkspaceSymbol hit in hits)
        {
            // A hit in a file we cannot read cannot be navigated to, so it is not worth offering.
            if (!workspace.TryGetDocument(hit.File, out Document document))
                continue;

            SourceText text = document.Text;

            LinePosition start = text.GetLinePosition(hit.Span.Start, encoding);
            LinePosition end = text.GetLinePosition(hit.Span.End, encoding);
            LinePosition nameStart = text.GetLinePosition(hit.NameSpan.Start, encoding);
            LinePosition nameEnd = text.GetLinePosition(hit.NameSpan.End, encoding);

            if (!first)
                json.Append(',');

            first = false;

            json.Append("{\"name\":");
            SymbolJson.AppendString(json, hit.Name);
            json.Append(",\"detail\":");
            SymbolJson.AppendString(json, hit.Detail);
            json.Append(",\"kind\":").Append((int)hit.Kind);
            json.Append(",\"file\":");
            SymbolJson.AppendString(json, hit.File);
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
