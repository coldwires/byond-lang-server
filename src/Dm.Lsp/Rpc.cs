using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Dm.Lsp;

/// <summary>
/// LSP's base protocol: JSON-RPC 2.0 messages framed with a Content-Length header over stdio.
/// </summary>
/// <remarks>
/// Hand-rolled rather than a protocol library, resolving PLAN.md open question 6: the method set
/// is a dozen entries, the framing is two header lines, and owning it keeps <c>Dm.Lsp</c>
/// dependency-free and AOT-able later if startup latency ever matters. The reflection-based
/// libraries solve a bigger problem than this shell has.
/// </remarks>
internal static class Rpc
{
    /// <summary>Reads one framed message, or null at end of stream.</summary>
    public static JsonDocument? Read(Stream input)
    {
        int contentLength = -1;

        // Headers are ASCII lines ending CRLF; a blank line starts the content.
        while (true)
        {
            string? line = ReadHeaderLine(input);

            if (line is null)
                return null;

            if (line.Length == 0)
                break;

            const string prefix = "Content-Length:";
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.Substring(prefix.Length).Trim());
        }

        if (contentLength < 0)
            return null;

        byte[] content = new byte[contentLength];
        int read = 0;

        while (read < contentLength)
        {
            int chunk = input.Read(content, read, contentLength - read);

            if (chunk <= 0)
                return null;

            read += chunk;
        }

        return JsonDocument.Parse(content);
    }

    private static string? ReadHeaderLine(Stream input)
    {
        StringBuilder line = new();

        while (true)
        {
            int b = input.ReadByte();

            if (b < 0)
                return line.Length == 0 ? null : line.ToString();

            if (b == '\n')
            {
                if (line.Length > 0 && line[^1] == '\r')
                    line.Length--;

                return line.ToString();
            }

            line.Append((char)b);
        }
    }

    /// <summary>Writes one framed message. The writer callback produces the JSON body.</summary>
    public static void Write(Stream output, Action<Utf8JsonWriter> body)
    {
        using MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer))
        {
            body(json);
        }

        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {buffer.Length}\r\n\r\n");

        output.Write(header, 0, header.Length);
        buffer.Position = 0;
        buffer.CopyTo(output);
        output.Flush();
    }

    /// <summary>A response carrying a result produced by the callback.</summary>
    public static void Respond(Stream output, JsonElement id, Action<Utf8JsonWriter> result)
        => Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("id");
            id.WriteTo(json);
            json.WritePropertyName("result");
            result(json);
            json.WriteEndObject();
        });

    /// <summary>An error response. Codes follow JSON-RPC: -32601 method not found, -32603 internal.</summary>
    public static void RespondError(Stream output, JsonElement id, int code, string message)
        => Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WritePropertyName("id");
            id.WriteTo(json);
            json.WriteStartObject("error");
            json.WriteNumber("code", code);
            json.WriteString("message", message);
            json.WriteEndObject();
            json.WriteEndObject();
        });

    /// <summary>
    /// A server-initiated request, such as <c>window/workDoneProgress/create</c>. The client's
    /// response comes back as a message with an id and no method; the dispatcher ignores those.
    /// </summary>
    public static void Request(Stream output, int id, string method, Action<Utf8JsonWriter> parameters)
        => Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteNumber("id", id);
            json.WriteString("method", method);
            json.WritePropertyName("params");
            parameters(json);
            json.WriteEndObject();
        });

    /// <summary>A server-initiated notification, such as publishDiagnostics.</summary>
    public static void Notify(Stream output, string method, Action<Utf8JsonWriter> parameters)
        => Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteString("jsonrpc", "2.0");
            json.WriteString("method", method);
            json.WritePropertyName("params");
            parameters(json);
            json.WriteEndObject();
        });
}
