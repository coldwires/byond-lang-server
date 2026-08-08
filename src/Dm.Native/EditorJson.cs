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

    /// <summary>
    /// The colours in a file, with the text to write for each if a picker changes one.
    /// </summary>
    /// <remarks>
    /// Components are 0-255 rather than the 0-1 floats LSP uses, because that is what DM itself
    /// writes and reads and this ABI serves clients that are not speaking LSP. The LSP shell
    /// divides at its own boundary.
    ///
    /// The presentations ride along rather than waiting for a second call: there are at most two,
    /// they are pure arithmetic on a colour already computed, and a round trip per swatch would
    /// cost more than the bytes.
    /// </remarks>
    public static string WriteColors(
        IReadOnlyList<ColorInformation> colors, SourceText text, PositionEncoding encoding)
    {
        StringBuilder json = new();
        json.Append("{\"colors\":[");

        for (int i = 0; i < colors.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            ColorInformation color = colors[i];

            LinePosition start = text.GetLinePosition(color.Span.Start, encoding);
            LinePosition end = text.GetLinePosition(color.Span.End, encoding);

            json.Append("{\"startLine\":").Append(start.Line);
            json.Append(",\"startChar\":").Append(start.Character);
            json.Append(",\"endLine\":").Append(end.Line);
            json.Append(",\"endChar\":").Append(end.Character);
            json.Append(",\"red\":").Append(color.Red);
            json.Append(",\"green\":").Append(color.Green);
            json.Append(",\"blue\":").Append(color.Blue);
            json.Append(",\"alpha\":").Append(color.Alpha);

            // A word, like every other kind on this boundary.
            json.Append(",\"form\":\"").Append(color.Form == ColorForm.RgbCall ? "rgb" : "literal");
            json.Append("\",\"presentations\":[");

            IReadOnlyList<string> presentations = ColorService.PresentationsFor(color);

            for (int p = 0; p < presentations.Count; p++)
            {
                if (p > 0)
                    json.Append(',');

                SymbolJson.AppendString(json, presentations[p]);
            }

            json.Append("]}");
        }

        json.Append("]}");
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
