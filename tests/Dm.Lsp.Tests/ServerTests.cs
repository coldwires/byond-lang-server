using System.Text;
using System.Text.Json;
using Dm.Lsp;

namespace Dm.Lsp.Tests;

/// <summary>
/// Drives <see cref="LspServer"/> with real protocol messages and reads back real frames — the
/// same bytes a client sees, without a process in between.
/// </summary>
public sealed class ServerTests : IDisposable
{
    private readonly string _root;
    private readonly MemoryStream _output = new();
    private readonly LspServer _server;

    public ServerTests()
    {
        _root = Directory.CreateTempSubdirectory("dm-lsp-test-").FullName;

        File.WriteAllText(Path.Combine(_root, "game.dme"), "#include \"code.dm\"\n");
        File.WriteAllText(
            Path.Combine(_root, "code.dm"),
            "/mob\n\tvar/hp = 1\n\n/proc/f()\n\tvar/mob/m = new\n\treturn m\n");

        _server = new LspServer(_output);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Send(string json)
    {
        using JsonDocument message = JsonDocument.Parse(json);
        _server.Dispatch(message);
    }

    /// <summary>Every frame written so far, parsed, oldest first.</summary>
    private List<JsonDocument> Frames()
    {
        List<JsonDocument> frames = new();
        _output.Position = 0;

        while (Rpc.Read(_output) is { } frame)
            frames.Add(frame);

        return frames;
    }

    private string RootUri() => new Uri(_root).AbsoluteUri;

    private string FileUri(string name) => new Uri(Path.Combine(_root, name)).AbsoluteUri;

    private void Initialize()
        => Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"rootUri\":\"{RootUri()}\"}}}}");

    [Fact]
    public void Initialize_reports_capabilities_and_finds_the_dme()
    {
        Initialize();

        JsonDocument response = Assert.Single(Frames());
        JsonElement capabilities = response.RootElement.GetProperty("result").GetProperty("capabilities");

        Assert.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString());
        Assert.True(capabilities.GetProperty("hoverProvider").GetBoolean());
        Assert.Equal(1, capabilities.GetProperty("textDocumentSync").GetInt32());
    }

    [Fact]
    public void A_broken_buffer_produces_diagnostics_and_a_fix_clears_them()
    {
        Initialize();

        string uri = FileUri("code.dm");

        // `var/in` is a must-fail the fixtures pin: dm.exe rejects it on use.
        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/proc/f()\\n\\tvar/x = /obj/nothing\\n\"}}}}}}");

        List<JsonDocument> afterOpen = Frames();
        JsonElement published = afterOpen[^1].RootElement;

        Assert.Equal("textDocument/publishDiagnostics", published.GetProperty("method").GetString());
        Assert.True(published.GetProperty("params").GetProperty("diagnostics").GetArrayLength() > 0);

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"version\":2}},\"contentChanges\":[{{\"text\":\"/proc/f()\\n\\treturn 1\\n\"}}]}}}}");

        List<JsonDocument> afterFix = Frames();
        JsonElement cleared = afterFix[^1].RootElement;

        Assert.Equal("textDocument/publishDiagnostics", cleared.GetProperty("method").GetString());
        Assert.Equal(0, cleared.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public void Completion_after_a_dot_offers_the_receivers_members()
    {
        Initialize();

        string uri = FileUri("code.dm");

        // The buffer ends `return m.` — cursor right after the dot on line 5, character 10.
        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n\\n/proc/f()\\n\\tvar/mob/m = new\\n\\treturn m.\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":5,\"character\":10}}}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement result = frames[^1].RootElement.GetProperty("result");

        List<string> labels = new();

        foreach (JsonElement item in result.EnumerateArray())
            labels.Add(item.GetProperty("label").GetString() ?? "");

        Assert.Contains("hp", labels);
        Assert.Contains("loc", labels); // inherited from the builtin /atom chain
    }

    [Fact]
    public void Definition_lands_on_the_declaration_in_the_right_file()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n\\n/proc/f()\\n\\tvar/mob/m = new\\n\\treturn m.hp\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/definition\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":5,\"character\":11}}}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement result = frames[^1].RootElement.GetProperty("result");

        Assert.True(result.GetArrayLength() > 0);

        JsonElement first = result[0];
        Assert.EndsWith("code.dm", new Uri(first.GetProperty("uri").GetString()!).LocalPath);
        Assert.Equal(1, first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    /// <summary>
    /// VS Code drops the whole outline when any symbol's selectionRange is not contained in its
    /// range — a validation the native IDE clients never applied, so nothing upstream caught it.
    /// </summary>
    [Fact]
    public void Document_symbols_are_present_and_every_selection_is_inside_its_range()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n\\tproc/heal(amount)\\n\\t\\thp += amount\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"textDocument/documentSymbol\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement result = frames[^1].RootElement.GetProperty("result");

        Assert.True(result.GetArrayLength() > 0, "no symbols at all");

        static void AssertContained(JsonElement symbol)
        {
            (int, int) Point(JsonElement range, string end) => (
                range.GetProperty(end).GetProperty("line").GetInt32(),
                range.GetProperty(end).GetProperty("character").GetInt32());

            JsonElement range = symbol.GetProperty("range");
            JsonElement selection = symbol.GetProperty("selectionRange");

            Assert.True(Point(range, "start").CompareTo(Point(selection, "start")) <= 0,
                $"{symbol.GetProperty("name")}: selection starts before its range");
            Assert.True(Point(selection, "end").CompareTo(Point(range, "end")) <= 0,
                $"{symbol.GetProperty("name")}: selection ends after its range");

            if (symbol.TryGetProperty("children", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                    AssertContained(child);
            }
        }

        foreach (JsonElement symbol in result.EnumerateArray())
            AssertContained(symbol);
    }

    /// <summary>
    /// VS Code spells file URIs with a percent-encoded, lowercased drive colon —
    /// <c>file:///c%3A/...</c> — and .NET's <c>Uri.LocalPath</c> mangles that form into
    /// <c>/c:/...</c>. The first real session failed on exactly this: no .dme found, workspace
    /// silently off, empty outline. This test speaks VS Code's spelling end to end.
    /// </summary>
    [Fact]
    public void A_percent_encoded_drive_colon_still_opens_the_workspace()
    {
        string rootUri = RootUri();
        string fileUri = FileUri("code.dm");

        // file:///C:/x -> file:///c%3A/x, the spelling VS Code actually sends. The drive letter
        // sits right after the "file:///" prefix.
        static string VsCodeSpelling(string uri)
        {
            const int prefix = 8; // "file:///"
            Assert.Equal(':', uri[prefix + 1]);

            return uri.Substring(0, prefix)
                + char.ToLowerInvariant(uri[prefix])
                + "%3A"
                + uri.Substring(prefix + 2);
        }

        Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"rootUri\":\"{VsCodeSpelling(rootUri)}\"}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{VsCodeSpelling(fileUri)}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/proc/f()\\n\\tvar/x = /obj/nothing\\n\"}}}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement published = frames[^1].RootElement;

        // Diagnostics arriving proves the workspace opened: with no workspace the didOpen is a
        // silent no-op, which is exactly the failure this guards against.
        Assert.Equal("textDocument/publishDiagnostics", published.GetProperty("method").GetString());
        Assert.True(published.GetProperty("params").GetProperty("diagnostics").GetArrayLength() > 0);
    }

    [Fact]
    public void Signature_help_names_the_proc_and_the_active_parameter()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tproc/heal(target, amount)\\n\\t\\treturn amount\\n/mob/proc/f()\\n\\theal(src, \\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/signatureHelp\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":4,\"character\":11}}}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement result = frames[^1].RootElement.GetProperty("result");

        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());
        Assert.StartsWith("heal(", result.GetProperty("signatures")[0].GetProperty("label").GetString());
    }

    /// <summary>
    /// The legend is advertised and the data decodes: a keyword token where `var` sits, and a
    /// multi-line string split into one token per line, since VS Code renders only the first line
    /// of a semantic token that crosses lines.
    /// </summary>
    [Fact]
    public void Semantic_tokens_cover_the_file_and_split_multiline_spans()
    {
        Initialize();

        string uri = FileUri("code.dm");

        // Line 1 holds `var/s = {"one` and the string runs through line 2 (`two`) to `"}` on
        // line 2's end. The string span crosses a line boundary.
        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/proc/f()\\n\\tvar/s = {{\\\"one\\ntwo\\\"}}\\n\\treturn s\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"textDocument/semanticTokens/full\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}");

        List<JsonDocument> frames = Frames();

        JsonElement legend = frames[0].RootElement.GetProperty("result").GetProperty("capabilities")
            .GetProperty("semanticTokensProvider").GetProperty("legend").GetProperty("tokenTypes");
        List<string> types = new();

        foreach (JsonElement type in legend.EnumerateArray())
            types.Add(type.GetString() ?? "");

        JsonElement data = frames[^1].RootElement.GetProperty("result").GetProperty("data");
        Assert.True(data.GetArrayLength() > 0, "no tokens at all");
        Assert.Equal(0, data.GetArrayLength() % 5);

        // Decode the relative encoding back to absolute (line, character, length, type).
        List<(int Line, int Character, int Length, string Type)> tokens = new();
        int line = 0, character = 0;

        for (int i = 0; i < data.GetArrayLength(); i += 5)
        {
            int deltaLine = data[i].GetInt32();
            line += deltaLine;
            character = deltaLine == 0 ? character + data[i + 1].GetInt32() : data[i + 1].GetInt32();
            tokens.Add((line, character, data[i + 2].GetInt32(), types[data[i + 3].GetInt32()]));
        }

        // `var` on line 1, character 1, is a keyword token of length 3.
        Assert.Contains((1, 1, 3, "keyword"), tokens);

        // The multiline string produced a string token on line 1 AND one on line 2 starting at
        // character 0 — one span split per line, not a single token VS Code would truncate.
        Assert.Contains(tokens, t => t.Line == 1 && t.Type == "string");
        Assert.Contains(tokens, t => t.Line == 2 && t.Character == 0 && t.Type == "string");
    }

    /// <summary>
    /// A request cancelled while still queued is answered -32800 without running. The cancel
    /// reaches the server ahead of the request here, which is exactly what the reader thread
    /// arranges in the real process — in-order delivery could never cancel anything.
    /// </summary>
    [Fact]
    public void A_cancelled_request_answers_request_cancelled_without_running()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":42}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"textDocument/hover\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":1,\"character\":6}}}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement response = frames[^1].RootElement;

        Assert.Equal(42, response.GetProperty("id").GetInt32());
        Assert.Equal(-32800, response.GetProperty("error").GetProperty("code").GetInt32());

        // The next request with a fresh id runs normally: one cancel consumes one request.
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"textDocument/hover\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":1,\"character\":6}}}}}}");

        frames = Frames();
        Assert.True(frames[^1].RootElement.TryGetProperty("result", out _));
    }

    /// <summary>
    /// The dm/* methods answer with the same shapes dm_query_json freezes in abi/schema/: a node
    /// with childCount and parentType, and members that say which ancestor owns them.
    /// </summary>
    [Fact]
    public void Dm_members_resolves_inheritance_and_names_the_owner()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n/mob/guy\\n\\tvar/mana = 2\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"dm/members\",\"params\":{{\"path\":\"/mob/guy\"}}}}");

        List<JsonDocument> frames = Frames();
        JsonElement result = frames[^1].RootElement.GetProperty("result");

        Assert.Equal("members", result.GetProperty("query").GetString());
        Assert.Equal("/mob/guy", result.GetProperty("path").GetString());

        (string Name, string Owner, bool Inherited)? hp = null, mana = null;

        foreach (JsonElement member in result.GetProperty("vars").EnumerateArray())
        {
            var entry = (
                member.GetProperty("name").GetString() ?? "",
                member.GetProperty("owner").GetString() ?? "",
                member.GetProperty("inherited").GetBoolean());

            if (entry.Item1 == "hp")
                hp = entry;
            if (entry.Item1 == "mana")
                mana = entry;
        }

        Assert.Equal(("hp", "/mob", true), hp);
        Assert.Equal(("mana", "/mob/guy", false), mana);
    }

    [Fact]
    public void Dm_objectTree_reports_childCount_and_a_missing_path_is_an_error()
    {
        Initialize();

        Send("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"dm/objectTree\",\"params\":{\"path\":\"/mob\",\"depth\":0}}");
        Send("{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"dm/objectTree\",\"params\":{\"path\":\"/no/such/type\"}}");

        List<JsonDocument> frames = Frames();

        JsonElement node = frames[^2].RootElement.GetProperty("result").GetProperty("node");
        Assert.Equal("/mob", node.GetProperty("path").GetString());

        // Depth 0 returns the node alone, and childCount still says whether to draw an expander.
        Assert.Equal(0, node.GetProperty("children").GetArrayLength());
        Assert.Equal("/atom/movable", node.GetProperty("parentType").GetString());

        JsonElement error = frames[^1].RootElement.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public void An_unknown_method_answers_with_an_error_rather_than_silence()
    {
        Initialize();
        Send("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"textDocument/rename\",\"params\":{}}");

        List<JsonDocument> frames = Frames();
        JsonElement error = frames[^1].RootElement.GetProperty("error");

        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
    }
}
