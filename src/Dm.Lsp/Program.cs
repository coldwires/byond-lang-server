using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Dm.Lsp;

/// <summary>
/// stdio entry point. A reader thread takes frames off stdin; this thread dispatches and responds
/// in order, which is the workspace's documented concurrency contract.
/// </summary>
/// <remarks>
/// The split exists for exactly one message: <c>$/cancelRequest</c>. Delivered in order behind
/// the queue, a cancel always arrives after the request it names has been answered and can never
/// do anything. The reader intercepts it at intake — it touches only the server's lock-guarded
/// cancel ledger, never the workspace — so a request still queued is skipped and one mid-flight
/// aborts at its next token check.
/// </remarks>
internal static class Program
{
    private static int Main()
    {
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();

        LspServer server = new(output);
        using BlockingCollection<JsonDocument> queue = new();

        Thread reader = new(() =>
        {
            while (true)
            {
                JsonDocument? message = Rpc.Read(input);

                if (message is null)
                    break;

                if (CancelTarget(message) is { } id)
                {
                    server.RequestCancel(id);
                    message.Dispose();
                    continue;
                }

                queue.Add(message);
            }

            queue.CompleteAdding();
        })
        {
            IsBackground = true,
            Name = "dm-lsp reader",
        };

        reader.Start();

        foreach (JsonDocument message in queue.GetConsumingEnumerable())
        {
            using (message)
            {
                server.Dispatch(message);
            }

            if (server.Exited)
                break;
        }

        return Environment.ExitCode;
    }

    /// <summary>The raw id a <c>$/cancelRequest</c> names, or null for every other message.</summary>
    private static string? CancelTarget(JsonDocument message)
    {
        JsonElement root = message.RootElement;

        if (root.TryGetProperty("method", out JsonElement method)
            && method.ValueKind == JsonValueKind.String
            && method.GetString() == "$/cancelRequest"
            && root.TryGetProperty("params", out JsonElement params_)
            && params_.ValueKind == JsonValueKind.Object
            && params_.TryGetProperty("id", out JsonElement id))
        {
            return id.GetRawText();
        }

        return null;
    }
}
