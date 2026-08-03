using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core;

/// <summary>
/// One source file plus everything derived from it.
/// </summary>
/// <remarks>
/// Immutable. An edit produces a new <see cref="Document"/> rather than mutating this one, which
/// makes cache invalidation fall out for free — there is no stale derived state to clear because
/// the whole object is replaced.
///
/// Derived results are computed on first use and then held. Classification needs a whole-file lex
/// (a multiline string can begin thousands of lines above the visible range), so caching it here is
/// what keeps scrolling from re-lexing.
/// </remarks>
public sealed class Document
{
    private LexResult? _lex;

    internal Document(string path, SourceText text, bool fromBuffer)
    {
        Path = path;
        Text = text;
        IsFromBuffer = fromBuffer;
    }

    /// <summary>Normalised absolute path.</summary>
    public string Path { get; }

    public SourceText Text { get; }

    /// <summary>
    /// True when a client pushed this text. A pushed buffer is authoritative and disk is never
    /// consulted for it — see PLAN.md §4.
    /// </summary>
    public bool IsFromBuffer { get; }

    /// <summary>Token stream for the whole file. Computed once.</summary>
    public LexResult Lex => _lex ??= Lexer.Lex(Text);
}
