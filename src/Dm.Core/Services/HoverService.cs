using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>What to show when the pointer rests on a symbol.</summary>
public sealed class HoverResult
{
    public HoverResult(string detail, string signature, string documentation, TextSpan span)
    {
        Detail = detail;
        Signature = signature;
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
    public static HoverResult? HoverAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
            tree, document, line, character, encoding, cancellationToken);

        if (found.Count == 0)
            return null;

        DefinitionLocation target = found[0];

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

        int offset = document.Text.GetOffset(new LinePosition(line, character), encoding);

        return new HoverResult(target.Detail, signature, documentation, TokenSpanAt(document, offset));
    }

    /// <summary>The span of the token under the cursor, for the client to highlight.</summary>
    private static TextSpan TokenSpanAt(Document document, int offset)
    {
        foreach (Token token in document.Lex.Tokens)
        {
            if (token.Span.Start > offset)
                break;

            if (!token.Span.IsEmpty && offset >= token.Span.Start && offset <= token.Span.End)
                return token.Span;
        }

        return new TextSpan(offset, 0);
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
