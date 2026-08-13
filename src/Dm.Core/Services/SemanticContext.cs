using System;
using System.Collections.Generic;
using Dm.Core.Symbols;
using Dm.Core.Syntax;

namespace Dm.Core.Services;

/// <summary>
/// What classification needs to tell a type from a proc from a var from a macro.
/// </summary>
/// <remarks>
/// <para>
/// M2 deliberately feeds classification straight off the lexer, because highlighting must stay
/// instant: it runs on every scroll and every keystroke. That is still true — this only ever
/// <b>refines</b> a span the lexical pass already produced, and never adds or moves one.
/// </para>
/// <para>
/// The object tree is optional for the same reason. Building it is a whole-project walk, and a
/// paint path must not trigger one, so a caller passes the tree only if it already has it. Without
/// a tree the rules that need no lookup still apply; type names light up once something else has
/// built one.
/// </para>
/// <para>
/// Every rule here is deliberately conservative. Over-colouring is worse than under-colouring in a
/// highlighter — a wrong colour reads as a bug in the analyzer, while a missing one reads as
/// "semantic highlighting hasn't reached that yet".
/// </para>
/// </remarks>
public sealed class SemanticContext
{
    private readonly HashSet<string> _macros;

    /// <summary>Context from whatever the caller already has; both parts are optional.</summary>
    public SemanticContext(ObjectTree? tree = null, IReadOnlyCollection<string>? macros = null)
    {
        Tree = tree;
        _macros = macros is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(macros, StringComparer.Ordinal);
    }

    /// <summary>The object tree, when the caller already had one. Null is normal on a paint path.</summary>
    public ObjectTree? Tree { get; }

    /// <summary>True if the name was in the macro set this context was built with.</summary>
    public bool IsMacro(string name) => _macros.Contains(name);

    /// <summary>
    /// Refines one identifier, or returns null to leave the lexical classification alone.
    /// </summary>
    /// <param name="lex">The file's lex, which supplies both the tokens and their text.</param>
    /// <param name="index">Index of the identifier being classified.</param>
    internal ClassificationKind? Refine(LexResult lex, int index)
    {
        IReadOnlyList<Token> tokens = lex.Tokens;
        string text = lex.GetText(tokens[index]);

        // A macro use is unmistakable: the name is defined, and at this layer it has not been
        // expanded away. Checked first because a macro can expand to anything at all.
        if (_macros.Contains(text))
            return ClassificationKind.MacroName;

        TokenKind previous = PreviousMeaningful(tokens, index);
        TokenKind next = NextMeaningful(tokens, index);

        bool afterMemberAccess = previous
            is TokenKind.Dot or TokenKind.Colon or TokenKind.QuestionDot or TokenKind.QuestionColon;

        // A call is a call whatever it is called on. `(` directly after a name is the one shape that
        // needs no lookup at all.
        if (next == TokenKind.OpenParen)
            return ClassificationKind.ProcName;

        // `a.b` with no parentheses is a var read. This is the common case in real code and it
        // costs nothing to be sure of.
        if (afterMemberAccess)
            return ClassificationKind.VarName;

        // Everything below wants the tree.
        if (Tree is null)
            return null;

        // A path segment, resolved from the leading `/`. Only absolute paths qualify: a bare `mob`
        // is far more often a variable than the type, and guessing wrong there would miscolour
        // ordinary code.
        if (previous == TokenKind.Slash && ResolvePathAt(lex, index) is not null)
            return ClassificationKind.TypeName;

        return null;
    }

    /// <summary>
    /// Walks back to the leading <c>/</c> and resolves the whole path, or null if it is not one.
    /// </summary>
    private TypePath? ResolvePathAt(LexResult lex, int index)
    {
        IReadOnlyList<Token> tokens = lex.Tokens;
        List<int> segments = new();
        int probe = index;

        // Back up over `name /` pairs to the start of the path.
        while (probe >= 0 && IsNameLike(tokens[probe].Kind))
        {
            segments.Add(probe);

            if (probe - 1 < 0 || tokens[probe - 1].Kind is not (TokenKind.Slash or TokenKind.Dot))
                break;

            probe -= 2;
        }

        if (segments.Count == 0)
            return null;

        // Absolute only: the token before the first segment's separator must not be a name, or this
        // is member access rather than a path.
        int first = segments[^1];
        if (first - 1 < 0 || tokens[first - 1].Kind is not (TokenKind.Slash or TokenKind.Dot))
            return null;

        if (first - 2 >= 0 && IsNameLike(tokens[first - 2].Kind))
            return null;

        List<string> names = new(segments.Count);
        for (int i = segments.Count - 1; i >= 0; i--)
            names.Add(lex.GetText(tokens[segments[i]]));

        TypePath path = TypePath.FromSegments(names);
        return Tree!.Find(path) is null ? null : path;
    }

    /// <summary>
    /// <c>proc</c> and <c>verb</c> are ordinary identifiers in DM, not keywords (PLAN.md §4a), so
    /// they need no special case here. <c>var</c> is a keyword and does appear mid-path.
    /// </summary>
    private static bool IsNameLike(TokenKind kind)
        => kind is TokenKind.Identifier or TokenKind.KeywordVar;

    private static TokenKind PreviousMeaningful(IReadOnlyList<Token> tokens, int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            if (!IsTrivia(tokens[i].Kind))
                return tokens[i].Kind;
        }

        return TokenKind.EndOfFile;
    }

    private static TokenKind NextMeaningful(IReadOnlyList<Token> tokens, int index)
    {
        for (int i = index + 1; i < tokens.Count; i++)
        {
            if (!IsTrivia(tokens[i].Kind))
                return tokens[i].Kind;
        }

        return TokenKind.EndOfFile;
    }

    /// <summary>
    /// Newlines are <b>not</b> trivia here.
    /// </summary>
    /// <remarks>
    /// A name at the end of a line and a `(` at the start of the next are not a call, and treating
    /// the newline as skippable would colour the two as one.
    /// </remarks>
    private static bool IsTrivia(TokenKind kind) => kind is TokenKind.Comment;
}
