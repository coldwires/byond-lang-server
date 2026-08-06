using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>One place a symbol is declared.</summary>
public sealed class DefinitionLocation
{
    public DefinitionLocation(string file, TextSpan span, TextSpan nameSpan, string detail)
    {
        File = file;
        Span = span;
        NameSpan = nameSpan;
        Detail = detail;
    }

    public string File { get; }

    /// <summary>The whole declaration.</summary>
    public TextSpan Span { get; }

    /// <summary>The name alone, which is where a caret should land.</summary>
    public TextSpan NameSpan { get; }

    /// <summary>What was found, for a client that shows a picker: <c>/mob/proc/attack</c>.</summary>
    public string Detail { get; }

    public override string ToString() => $"{Detail} at {File}{NameSpan}";
}

/// <summary>
/// Finds where the symbol under a position is declared.
/// </summary>
/// <remarks>
/// <para>
/// Returns a <b>list</b>, never a single location, because DM genuinely declares one symbol in
/// several places: a type is reopened across files, and a proc has an override chain. Collapsing
/// that to "the" definition would pick one arbitrarily and hide the rest, which is exactly what a
/// reader needs to see in a codebase that overrides heavily.
/// </para>
/// <para>
/// Resolution reuses <see cref="CompletionService"/>'s receiver logic rather than repeating it, so
/// go-to-definition and completion agree about what a receiver is by construction. If one of them is
/// wrong about a shape, both are — which is easier to notice and fix than a silent disagreement.
/// </para>
/// </remarks>
public static class DefinitionService
{
    /// <summary>Locations for the symbol at a position, or empty when nothing resolves.</summary>
    public static IReadOnlyList<DefinitionLocation> DefinitionAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        cancellationToken.ThrowIfCancellationRequested();

        int offset = document.Text.GetOffset(new LinePosition(line, character), encoding);
        IReadOnlyList<Token> tokens = document.Lex.Tokens;

        int index = IndexAt(tokens, offset);
        if (index < 0)
            return Array.Empty<DefinitionLocation>();

        Token token = tokens[index];
        if (!CompletionService.IsName(token.Kind))
            return Array.Empty<DefinitionLocation>();

        string name = document.Text.ToString(token.Span);

        // A path segment first: `/obj/item` under the caret goes to the type, and that reading wins
        // over treating `item` as a member name.
        if (TypeAt(tree, document, tokens, index) is { } type)
            return FromType(type);

        TokenKind previous = index > 0 ? PreviousMeaningful(tokens, index) : TokenKind.EndOfFile;

        // A member, with the receiver resolved exactly as completion resolves it.
        if (previous is TokenKind.Dot or TokenKind.Colon or TokenKind.QuestionDot or TokenKind.QuestionColon)
        {
            int operatorIndex = IndexOfPrevious(tokens, index);
            TypeSymbol? receiver = CompletionService.ResolveReceiver(
                tree, document, tokens, operatorIndex - 1, offset);

            return receiver is null
                ? Array.Empty<DefinitionLocation>()
                : FromMember(tree, receiver, name);
        }

        // A bare name: the enclosing type and what it inherits, then globals on the root.
        if (CompletionService.EnclosingType(tree, document, offset) is { } enclosing)
        {
            IReadOnlyList<DefinitionLocation> found = FromMember(tree, enclosing, name);
            if (found.Count > 0)
                return found;
        }

        return FromMember(tree, tree.Root, name);
    }

    /// <summary>The token containing the offset, rather than the one before it.</summary>
    /// <remarks>
    /// Completion asks what precedes the caret; this asks what the caret is <i>on</i>, so a caret
    /// anywhere inside a name resolves that name. A caret on a boundary belongs to the token
    /// STARTING there: hovering the first character of `hp` in `t.hp` must answer about `hp`, not
    /// the dot — which an inclusive end matched first, so definition and hover returned nothing on
    /// the first character of every member. The tier-2 service fixture caught it on its first run.
    /// A caret with nothing on its right still falls back to the token it just left, so `hp|`
    /// resolves. Shared with <see cref="HoverService"/>, which highlights the token it answers
    /// about — two copies of this boundary carried the same bug.
    /// </remarks>
    internal static int IndexAt(IReadOnlyList<Token> tokens, int offset)
    {
        int endsHere = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            if (token.Span.Start > offset)
                break;

            if (token.Span.IsEmpty)
                continue;

            if (offset < token.Span.End)
                return i;

            if (token.Span.End == offset)
                endsHere = i;
        }

        return endsHere;
    }

    private static TokenKind PreviousMeaningful(IReadOnlyList<Token> tokens, int index)
    {
        int found = IndexOfPrevious(tokens, index);
        return found < 0 ? TokenKind.EndOfFile : tokens[found].Kind;
    }

    private static int IndexOfPrevious(IReadOnlyList<Token> tokens, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (tokens[i].Kind is not (TokenKind.Comment or TokenKind.Newline
                or TokenKind.Indent or TokenKind.Dedent))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Renders an owner and member as a path, without doubling the separator at the root.
    /// </summary>
    /// <remarks>
    /// The root's path text is already <c>/</c>, so a global proc would otherwise read
    /// <c>//do_hack()</c>.
    /// </remarks>
    private static string Describe(TypeSymbol owner, string name)
        => owner.Path.IsRoot ? $"/{name}" : $"{owner.Path.Text}/{name}";

    /// <summary>Resolves a path the caret sits inside, or null when this is not one.</summary>
    private static TypeSymbol? TypeAt(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int index)
    {
        int start = index;

        while (start > 0 && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
               && start - 2 >= 0 && CompletionService.IsName(tokens[start - 2].Kind))
        {
            start -= 2;
        }

        // A path needs a leading separator. Without one this is member access or a bare name, and
        // both are handled elsewhere.
        if (start == 0 || tokens[start - 1].Kind is not (TokenKind.Slash or TokenKind.Dot))
            return null;

        List<string> segments = new();
        for (int i = start; i <= index; i += 2)
            segments.Add(document.Text.ToString(tokens[i].Span));

        bool relative = tokens[start - 1].Kind == TokenKind.Dot
            && (start - 2 < 0 || !CompletionService.IsName(tokens[start - 2].Kind));

        if (!relative)
            return tree.Find(TypePath.FromSegments(segments));

        TypePath anchor = CompletionService
            .EnclosingType(tree, document, tokens[index].Span.Start)?.Path ?? TypePath.Root;

        return RelativePath.Resolve(tree, anchor, segments) is { } found ? tree.Find(found) : null;
    }

    private static IReadOnlyList<DefinitionLocation> FromType(TypeSymbol type)
    {
        List<DefinitionLocation> locations = new();

        // A type is legitimately declared in several files; every one of them is a definition.
        foreach (DeclarationSite site in type.Sites)
            locations.Add(new DefinitionLocation(site.File, site.Span, site.NameSpan, type.Path.Text));

        return locations;
    }

    /// <summary>
    /// Finds a member on a type or anything it inherits, returning every declaration of it.
    /// </summary>
    /// <remarks>
    /// For a proc that means the whole override chain, nearest first — which is the order a reader
    /// wants, since the nearest is what a call actually reaches.
    /// </remarks>
    private static IReadOnlyList<DefinitionLocation> FromMember(
        ObjectTree tree, TypeSymbol start, string name)
    {
        List<DefinitionLocation> locations = new();

        foreach (TypeSymbol step in tree.InheritanceChain(start))
        {
            if (step.FindProc(name) is { } proc)
            {
                foreach (DeclarationSite site in proc.Sites)
                    locations.Add(new DefinitionLocation(site.File, site.Span, site.NameSpan, Describe(step, name) + "()"));
            }

            if (step.FindVar(name) is { } variable && !variable.IsBuiltin)
            {
                DeclarationSite site = variable.Site;
                locations.Add(new DefinitionLocation(site.File, site.Span, site.NameSpan, Describe(step, name)));
            }
        }

        // The whole chain, not just the nearest match. An override and the declaration it overrides
        // are both places the reader may want, and `InheritanceChain` already yields nearest first —
        // which is the useful order, since the nearest is what a call actually reaches.
        return locations;
    }
}
