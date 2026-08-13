using System.Text;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>
/// Serialises a rename answer for <c>dm_rename_at</c>. Spans use the same
/// <c>startLine/startChar/endLine/endChar</c> spelling as the reference query, in the encoding the
/// caller asked for.
/// </summary>
internal static class RenameJson
{
    public static string Write(Workspace workspace, RenameResult result, PositionEncoding encoding)
    {
        StringBuilder json = new();

        json.Append("{\"refusal\":");
        SymbolJson.AppendString(json, result.Refusal switch
        {
            RenameRefusal.None => "none",
            RenameRefusal.NothingAtPosition => "nothingAtPosition",
            RenameRefusal.Builtin => "builtin",
            RenameRefusal.Type => "type",
            _ => "invalidName",
        });
        json.Append(",\"target\":");
        SymbolJson.AppendString(json, result.Target);
        json.Append(",\"newName\":");
        SymbolJson.AppendString(json, result.NewName);
        json.Append(",\"edits\":[");

        for (int i = 0; i < result.Edits.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            RenameEdit edit = result.Edits[i];
            json.Append("{\"file\":");
            SymbolJson.AppendString(json, edit.File);
            AppendSpan(json, workspace, edit.File, edit.Span, encoding);
            json.Append('}');
        }

        json.Append("],\"uncertain\":[");

        for (int i = 0; i < result.Uncertain.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            UncertainSite site = result.Uncertain[i];
            json.Append("{\"file\":");
            SymbolJson.AppendString(json, site.File);
            json.Append(",\"reason\":");
            SymbolJson.AppendString(json, site.Reason switch
            {
                UncertainReason.ColonAccess => "colonAccess",
                UncertainReason.UntypedReceiver => "untypedReceiver",
                _ => "stringLiteral",
            });
            AppendSpan(json, workspace, site.File, site.Span, encoding);
            json.Append('}');
        }

        json.Append("]}");
        return json.ToString();
    }

    private static void AppendSpan(
        StringBuilder json, Workspace workspace, string file, TextSpan span, PositionEncoding encoding)
    {
        if (workspace.GetFileText(file) is not { } text)
            return;

        LinePosition start = text.GetLinePosition(span.Start, encoding);
        LinePosition end = text.GetLinePosition(span.End, encoding);

        json.Append(",\"startLine\":").Append(start.Line);
        json.Append(",\"startChar\":").Append(start.Character);
        json.Append(",\"endLine\":").Append(end.Line);
        json.Append(",\"endChar\":").Append(end.Character);
    }
}
