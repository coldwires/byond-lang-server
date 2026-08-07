using System.Collections.Generic;
using System.Text;
using Dm.Core.Includes;
using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>
/// Serialises the editor-shaped answers — folding ranges and document links.
/// </summary>
/// <remarks>
/// Hand-written like every other writer here, because this assembly is a NativeAOT target and a
/// reflection-based serializer is not available. String escaping is shared with
/// <see cref="SymbolJson"/>.
/// </remarks>
internal static class EditorJson
{
    public static string WriteFolding(IReadOnlyList<FoldingRange> ranges)
    {
        StringBuilder json = new();
        json.Append("{\"ranges\":[");

        for (int i = 0; i < ranges.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            FoldingRange range = ranges[i];

            json.Append("{\"startLine\":").Append(range.StartLine);
            json.Append(",\"endLine\":").Append(range.EndLine);

            // A word rather than the enum's integer, the same choice diagnostics severity made:
            // shipping the number invites a client to decode it with a different table.
            json.Append(",\"kind\":\"").Append(range.Kind == FoldKind.Comment ? "comment" : "region");
            json.Append("\"}");
        }

        json.Append("]}");
        return json.ToString();
    }

    /// <summary>
    /// A tickmark edit, or the reason there is none.
    /// </summary>
    /// <remarks>
    /// Always an object with a <c>refusal</c> word, so "no edit" is an answer with a cause rather
    /// than an empty response a client has to guess at. The word rather than a number, as
    /// diagnostic severity does.
    /// </remarks>
    public static string WriteDmeEdit(DmeEdit? edit, DmeEditRefusal refusal)
    {
        StringBuilder json = new();
        json.Append("{\"refusal\":\"");

        json.Append(refusal switch
        {
            DmeEditRefusal.None => "none",
            DmeEditRefusal.NoBlock => "noBlock",
            DmeEditRefusal.Conditional => "conditional",
            _ => "noChange",
        });

        json.Append('"');

        if (edit is not null)
        {
            json.Append(",\"start\":").Append(edit.Span.Start);
            json.Append(",\"length\":").Append(edit.Span.Length);
            json.Append(",\"text\":");
            SymbolJson.AppendString(json, edit.Replacement);
        }

        json.Append('}');
        return json.ToString();
    }

    public static string WriteLinks(
        IReadOnlyList<DocumentLink> links, SourceText text, PositionEncoding encoding)
    {
        StringBuilder json = new();
        json.Append("{\"links\":[");

        for (int i = 0; i < links.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            DocumentLink link = links[i];

            LinePosition start = text.GetLinePosition(link.Span.Start, encoding);
            LinePosition end = text.GetLinePosition(link.Span.End, encoding);

            json.Append("{\"startLine\":").Append(start.Line);
            json.Append(",\"startChar\":").Append(start.Character);
            json.Append(",\"endLine\":").Append(end.Line);
            json.Append(",\"endChar\":").Append(end.Character);
            json.Append(",\"target\":");
            SymbolJson.AppendString(json, link.Target);
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }
}
