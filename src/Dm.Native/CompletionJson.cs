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

            // The item's OWN declared type and initialiser, so a client renders `fatigue - num`
            // without re-parsing the file. Both empty is the ordinary case and is honest: DM has
            // no num or text to name, and most vars carry neither.
            // WHY the receiver's type is what it is, for a client that wants to say more than
            // "inferred". A word, not a number, so it cannot be decoded with the wrong table.
            json.Append(",\"typeFrom\":\"").Append(item.TypeSource switch
            {
                TypeSource.Written => "written",
                TypeSource.Initializer => "initializer",
                TypeSource.Assignment => "assignment",
                TypeSource.InputFilter => "as",
                _ => "none",
            }).Append('"');

            json.Append(",\"type\":");
            SymbolJson.AppendString(json, item.DeclaredType);
            json.Append(",\"value\":");
            SymbolJson.AppendString(json, item.InitialValue);

            // Beside "value", never instead of it: the author's text and what it comes to are
            // different facts and a reader of `= 5 * 60` wants both. Empty unless the initialiser
            // folds, and empty for a bare literal - see CompletionItem.ConstantValue.
            json.Append(",\"constant\":");
            SymbolJson.AppendString(json, item.ConstantValue);
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
