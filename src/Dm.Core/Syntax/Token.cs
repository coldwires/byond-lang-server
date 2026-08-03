using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>
/// One lexical token: a kind and the span it occupies in the source.
/// </summary>
/// <remarks>
/// Tokens carry no text of their own. The span indexes the originating <see cref="SourceText"/>,
/// which keeps tokens small and means a token can always be traced back to exactly the bytes a
/// client pushed. Use <see cref="SourceText.ToString(TextSpan)"/> to materialise the text.
///
/// Whitespace produces no token; it is the gap between spans. Comments do produce tokens, because
/// classification and doc-comment extraction both need them.
/// </remarks>
public readonly struct Token
{
    public Token(TokenKind kind, TextSpan span)
    {
        Kind = kind;
        Span = span;
    }

    public TokenKind Kind { get; }

    public TextSpan Span { get; }

    public bool IsEndOfFile => Kind == TokenKind.EndOfFile;

    /// <summary>True for tokens the parser ignores: comments and layout markers it has consumed.</summary>
    public bool IsComment => Kind == TokenKind.Comment;

    public override string ToString() => $"{Kind}{Span}";
}
