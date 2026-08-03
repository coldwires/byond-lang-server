using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Records that a token came out of a macro, and where that macro was used.
/// </summary>
/// <remarks>
/// <see cref="Parent"/> chains outward through nested expansions, so a token produced three macros
/// deep can be traced back to the line the outermost one was written on. Without this, every
/// diagnostic and every go-to-definition in macro-heavy code lands on the macro's definition rather
/// than on the code the author is looking at.
/// </remarks>
public sealed class MacroExpansion
{
    internal MacroExpansion(MacroDefinition macro, SourceText useSource, TextSpan useSpan, MacroExpansion? parent)
    {
        Macro = macro;
        UseSource = useSource;
        UseSpan = useSpan;
        Parent = parent;
        Depth = (parent?.Depth ?? 0) + 1;
    }

    public MacroDefinition Macro { get; }

    /// <summary>File containing the invocation.</summary>
    public SourceText UseSource { get; }

    /// <summary>Span of the invocation, which is where a diagnostic should point.</summary>
    public TextSpan UseSpan { get; }

    /// <summary>The expansion this one occurred inside, or null at the outermost level.</summary>
    public MacroExpansion? Parent { get; }

    public int Depth { get; }

    /// <summary>
    /// Walks out to the invocation the user actually wrote, which is the only position worth
    /// showing in an editor.
    /// </summary>
    public MacroExpansion Outermost
    {
        get
        {
            MacroExpansion current = this;
            while (current.Parent is not null)
                current = current.Parent;

            return current;
        }
    }

    public override string ToString() => $"{Macro.Name} @{UseSpan}";
}

/// <summary>
/// A token after preprocessing, carrying enough to map it back to source.
/// </summary>
public readonly struct ExpandedToken
{
    public ExpandedToken(TokenKind kind, SourceText source, TextSpan span, MacroExpansion? expansion)
    {
        Kind = kind;
        Source = source;
        Span = span;
        Expansion = expansion;
    }

    public TokenKind Kind { get; }

    /// <summary>
    /// The text this token's characters live in. For a token from a macro body that is the defining
    /// file; for one synthesised by <c>#</c> or <c>##</c> it is a generated buffer.
    /// </summary>
    public SourceText Source { get; }

    public TextSpan Span { get; }

    /// <summary>Null when the token was written verbatim at this point in the file.</summary>
    public MacroExpansion? Expansion { get; }

    public bool IsFromMacro => Expansion is not null;

    public string Text => Source.ToString(Span);

    /// <summary>
    /// Where an editor should point for this token: the outermost macro invocation if it came from
    /// one, otherwise its own position.
    /// </summary>
    public (SourceText Source, TextSpan Span) ReportAt
        => Expansion is null ? (Source, Span) : (Expansion.Outermost.UseSource, Expansion.Outermost.UseSpan);

    public override string ToString() => IsFromMacro ? $"{Kind}({Text}) via {Expansion}" : $"{Kind}({Text})";
}
