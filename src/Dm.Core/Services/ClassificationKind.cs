namespace Dm.Core.Services;

/// <summary>
/// Colouring category for a span of source.
/// </summary>
/// <remarks>
/// <para>
/// These values cross the C ABI as plain integers, so they are assigned explicitly and must not be
/// renumbered. Add new members at the end.
/// </para>
/// <para>
/// This is a deliberately small set. A client maps each member to a colour once; anything finer
/// belongs in the semantic pass at M6, where the object tree is available.
/// </para>
/// </remarks>
public enum ClassificationKind
{
    /// <summary>Unclassified. Clients render with the default foreground.</summary>
    None = 0,

    /// <summary>A line or block comment.</summary>
    Comment = 1,

    /// <summary>A DM keyword.</summary>
    Keyword = 2,

    /// <summary>A plain name; the semantic pass may refine it.</summary>
    Identifier = 3,

    /// <summary>A numeric literal.</summary>
    Number = 4,

    /// <summary>String content and its delimiters, including <c>{"</c> and <c>"}</c>.</summary>
    String = 5,

    /// <summary>
    /// The <c>[</c> and <c>]</c> around an interpolation hole. Separated from
    /// <see cref="String"/> so the expression inside reads as code rather than as text.
    /// </summary>
    InterpolationDelimiter = 6,

    /// <summary>A resource literal in single quotes, such as <c>'icons/mob.dmi'</c>.</summary>
    Resource = 7,

    /// <summary>An operator.</summary>
    Operator = 8,

    /// <summary>Structural punctuation: brackets, commas, semicolons.</summary>
    Punctuation = 9,

    /// <summary>A <c>#</c> and its directive name.</summary>
    PreprocessorDirective = 10,

    /// <summary>Something the lexer could not recognise.</summary>
    Error = 11,

    // -- reserved ---------------------------------------------------------
    // Produced only once the object tree exists (M6). Declared now so the numbering the two IDE
    // clients bind against does not shift when semantic classification lands.

    /// <summary>A path segment naming a type. Requires the object tree.</summary>
    TypeName = 12,

    /// <summary>An identifier resolved to a proc. Requires the object tree.</summary>
    ProcName = 13,

    /// <summary>An identifier resolved to a var or parameter. Requires the object tree.</summary>
    VarName = 14,

    /// <summary>An identifier introduced by a macro. Requires the preprocessor.</summary>
    MacroName = 15,
}
