using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Text;

namespace Dm.Native;

/// <summary>Why a bulk query could not be answered.</summary>
internal enum QueryError
{
    None,

    /// <summary>The request was not readable, or named a query we do not have.</summary>
    BadRequest,

    /// <summary>The request was fine and the path it named is not in the tree.</summary>
    NoSuchPath,
}

/// <summary>
/// The bulk query endpoint: a JSON request in, a JSON response out.
/// </summary>
/// <remarks>
/// <para>
/// Serialized rather than handle-based because these answers are shaped like documents — a tree
/// panel wants a subtree with counts, not a cursor into one — and because the same shapes have to
/// come back over LSP as <c>dm/objectTree</c> at M10. One export carrying a named query keeps the
/// two shells describable by one schema instead of drifting apart per call.
/// </para>
/// <para>
/// Requests are read with <see cref="Utf8JsonReader"/>, which is a forward-only reader with no
/// reflection in it, so <c>Dm.Native</c> stays AOT-clean. Responses are written by hand for the same
/// reason <see cref="SymbolJson"/> is.
/// </para>
/// </remarks>
internal static class QueryJson
{
    /// <summary>A request, as far as any of the queries need it.</summary>
    private readonly record struct Request(
        string Query,
        string Path,
        int Depth,
        int Limit,
        bool Inherited,
        bool IncludeBuiltins,
        PositionEncoding Encoding,
        string Name);

    public static string? Answer(Workspace workspace, string requestJson, out QueryError error)
    {
        if (!TryRead(requestJson, out Request request))
        {
            error = QueryError.BadRequest;
            return null;
        }

        ObjectTree tree = workspace.GetObjectTree();
        error = QueryError.None;

        switch (request.Query)
        {
            case "objectTree":
            {
                TreeNode? node = TreeQueryService.Browse(
                    tree, request.Path, request.Depth, request.IncludeBuiltins);

                if (node is null)
                    break;

                StringBuilder json = new();
                json.Append("{\"query\":\"objectTree\",\"node\":");
                WriteNode(json, node);
                json.Append('}');
                return json.ToString();
            }

            case "subtypesOf":
            {
                SubtypeListing? listing = TreeQueryService.Subtypes(
                    tree, request.Path, request.Limit, request.IncludeBuiltins);

                if (listing is null)
                    break;

                StringBuilder json = new();
                json.Append("{\"query\":\"subtypesOf\",\"path\":");
                SymbolJson.AppendString(json, request.Path);
                json.Append(",\"truncated\":").Append(listing.Truncated ? "true" : "false");
                json.Append(",\"types\":[");

                for (int i = 0; i < listing.Types.Count; i++)
                {
                    if (i > 0)
                        json.Append(',');

                    WriteNode(json, listing.Types[i]);
                }

                json.Append("]}");
                return json.ToString();
            }

            case "overriddenProc":
            {
                // The inverse of the `references` query's `override` kind: not what overrides
                // this, but what THIS overrides. A caller drawing a "go to overridden" affordance
                // needs the answer before it can decide whether to draw one at all.
                if (request.Name.Length == 0 || tree.Find(request.Path) is not { } subject)
                    break;

                StringBuilder json = new();
                json.Append("{\"query\":\"overriddenProc\",\"path\":");
                SymbolJson.AppendString(json, subject.Path.Text);
                json.Append(",\"name\":");
                SymbolJson.AppendString(json, request.Name);

                // A fresh declaration overrides nothing, and that is an ANSWER rather than an
                // error - it is what dm.exe's own no_parent warning reports on. So the call
                // succeeds with "overrides": false and no owner.
                if (tree.FindOverriddenProc(subject.Path, request.Name) is { } found)
                {
                    json.Append(",\"overrides\":true,\"owner\":");
                    SymbolJson.AppendString(json, found.Owner.Text);
                    json.Append(",\"builtin\":").Append(found.IsBuiltin ? "true" : "false");
                }
                else
                {
                    json.Append(",\"overrides\":false,\"owner\":\"\",\"builtin\":false");
                }

                json.Append('}');
                return json.ToString();
            }

            case "ancestorsOf":
            {
                TypeSymbol? type = tree.Find(request.Path);

                if (type is null)
                    break;

                StringBuilder json = new();
                json.Append("{\"query\":\"ancestorsOf\",\"path\":");
                SymbolJson.AppendString(json, type.Path.Text);
                json.Append(",\"ancestors\":[");

                // The chain the tree already holds, nearest first, self excluded — one call
                // instead of one objectTree round trip per level.
                bool first = true;

                foreach (TypeSymbol step in tree.InheritanceChain(type))
                {
                    if (ReferenceEquals(step, type))
                        continue;

                    if (!first)
                        json.Append(',');

                    first = false;

                    TreeNode? node = TreeQueryService.Browse(tree, step.Path.Text, depth: 0);

                    if (node is not null)
                        WriteNode(json, node);
                }

                json.Append("]}");
                return json.ToString();
            }

            case "references":
            {
                ReferenceListing listing = ReferenceService.Find(
                    tree,
                    workspace.GetProjectParses(),
                    request.Path,
                    request.Limit > 0 ? request.Limit : ReferenceService.DefaultLimit);

                StringBuilder json = new();
                json.Append("{\"query\":\"references\",\"path\":");
                SymbolJson.AppendString(json, request.Path);
                json.Append(",\"truncated\":").Append(listing.Truncated ? "true" : "false");
                json.Append(",\"references\":[");

                for (int i = 0; i < listing.References.Count; i++)
                {
                    if (i > 0)
                        json.Append(',');

                    Reference reference = listing.References[i];
                    SourceText? text = workspace.GetFileText(reference.File);

                    json.Append("{\"file\":");
                    SymbolJson.AppendString(json, reference.File);
                    json.Append(",\"kind\":");
                    SymbolJson.AppendString(json, reference.Kind switch
                    {
                        ReferenceKind.Write => "write",
                        ReferenceKind.Call => "call",
                        ReferenceKind.Override => "override",
                        _ => "read",
                    });
                    json.Append(",\"inside\":");
                    SymbolJson.AppendString(json, reference.Inside);

                    if (text is not null)
                    {
                        LinePosition start = text.GetLinePosition(reference.Span.Start, request.Encoding);
                        LinePosition end = text.GetLinePosition(reference.Span.End, request.Encoding);

                        json.Append(",\"startLine\":").Append(start.Line);
                        json.Append(",\"startChar\":").Append(start.Character);
                        json.Append(",\"endLine\":").Append(end.Line);
                        json.Append(",\"endChar\":").Append(end.Character);
                    }

                    json.Append('}');
                }

                json.Append("]}");
                return json.ToString();
            }

            case "members":
            {
                TypeMembers? members = TreeQueryService.Members(
                    tree, request.Path, request.Inherited, request.IncludeBuiltins);

                if (members is null)
                    break;

                StringBuilder json = new();
                json.Append("{\"query\":\"members\",\"path\":");
                SymbolJson.AppendString(json, members.Path);
                json.Append(",\"vars\":");
                WriteMembers(json, members.Vars);
                json.Append(",\"procs\":");
                WriteMembers(json, members.Procs);
                json.Append('}');
                return json.ToString();
            }

            default:
                error = QueryError.BadRequest;
                return null;
        }

        error = QueryError.NoSuchPath;
        return null;
    }

    /// <summary>
    /// Reads the request, filling in the defaults for anything the caller left out.
    /// </summary>
    /// <remarks>
    /// Unknown members are skipped rather than rejected, so a client written against a later version
    /// of the schema still gets an answer from an older library.
    /// </remarks>
    private static bool TryRead(string requestJson, out Request request)
    {
        request = default;

        if (string.IsNullOrWhiteSpace(requestJson))
            return false;

        string query = string.Empty;
        string path = "/";
        string memberName = string.Empty;
        int depth = TreeQueryService.DefaultDepth;
        int limit = TreeQueryService.DefaultSubtypeLimit;
        bool inherited = true;
        bool includeBuiltins = true;
        PositionEncoding encoding = PositionEncoding.Utf16;

        try
        {
            Utf8JsonReader reader = new(Encoding.UTF8.GetBytes(requestJson));

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return false;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    return false;

                string name = reader.GetString() ?? string.Empty;

                if (!reader.Read())
                    return false;

                switch (name)
                {
                    case "query":
                        query = reader.GetString() ?? string.Empty;
                        break;

                    case "path":
                        path = reader.GetString() ?? "/";
                        break;

                    // The member `overriddenProc` asks about. Named rather than folded into
                    // "path" because a proc's canonical spelling there is `/mob/Login()`, and a
                    // caller holding a type and a name should not have to assemble one.
                    case "name":
                        memberName = reader.GetString() ?? string.Empty;
                        break;

                    case "depth":
                        depth = reader.GetInt32();
                        break;

                    case "limit":
                        limit = reader.GetInt32();
                        break;

                    case "inherited":
                        inherited = reader.TokenType == JsonTokenType.True;
                        break;

                    case "includeBuiltins":
                        includeBuiltins = reader.TokenType == JsonTokenType.True;
                        break;

                    // References carry positions, and this call has no encoding parameter of its
                    // own, so the request says. UTF-16 when it does not, like LSP.
                    case "encoding":
                        encoding = reader.GetString() == "utf8"
                            ? PositionEncoding.Utf8
                            : PositionEncoding.Utf16;
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // A value of the wrong kind: "depth": "deep".
            return false;
        }

        if (query.Length == 0)
            return false;

        // A negative depth would recurse forever through Build's depth - 1.
        if (depth < 0)
            depth = 0;

        request = new Request(
            query, path, depth, limit, inherited, includeBuiltins, encoding, memberName);
        return true;
    }

    private static void WriteNode(StringBuilder json, TreeNode node)
    {
        json.Append("{\"path\":");
        SymbolJson.AppendString(json, node.Path);
        json.Append(",\"name\":");
        SymbolJson.AppendString(json, node.Name);
        json.Append(",\"declared\":").Append(node.Declared ? "true" : "false");
        json.Append(",\"builtin\":").Append(node.Builtin ? "true" : "false");

        json.Append(",\"parentType\":");

        if (node.ParentType is null)
            json.Append("null");
        else
            SymbolJson.AppendString(json, node.ParentType);

        json.Append(",\"childCount\":").Append(node.ChildCount);
        json.Append(",\"varCount\":").Append(node.VarCount);
        json.Append(",\"procCount\":").Append(node.ProcCount);
        json.Append(",\"children\":[");

        for (int i = 0; i < node.Children.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            WriteNode(json, node.Children[i]);
        }

        json.Append("]}");
    }

    private static void WriteMembers(StringBuilder json, IReadOnlyList<MemberEntry> members)
    {
        json.Append('[');

        for (int i = 0; i < members.Count; i++)
        {
            if (i > 0)
                json.Append(',');

            MemberEntry member = members[i];

            json.Append("{\"name\":");
            SymbolJson.AppendString(json, member.Name);
            json.Append(",\"detail\":");
            SymbolJson.AppendString(json, member.Detail);
            json.Append(",\"kind\":").Append((int)member.Kind);
            json.Append(",\"builtin\":").Append(member.Builtin ? "true" : "false");
            json.Append(",\"inherited\":").Append(member.Inherited ? "true" : "false");
            json.Append(",\"owner\":");
            SymbolJson.AppendString(json, member.Owner);
            json.Append(",\"file\":");
            SymbolJson.AppendString(json, member.File);
            json.Append('}');
        }

        json.Append(']');
    }
}
