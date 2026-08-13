using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Dm.Core.Preprocessing;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>What to show when the pointer rests on a symbol.</summary>
public sealed class HoverResult
{
    /// <summary>A hover; a part with nothing to show is passed as an empty string.</summary>
    public HoverResult(string detail, string signature, string documentation, TextSpan span,
        string reference = "")
    {
        Detail = detail;
        Signature = signature;
        Reference = reference;
        Documentation = documentation;
        Span = span;
    }

    /// <summary>The resolved path, such as <c>/mob/proc/attack</c>.</summary>
    public string Detail { get; }

    /// <summary>The declaration as written, one line, trimmed.</summary>
    public string Signature { get; }

    /// <summary>Preceding <c>///</c> lines with their markers stripped, or empty.</summary>
    public string Documentation { get; }

    /// <summary>The token hovered, so a client can highlight exactly what it answered about.</summary>
    public TextSpan Span { get; }

    /// <summary>
    /// A link to the DM Reference section for a builtin, or empty.
    /// </summary>
    /// <remarks>
    /// Only for builtins the reference actually documents — see
    /// <see cref="DefinitionLocation.Reference"/>. A project's own symbols have a declaration to
    /// open instead, which is better than any link.
    /// </remarks>
    public string Reference { get; }
}

/// <summary>
/// Renders the declaration behind the symbol at a position.
/// </summary>
/// <remarks>
/// Hover is go-to-definition with a different presentation, so it resolves through
/// <see cref="DefinitionService"/> rather than repeating the work. Anything the two could disagree
/// about — which receiver a member belongs to, whether a path is relative — is then impossible to
/// get differently in one and not the other.
///
/// Where a symbol has several declarations, the nearest is rendered: hover is a glance, and a
/// reader who wants the whole override chain is asking for go-to-definition instead.
/// </remarks>
public static class HoverService
{
    /// <summary>The hover for the symbol at a position, or null when nothing resolves there.</summary>
    public static HoverResult? HoverAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default,
        MacroTable? macros = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        // Builtins included: hover is a glance at what a symbol IS, and a builtin is still
        // something, even though go-to-definition rightly has nowhere to send the caret.
        IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
            tree, document, line, character, encoding, cancellationToken, macros,
            includeBuiltins: true);

        if (found.Count == 0)
            return null;

        DefinitionLocation target = found[0];
        int offset = document.Text.GetOffset(new LinePosition(line, character), encoding);

        // A builtin match has no file to read a declaration from; its signature was rendered from
        // the symbol table instead, and nothing declares it so there is no doc comment.
        if (target.File.Length == 0)
            return new HoverResult(
                target.Detail, target.Signature, string.Empty, TokenSpanAt(document, offset),
                target.Reference);

        // The declaration lives in whatever file declared it, which is usually not this one.
        SourceText source = string.Equals(target.File, document.Path, StringComparison.OrdinalIgnoreCase)
            ? document.Text
            : ReadOrNull(target.File) ?? document.Text;

        string signature = string.Empty;
        string documentation = string.Empty;

        if (target.NameSpan.Start <= source.Length)
        {
            int declarationLine = source.GetLineIndex(target.NameSpan.Start);
            signature = source.GetLineText(declarationLine).Trim();
            documentation = DocComments.Above(source, declarationLine);
        }

        return new HoverResult(target.Detail, signature, documentation, TokenSpanAt(document, offset));
    }

    /// <summary>The span of the token under the cursor, for the client to highlight.</summary>
    /// <remarks>
    /// Resolution and highlight use the same lookup, so the span lit up is always the token the
    /// answer is about — a private copy of the boundary logic here carried the same off-by-one
    /// the definition side had.
    /// </remarks>
    private static TextSpan TokenSpanAt(Document document, int offset)
    {
        int index = DefinitionService.IndexAt(document.Lex.Tokens, offset);

        return index >= 0 ? document.Lex.Tokens[index].Span : new TextSpan(offset, 0);
    }

    private static SourceText? ReadOrNull(string file)
    {
        try
        {
            return SourceFileReader.Read(file);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            // A declaration we cannot re-read still has a path and a detail worth showing.
            return null;
        }
    }
}
