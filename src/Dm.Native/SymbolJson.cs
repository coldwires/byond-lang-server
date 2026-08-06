using System.Collections.Generic;
using System.Text;
using Dm.Core.Diagnostics;
using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>
/// Serialises a document outline for the bulk side of the ABI.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than reflection-based. <c>Dm.Core</c> and this assembly are both NativeAOT
/// targets, so a serializer that discovers properties at runtime is not an option — see PLAN.md §7.
/// The shape is small and stable enough that a writer costs less than source generators would.
/// </para>
/// <para>
/// Symbols go over as JSON rather than a packed block, unlike classification. They carry names and
/// details, so packing would need a string table on both sides, and an outline is rebuilt per edit
/// rather than per scroll — the per-item cost matters far less than it does for highlighting.
/// </para>
/// </remarks>
internal static class SymbolJson
{
    public static string Write(
        IReadOnlyList<DocumentSymbol> symbols,
        IReadOnlyList<Diagnostic> diagnostics,
        SourceText text,
        PositionEncoding encoding)
    {
        StringBuilder json = new();
        json.Append("{\"symbols\":");
        WriteSymbols(json, symbols);

        json.Append(",\"diagnostics\":");
        WriteDiagnostics(json, diagnostics, text, encoding);

        json.Append('}');
        return json.ToString();
    }

    /// <summary>
    /// Writes a diagnostics array. Shared with <c>dm_diagnostics</c>, so the elements a client
    /// reads there are byte-identical to the ones this document has always carried.
    /// </summary>
    internal static void WriteDiagnostics(
        StringBuilder json,
        IReadOnlyList<Diagnostic> diagnostics,
        SourceText text,
        PositionEncoding encoding)
    {
        json.Append('[');

        for (int i = 0; i < diagnostics.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            Diagnostic diagnostic = diagnostics[i];
            LinePosition start = text.GetLinePosition(diagnostic.Span.Start, encoding);
            LinePosition end = text.GetLinePosition(diagnostic.Span.End, encoding);

            json.Append("{\"id\":");
            AppendString(json, diagnostic.Id);
            json.Append(",\"severity\":");
            AppendString(json, SeverityName(diagnostic.Severity));
            json.Append(",\"message\":");
            AppendString(json, diagnostic.Message);
            json.Append(",\"startLine\":").Append(start.Line);
            json.Append(",\"startChar\":").Append(start.Character);
            json.Append(",\"endLine\":").Append(end.Line);
            json.Append(",\"endChar\":").Append(end.Character);
            json.Append('}');
        }

        json.Append(']');
    }

    /// <summary>
    /// The severity as a word rather than a number.
    /// </summary>
    /// <remarks>
    /// LSP numbers these 1–4 and our enum starts at 0, so shipping either integer would invite a
    /// client to map it with the other scheme's table and silently paint warnings as errors. A word
    /// cannot be misread, and this document is already full of strings.
    /// </remarks>
    private static string SeverityName(DiagnosticSeverity severity)
        => severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Information => "information",
            _ => "hint",
        };

    private static void WriteSymbols(StringBuilder json, IReadOnlyList<DocumentSymbol> symbols)
    {
        json.Append('[');

        for (int i = 0; i < symbols.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            DocumentSymbol symbol = symbols[i];

            json.Append("{\"name\":");
            AppendString(json, symbol.Name);
            json.Append(",\"detail\":");
            AppendString(json, symbol.Detail);
            json.Append(",\"kind\":").Append((int)symbol.Kind);
            json.Append(",\"startLine\":").Append(symbol.Start.Line);
            json.Append(",\"startChar\":").Append(symbol.Start.Character);
            json.Append(",\"endLine\":").Append(symbol.End.Line);
            json.Append(",\"endChar\":").Append(symbol.End.Character);
            json.Append(",\"selStartLine\":").Append(symbol.SelectionStart.Line);
            json.Append(",\"selStartChar\":").Append(symbol.SelectionStart.Character);
            json.Append(",\"selEndLine\":").Append(symbol.SelectionEnd.Line);
            json.Append(",\"selEndChar\":").Append(symbol.SelectionEnd.Character);
            json.Append(",\"children\":");
            WriteSymbols(json, symbol.Children);
            json.Append('}');
        }

        json.Append(']');
    }

    /// <summary>
    /// Writes a JSON string literal.
    /// </summary>
    /// <remarks>
    /// Non-ASCII is emitted as-is, since the buffer crossing the boundary is UTF-8. Only the
    /// characters JSON actually forbids are escaped. DM identifiers can legally contain backslash
    /// escapes (<c>\~Admin_Chat</c>), so the backslash case is reached by real code, not just by
    /// string literals.
    /// </remarks>
    internal static void AppendString(StringBuilder json, string? value)
    {
        json.Append('"');

        if (value is not null)
        {
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        json.Append("\\\"");
                        break;

                    case '\\':
                        json.Append("\\\\");
                        break;

                    case '\n':
                        json.Append("\\n");
                        break;

                    case '\r':
                        json.Append("\\r");
                        break;

                    case '\t':
                        json.Append("\\t");
                        break;

                    default:
                        if (c < 0x20)
                            json.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            json.Append(c);

                        break;
                }
            }
        }

        json.Append('"');
    }
}
