using System;
using System.Collections.Generic;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>What a signature popup shows for the call enclosing a position.</summary>
public sealed class SignatureHelpResult
{
    internal SignatureHelpResult(string detail, string name, IReadOnlyList<string> parameters, int activeParameter)
    {
        Detail = detail;
        Name = name;
        Parameters = parameters;
        ActiveParameter = activeParameter;
    }

    /// <summary>The owning path, `/mob/proc/heal`-style.</summary>
    public string Detail { get; }

    /// <summary>The proc's name as called.</summary>
    public string Name { get; }

    /// <summary>Rendered parameters, types and defaults included: <c>amount as num</c>.</summary>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>Which parameter the position is inside, counting from zero.</summary>
    public int ActiveParameter { get; }

    /// <summary>The whole signature as one line; each parameter is a substring of it.</summary>
    public string Label => $"{Name}({string.Join(", ", Parameters)})";
}

/// <summary>
/// Which call encloses a position, whose proc it is, and which parameter the caret sits in.
/// </summary>
/// <remarks>
/// <para>
/// The enclosing call and the active parameter come from a bracket-frame scan over the
/// <b>tokens</b>, not the text and not the AST. Text is what dm-patch's upstream-requests doc
/// rules out — counting text commas means re-knowing that a comma inside a string is not a
/// separator, which the lexer already knows. The AST is the wrong tool for the opposite reason:
/// signature help runs mid-keystroke on <c>f(a,</c>, which the parser only sees through error
/// recovery, while the token stream is exact at every prefix.
/// </para>
/// <para>
/// The callee resolves exactly as completion and definition resolve it — shared code, so the
/// popup, the completion list and the definition jump cannot disagree about which proc a call
/// reaches. DM has no overloads, so there is one signature per site rather than a set to rank.
/// </para>
/// </remarks>
public static class SignatureHelpService
{
    /// <summary>The signature for the call enclosing a position, or null when no call encloses it.</summary>
    public static SignatureHelpResult? SignatureAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        PositionEncoding encoding = PositionEncoding.Utf16)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        int offset = document.Text.GetOffset(new LinePosition(line, character), encoding);
        IReadOnlyList<Token> tokens = document.Lex.Tokens;

        (int calleeIndex, int activeParameter) = EnclosingCall(tokens, offset);
        if (calleeIndex < 0)
            return null;

        string name = document.Text.ToString(tokens[calleeIndex].Span);

        TypeSymbol? owner = OwnerOf(tree, document, tokens, calleeIndex, offset);
        if (owner is null)
            return null;

        ProcSymbol? proc = tree.ResolveProc(owner, name);
        if (proc is null && !owner.Path.IsRoot)
            proc = tree.ResolveProc(tree.Root, name);

        if (proc is null)
            return null;

        string detail = owner.Path.IsRoot ? $"/proc/{proc.Name}" : $"{owner.Path.Text}/{proc.Name}";

        return new SignatureHelpResult(detail, proc.Name, proc.Parameters, activeParameter);
    }

    /// <summary>
    /// The innermost unclosed call at the offset: the callee's token index and how many commas
    /// its own frame has passed.
    /// </summary>
    /// <remarks>
    /// Every bracket pushes a frame so a comma only ever counts in the frame it belongs to —
    /// <c>f(list[a, b], |</c> is parameter one of <c>f</c>, not three. Only a frame whose <c>(</c>
    /// directly follows a name is a call; the rest (grouping, indexing, brace initialisers) are
    /// opaque to the popup but still fence their commas.
    /// </remarks>
    private static (int CalleeIndex, int ActiveParameter) EnclosingCall(IReadOnlyList<Token> tokens, int offset)
    {
        List<(int Callee, int Commas)> frames = new();

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            if (token.Span.End > offset)
                break;

            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                {
                    int previous = PreviousMeaningful(tokens, i);
                    bool isCall = previous >= 0 && CompletionService.IsName(tokens[previous].Kind);
                    frames.Add((isCall ? previous : -1, 0));
                    break;
                }

                case TokenKind.OpenBracket:
                case TokenKind.QuestionOpenBracket:
                case TokenKind.OpenBrace:
                    frames.Add((-1, 0));
                    break;

                case TokenKind.CloseParen:
                case TokenKind.CloseBracket:
                case TokenKind.CloseBrace:
                    if (frames.Count > 0)
                        frames.RemoveAt(frames.Count - 1);

                    break;

                case TokenKind.Comma:
                    if (frames.Count > 0)
                        frames[^1] = (frames[^1].Callee, frames[^1].Commas + 1);

                    break;
            }
        }

        for (int i = frames.Count - 1; i >= 0; i--)
        {
            if (frames[i].Callee >= 0)
                return (frames[i].Callee, frames[i].Commas);
        }

        return (-1, 0);
    }

    /// <summary>The type whose proc the callee names, resolved the way completion resolves it.</summary>
    private static TypeSymbol? OwnerOf(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int calleeIndex, int offset)
    {
        int previous = PreviousMeaningful(tokens, calleeIndex);

        if (previous >= 0 && tokens[previous].Kind
            is TokenKind.Dot or TokenKind.Colon or TokenKind.QuestionDot or TokenKind.QuestionColon)
        {
            return CompletionService.ResolveReceiver(tree, document, tokens, previous - 1, offset);
        }

        return CompletionService.EnclosingType(tree, document, offset) ?? tree.Root;
    }

    private static int PreviousMeaningful(IReadOnlyList<Token> tokens, int index)
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
}
