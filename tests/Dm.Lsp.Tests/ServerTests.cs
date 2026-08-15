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

    /// <summary>
    /// A client offering only utf-8 gets utf-8 — declared in the result AND spoken in the
    /// answers. <c>é</c> is two UTF-8 units and one UTF-16 unit, so the diagnostic's column says
    /// which encoding the server is actually using; before negotiation this client was mis-served
    /// silently, and only on lines like this one.
    /// </summary>
    [Fact]
    public void A_utf8_only_client_gets_utf8_positions()
    {
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"rootUri\":\"{RootUri()}\","
            + "\"capabilities\":{\"general\":{\"positionEncodings\":[\"utf-8\"]}}}}");

        Assert.Equal(
            "utf-8",
            Frames()[^1].RootElement.GetProperty("result").GetProperty("capabilities")
                .GetProperty("positionEncoding").GetString());

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/proc/f()\\n\\tvar/x = \\\"é\\\" + /obj/nothing\\n\"}}}}}}");

        JsonElement diagnostic = Frames()[^1].RootElement
            .GetProperty("params").GetProperty("diagnostics")[0];
        int character = diagnostic.GetProperty("range").GetProperty("start")
            .GetProperty("character").GetInt32();

        // The path starts at UTF-8 column 16 — one MORE than the UTF-16 column, because of the é.
        Assert.Equal(16, character);
    }

    /// <summary>
    /// The disk moving underneath the workspace — a git checkout, another editor — reaches
    /// answers once the client says so: <c>didChangeWatchedFiles</c> invalidates, and the next
    /// query sees what is on disk now.
    /// </summary>
    [Fact]
    public void A_disk_change_reaches_answers_after_didChangeWatchedFiles()
    {
        Initialize();

        Send($"{{\"jsonrpc\":\"2.0\",\"id\":40,\"method\":\"dm/members\",\"params\":{{\"path\":\"/mob\"}}}}");

        // The editor never sees this write; only the watcher notification announces it.
        File.WriteAllText(Path.Combine(_root, "code.dm"), "/mob\n\tvar/hp = 1\n\tvar/mp = 2\n");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"workspace/didChangeWatchedFiles\",\"params\":{{\"changes\":[{{\"uri\":\"{FileUri("code.dm")}\",\"type\":2}}]}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"dm/members\",\"params\":{{\"path\":\"/mob\"}}}}");

        // The first tree build interleaves $/progress frames, so responses are found by id.
        List<JsonDocument> frames = Frames();

        List<string?> VarNames(int id)
        {
            foreach (JsonDocument frame in frames)
            {
                if (!frame.RootElement.TryGetProperty("id", out JsonElement found)
                    || found.ValueKind != JsonValueKind.Number
                    || found.GetInt32() != id)
                {
                    continue;
                }

                List<string?> names = new();

                foreach (JsonElement entry in frame.RootElement.GetProperty("result").GetProperty("vars").EnumerateArray())
                    names.Add(entry.GetProperty("name").GetString());

                return names;
            }

            throw new Xunit.Sdk.XunitException($"no response for id {id}");
        }

        Assert.DoesNotContain("mp", VarNames(40));
        Assert.Contains("mp", VarNames(41));
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

        // A CompletionList, not a bare array: isIncomplete is what tells the client whether it may
        // filter locally, and it is false because nothing capped this list.
        Assert.False(result.GetProperty("isIncomplete").GetBoolean());

        List<string> labels = new();

        foreach (JsonElement item in result.GetProperty("items").EnumerateArray())
            labels.Add(item.GetProperty("label").GetString() ?? "");

        Assert.Contains("hp", labels);
        Assert.Contains("loc", labels); // inherited from the builtin /atom chain

        // The project's own member outranks BYOND's, and sortText is what pins that in VS Code.
        Assert.True(labels.IndexOf("hp") < labels.IndexOf("loc"), "a declared member ranks above a builtin");
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
    [SkippableFact]
    public void A_percent_encoded_drive_colon_still_opens_the_workspace()
    {
        // A DRIVE LETTER is the whole subject, so there is nothing to assert where paths have no
        // drive: on Linux RootUri() is file:///home/... and the spelling helper below has no colon
        // to find. It skipped silently for months because CI never ran this job against a real
        // remote; the first run on ubuntu failed with Expected ':' / Actual 'm', from /home.
        Skip.IfNot(OperatingSystem.IsWindows(), "drive-letter URIs are a Windows spelling");

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
    /// The M8 method: icon states over the LSP, in the shape <c>dm_icon_states</c> answers, and a
    /// file that is not an icon reported as an answer rather than an error.
    /// </summary>
    [Fact]
    public void Dm_iconStates_reads_an_icon_and_reports_a_non_icon_as_one()
    {
        // A minimal .dmi: a PNG carrying the metadata in an uncompressed tEXt chunk, which the
        // reader accepts alongside the deflated zTXt Dream Maker writes.
        string metadata =
            "# BEGIN DMI\nversion = 4.0\nwidth = 32\nheight = 32\n" +
            "state = \"door\"\n\tdirs = 4\n\tframes = 1\n# END DMI\n";

        List<byte> body = new(System.Text.Encoding.ASCII.GetBytes("Description"));
        body.Add(0);
        body.AddRange(System.Text.Encoding.Latin1.GetBytes(metadata));

        List<byte> png = new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Append(png, "tEXt", body);
        Append(png, "IEND", new List<byte>());

        File.WriteAllBytes(Path.Combine(_root, "icon.dmi"), png.ToArray());

        Initialize();

        Send($"{{\"jsonrpc\":\"2.0\",\"id\":31,\"method\":\"dm/iconStates\",\"params\":{{\"uri\":\"{FileUri("icon.dmi")}\"}}}}");

        JsonElement result = Frames()[^1].RootElement.GetProperty("result");

        Assert.True(result.GetProperty("isDmi").GetBoolean());
        Assert.Equal(32, result.GetProperty("width").GetInt32());
        Assert.Equal("door", result.GetProperty("states")[0].GetProperty("name").GetString());
        Assert.Equal(4, result.GetProperty("states")[0].GetProperty("dirs").GetInt32());

        // code.dm is real and is not a PNG: an answer, not a failure.
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":32,\"method\":\"dm/iconStates\",\"params\":{{\"uri\":\"{FileUri("code.dm")}\"}}}}");

        JsonElement plain = Frames()[^1].RootElement.GetProperty("result");

        Assert.False(plain.GetProperty("isDmi").GetBoolean());
        Assert.Empty(plain.GetProperty("states").EnumerateArray());

        static void Append(List<byte> png, string kind, List<byte> body)
        {
            int length = body.Count;

            png.Add((byte)(length >> 24));
            png.Add((byte)(length >> 16));
            png.Add((byte)(length >> 8));
            png.Add((byte)length);
            png.AddRange(System.Text.Encoding.ASCII.GetBytes(kind));
            png.AddRange(body);
            png.AddRange(new byte[4]);
        }
    }

    /// <summary>
    /// Colours come back as LSP's 0-1 floats while the core speaks DM's 0-255, and the range
    /// covers the whole literal including its quotes so a picker replaces the lot.
    /// </summary>
    [Fact]
    public void Document_colors_are_reported_as_floats_over_the_whole_literal()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/obj/paint\\n\\tcolor = \\\"#ff0080\\\"\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"textDocument/documentColor\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}}}}}}");

        JsonElement result = Frames()[^1].RootElement.GetProperty("result");
        JsonElement color = result[0];

        Assert.Equal(1.0, color.GetProperty("color").GetProperty("red").GetDouble(), 3);
        Assert.Equal(0.0, color.GetProperty("color").GetProperty("green").GetDouble(), 3);
        Assert.Equal(128 / 255.0, color.GetProperty("color").GetProperty("blue").GetDouble(), 3);
        Assert.Equal(1.0, color.GetProperty("color").GetProperty("alpha").GetDouble(), 3);

        // `\tcolor = "#ff0080"` — the opening quote is character 9, the closing one ends at 18.
        Assert.Equal(1, color.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        Assert.Equal(9, color.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32());
        Assert.Equal(18, color.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32());
    }

    /// <summary>
    /// A presentation is what gets written when the user picks a colour, and the form already in
    /// the file leads — picking a shade beside an rgb() call must not rewrite it as a literal.
    /// </summary>
    [Fact]
    public void Color_presentations_keep_the_form_that_is_already_written()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/obj/paint\\n\\tvar/c = rgb(255, 0, 128)\\n\"}}}}}}");

        // The range the client sends back is the one documentColor reported: `rgb(255, 0, 128)`
        // starts at character 9 and ends at 25.
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"textDocument/colorPresentation\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"color\":{{\"red\":1.0,\"green\":0.0,\"blue\":0.5019607843137255,\"alpha\":1.0}},\"range\":{{\"start\":{{\"line\":1,\"character\":9}},\"end\":{{\"line\":1,\"character\":25}}}}}}}}");

        JsonElement result = Frames()[^1].RootElement.GetProperty("result");

        Assert.Equal("rgb(255, 0, 128)", result[0].GetProperty("label").GetString());
        Assert.Equal("\"#ff0080\"", result[1].GetProperty("label").GetString());
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
    /// `dm/overriddenProc` answers what a definition overrides — the inverse of `implementation`.
    /// Overriding NOTHING is an answer rather than an error, since that is the case dm.exe's own
    /// `no_parent` warning reports on, and a client draws its affordance from the flag.
    /// </summary>
    [Fact]
    public void Dm_overridden_proc_reports_both_answers()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tproc/base()\\n\\t\\treturn 1\\n/mob/guy\\n\\tbase()\\n\\t\\treturn 2\\n\\tproc/fresh()\\n\\t\\treturn 3\\n\"}}}}}}");

        Send($"{{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"dm/overriddenProc\",\"params\":{{\"path\":\"/mob/guy\",\"name\":\"base\"}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":61,\"method\":\"dm/overriddenProc\",\"params\":{{\"path\":\"/mob/guy\",\"name\":\"fresh\"}}}}");

        List<JsonDocument> frames = Frames();

        JsonElement overriding = frames.Single(
            f => f.RootElement.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 60)
            .RootElement.GetProperty("result");

        Assert.True(overriding.GetProperty("overrides").GetBoolean());
        Assert.Equal("/mob", overriding.GetProperty("owner").GetString());
        Assert.False(overriding.GetProperty("builtin").GetBoolean());

        JsonElement fresh = frames.Single(
            f => f.RootElement.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 61)
            .RootElement.GetProperty("result");

        Assert.False(fresh.GetProperty("overrides").GetBoolean());
        Assert.Equal(string.Empty, fresh.GetProperty("owner").GetString());
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

    /// <summary>
    /// References at a member's use finds every use and not the declaration; highlight filters to
    /// the asked document and carries read/write kinds.
    /// </summary>
    [Fact]
    public void References_and_highlight_find_the_uses()
    {
        Initialize();

        string uri = FileUri("code.dm");

        // Line 3 `hp = 2` is a write inside hurt; line 7 `m.hp` is a read inside f.
        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n\\tproc/hurt()\\n\\t\\thp = 2\\n\\n/proc/f()\\n\\tvar/mob/m = new\\n\\treturn m.hp\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":15,\"method\":\"textDocument/references\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":7,\"character\":10}},\"context\":{{\"includeDeclaration\":false}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":16,\"method\":\"textDocument/documentHighlight\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":7,\"character\":10}}}}}}");

        List<JsonDocument> frames = Frames();

        JsonElement references = frames[^2].RootElement.GetProperty("result");
        Assert.Equal(2, references.GetArrayLength());

        JsonElement highlights = frames[^1].RootElement.GetProperty("result");
        Assert.Equal(2, highlights.GetArrayLength());

        List<int> kinds = new();

        foreach (JsonElement highlight in highlights.EnumerateArray())
            kinds.Add(highlight.GetProperty("kind").GetInt32());

        Assert.Contains(3, kinds); // the write
        Assert.Contains(2, kinds); // the read
    }

    /// <summary>
    /// Rename answers a WorkspaceEdit of PROVABLE sites only, announces the uncertain count as a
    /// window/showMessage — the standard response has no field for it — and dm/rename returns the
    /// full answer with each uncertain site's reason.
    /// </summary>
    [Fact]
    public void Rename_edits_the_proven_sites_and_reports_the_colon_access()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n\\tproc/hurt()\\n\\t\\thp = 2\\n\\n/proc/f()\\n\\tvar/mob/m = new\\n\\treturn m.hp + m:hp\\n\"}}}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"textDocument/rename\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":7,\"character\":10}},\"newName\":\"health\"}}}}");
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"dm/rename\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":7,\"character\":10}},\"newName\":\"health\"}}}}");

        List<JsonDocument> frames = Frames();

        Assert.Contains(frames, f =>
            f.RootElement.TryGetProperty("method", out JsonElement method)
            && method.GetString() == "window/showMessage");

        // The declaration, the bare write inside hurt, and m.hp — three proven sites, one file.
        JsonElement changes = frames[^2].RootElement.GetProperty("result").GetProperty("changes");
        JsonProperty only = Assert.Single(changes.EnumerateObject());
        Assert.Equal(3, only.Value.GetArrayLength());

        foreach (JsonElement edit in only.Value.EnumerateArray())
            Assert.Equal("health", edit.GetProperty("newText").GetString());

        // `m:hp` is reported with its reason rather than edited.
        JsonElement full = frames[^1].RootElement.GetProperty("result");
        Assert.Equal("none", full.GetProperty("refusal").GetString());
        Assert.Equal("/mob/hp", full.GetProperty("target").GetString());
        Assert.Equal(3, full.GetProperty("edits").GetArrayLength());

        JsonElement uncertain = full.GetProperty("uncertain");
        Assert.Equal(1, uncertain.GetArrayLength());
        Assert.Equal("colonAccess", uncertain[0].GetProperty("reason").GetString());
    }

    /// <summary>
    /// The first tree build announces itself: a workDoneProgress/create request, a begin, the
    /// answer, an end — and the client's response to the create is ignored rather than answered
    /// with a method-not-supported error.
    /// </summary>
    [Fact]
    public void The_first_build_reports_progress_and_responses_are_ignored()
    {
        Initialize();

        string uri = FileUri("code.dm");

        Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"/mob\\n\\tvar/hp = 1\\n\"}}}}}}");

        List<JsonDocument> frames = Frames();

        List<string> methods = new();

        foreach (JsonDocument frame in frames)
        {
            if (frame.RootElement.TryGetProperty("method", out JsonElement m))
                methods.Add(m.GetString() ?? "");
        }

        Assert.Contains("window/workDoneProgress/create", methods);
        Assert.Equal(2, methods.Count(m => m == "$/progress")); // begin and end

        // The begin arrives before the diagnostics the build produced.
        Assert.True(
            methods.IndexOf("$/progress") < methods.IndexOf("textDocument/publishDiagnostics"),
            "progress must begin before the answer it announces");

        // A second question on the built tree stays silent — no fresh progress.
        int before = frames.Count;
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"textDocument/hover\",\"params\":{{\"textDocument\":{{\"uri\":\"{uri}\"}},\"position\":{{\"line\":1,\"character\":6}}}}}}");
        Assert.Equal(before + 1, Frames().Count);

        // The client's response to the create request is consumed without a reply.
        Send("{\"jsonrpc\":\"2.0\",\"id\":1000000,\"result\":null}");
        Assert.Equal(before + 1, Frames().Count);
    }

    [Fact]
    public void An_unknown_method_answers_with_an_error_rather_than_silence()
    {
        Initialize();
        // A name no future feature will claim — textDocument/rename was the probe here until it
        // became a real method at ABI 0.27 and this test started failing for the right reason.
        Send("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"dm/noSuchMethod\",\"params\":{}}");

        List<JsonDocument> frames = Frames();
        JsonElement error = frames[^1].RootElement.GetProperty("error");

        Assert.Equal(-32601, error.GetProperty("code").GetInt32());
    }

    private void WriteNestedProject()
    {
        string game = Path.Combine(_root, "sub", "game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "nested.dme"), "#include \"types.dm\"\n#include \"play.dm\"\n");
        File.WriteAllText(Path.Combine(game, "types.dm"), "/mob/pet\n\tvar/tricks = 1\n");
        File.WriteAllText(Path.Combine(game, "play.dm"), "/proc/g()\n\treturn 1\n");
    }

    private void DidOpen(string name, string text = "/proc/opened()\\n\\treturn 1\\n")
        => Send($"{{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{{\"textDocument\":{{\"uri\":\"{FileUri(name)}\",\"languageId\":\"dm\",\"version\":1,\"text\":\"{text}\"}}}}}}");

    private JsonElement ResponseTo(int id)
    {
        foreach (JsonDocument frame in Frames())
        {
            if (frame.RootElement.TryGetProperty("id", out JsonElement found)
                && found.ValueKind == JsonValueKind.Number
                && found.GetInt32() == id)
            {
                return frame.RootElement;
            }
        }

        throw new Xunit.Sdk.XunitException($"no response for id {id}");
    }

    /// <summary>
    /// With nothing configured, the FIRST opened document picks the project by proximity: the
    /// nearest .dme walking up from the file wins over the workspace root's — which is what a
    /// game nested below the root needs, and what the root-level scan can never find. The swap
    /// is announced through dm/environment so a client's status bar shows the project actually
    /// analysed, and the auto-discovered-without-defines note arrives as a window/showMessage.
    /// </summary>
    [Fact]
    public void The_first_opened_file_picks_the_nearest_dme()
    {
        WriteNestedProject();
        Initialize();
        DidOpen("sub/game/play.dm", "/proc/g()\\n\\treturn 1\\n");

        // /mob/pet lives in a file only nested.dme includes, so answering it proves the nested
        // project was adopted — the root's game.dme knows nothing of it.
        Send("{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"dm/members\",\"params\":{\"path\":\"/mob/pet\"}}");

        List<string?> names = new();

        foreach (JsonElement entry in ResponseTo(60).GetProperty("result").GetProperty("vars").EnumerateArray())
            names.Add(entry.GetProperty("name").GetString());

        Assert.Contains("tricks", names);

        List<JsonDocument> frames = Frames();

        JsonDocument environment = Assert.Single(frames, f =>
            f.RootElement.TryGetProperty("method", out JsonElement m)
            && m.GetString() == "dm/environment");
        JsonElement announced = environment.RootElement.GetProperty("params");

        Assert.EndsWith("nested.dme", announced.GetProperty("environmentFile").GetString());
        Assert.True(announced.GetProperty("autoDiscovered").GetBoolean());

        JsonDocument note = Assert.Single(frames, f =>
            f.RootElement.TryGetProperty("method", out JsonElement m)
            && m.GetString() == "window/showMessage");

        Assert.Equal(3, note.RootElement.GetProperty("params").GetProperty("type").GetInt32());
        Assert.Contains("dm.defines", note.RootElement.GetProperty("params").GetProperty("message").GetString());
    }

    /// <summary>
    /// An explicit environmentFile is never second-guessed: opening a file beside a nearer .dme
    /// swaps nothing, no dm/environment is announced — the client configured the value and
    /// already knows it — and no defines note nags a client that has engaged with its settings.
    /// </summary>
    [Fact]
    public void An_explicit_environmentFile_is_never_second_guessed()
    {
        WriteNestedProject();
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"rootUri\":\"{RootUri()}\","
            + "\"initializationOptions\":{\"environmentFile\":\"game.dme\"}}}");
        DidOpen("sub/game/play.dm", "/proc/g()\\n\\treturn 1\\n");

        // The nested project stayed unadopted, so its type is a missing path...
        Send("{\"jsonrpc\":\"2.0\",\"id\":61,\"method\":\"dm/members\",\"params\":{\"path\":\"/mob/pet\"}}");
        Assert.Equal(-32803, ResponseTo(61).GetProperty("error").GetProperty("code").GetInt32());

        // ...while the configured project answers.
        Send("{\"jsonrpc\":\"2.0\",\"id\":62,\"method\":\"dm/members\",\"params\":{\"path\":\"/mob\"}}");
        Assert.True(ResponseTo(62).TryGetProperty("result", out _));

        Assert.DoesNotContain(Frames(), f =>
            f.RootElement.TryGetProperty("method", out JsonElement m)
            && (m.GetString() == "dm/environment" || m.GetString() == "window/showMessage"));
    }

    /// <summary>Configured defines are the client engaging with its settings; no note.</summary>
    [Fact]
    public void Configured_defines_suppress_the_auto_discovery_note()
    {
        Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"rootUri\":\"{RootUri()}\","
            + "\"initializationOptions\":{\"defines\":[\"CBT\"]}}}");
        DidOpen("code.dm", "/mob\\n\\tvar/hp = 1\\n");

        List<JsonDocument> frames = Frames();

        Assert.DoesNotContain(frames, f =>
            f.RootElement.TryGetProperty("method", out JsonElement m)
            && m.GetString() == "window/showMessage");

        // Discovery still announces itself — the .dme was still auto-picked.
        Assert.Contains(frames, f =>
            f.RootElement.TryGetProperty("method", out JsonElement m)
            && m.GetString() == "dm/environment");
    }

    /// <summary>
    /// A 3.17 client may send only workspaceFolders — rootUri is deprecated — and a multi-root
    /// window sends several. One server holds one workspace, so the FIRST folder wins: the
    /// documented contract, pinned here so it cannot regress into "no root found".
    /// </summary>
    [Fact]
    public void The_first_workspace_folder_wins()
    {
        string second = Directory.CreateTempSubdirectory("dm-lsp-second-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(second, "other.dme"), "#include \"other.dm\"\n");
            File.WriteAllText(Path.Combine(second, "other.dm"), "/mob/stranger\n\tvar/unseen = 1\n");

            Send($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"workspaceFolders\":[{{\"uri\":\"{RootUri()}\",\"name\":\"first\"}},{{\"uri\":\"{new Uri(second).AbsoluteUri}\",\"name\":\"second\"}}]}}}}");

            // The first folder's project answers; the second folder's type does not exist.
            Send("{\"jsonrpc\":\"2.0\",\"id\":70,\"method\":\"dm/members\",\"params\":{\"path\":\"/mob\"}}");
            Assert.True(ResponseTo(70).TryGetProperty("result", out _));

            Send("{\"jsonrpc\":\"2.0\",\"id\":71,\"method\":\"dm/members\",\"params\":{\"path\":\"/mob/stranger\"}}");
            Assert.Equal(-32803, ResponseTo(71).GetProperty("error").GetProperty("code").GetInt32());
        }
        finally
        {
            Directory.Delete(second, recursive: true);
        }
    }

    /// <summary>
    /// A single-file window — no workspace root at all — used to mean analysis off. The first
    /// opened file now finds the project above it, so cross-file resolution works with nothing
    /// configured anywhere.
    /// </summary>
    [Fact]
    public void A_single_file_window_finds_the_project_above_it()
    {
        WriteNestedProject();
        Send("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
        DidOpen("sub/game/play.dm", "/proc/g()\\n\\treturn 1\\n");

        Send("{\"jsonrpc\":\"2.0\",\"id\":63,\"method\":\"dm/members\",\"params\":{\"path\":\"/mob/pet\"}}");

        List<string?> names = new();

        foreach (JsonElement entry in ResponseTo(63).GetProperty("result").GetProperty("vars").EnumerateArray())
            names.Add(entry.GetProperty("name").GetString());

        Assert.Contains("tricks", names);
    }
}
