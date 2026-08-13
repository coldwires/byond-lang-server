namespace Dm.Core.Syntax;

/// <summary>
/// Lexical categories produced by <see cref="Lexer"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Slash"/> and <see cref="Dot"/> stay distinct here. Mid-path they mean the same thing
/// (PLAN.md §4a), but the lexer cannot tell a path from member access or division — <c>a.b</c>,
/// <c>/a.b</c> and <c>a / b</c> are all legal and differ only by context. Folding happens in the
/// parser.
/// </para>
/// <para>
/// Interpolated strings are emitted as a flat run rather than one nested token:
/// <c>StringStart, StringText, InterpolationStart, …expression…, InterpolationEnd, StringText,
/// StringEnd</c>. The expression inside participates in normal lexing, which is what
/// <c>"[src.name] hit [target.name]"</c> requires.
/// </para>
/// </remarks>
internal enum TokenKind
{
    /// <summary>A character the lexer does not recognise. Never thrown on; always reported.</summary>
    Unknown = 0,

    EndOfFile,

    // -- layout ------------------------------------------------------------

    Newline,
    Indent,
    Dedent,

    /// <summary>Line comment or block comment. Block comments nest in DM, unlike C.</summary>
    Comment,

    // -- literals ----------------------------------------------------------

    Number,

    /// <summary>Opening quote of a string, either <c>"</c> or <c>{"</c>.</summary>
    StringStart,

    /// <summary>Literal text inside a string.</summary>
    StringText,

    /// <summary>The <c>[</c> beginning an interpolation hole.</summary>
    InterpolationStart,

    /// <summary>The <c>]</c> ending an interpolation hole.</summary>
    InterpolationEnd,

    /// <summary>Closing quote of a string, either <c>"</c> or <c>"}</c>.</summary>
    StringEnd,

    /// <summary>A resource literal in single quotes, such as <c>'icons/mob.dmi'</c>.</summary>
    Resource,

    // -- names -------------------------------------------------------------

    Identifier,

    // Reserved and contextual keywords. DM has few true reserved words: `proc` and `verb` are
    // ordinary path segments, and `in`/`to`/`step`/`as`/`set` are contextual. The parser accepts a
    // keyword token wherever an identifier is legal.
    KeywordVar,
    KeywordNew,
    KeywordDel,
    KeywordIf,
    KeywordElse,
    KeywordFor,
    KeywordWhile,
    KeywordDo,
    KeywordSwitch,
    KeywordReturn,
    KeywordBreak,
    KeywordContinue,
    KeywordSpawn,
    KeywordGoto,
    KeywordTry,
    KeywordCatch,
    KeywordThrow,
    KeywordSet,
    KeywordIn,
    KeywordTo,
    KeywordStep,
    KeywordAs,
    KeywordNull,
    KeywordSrc,
    KeywordUsr,
    KeywordWorld,
    KeywordGlobal,

    // -- preprocessor ------------------------------------------------------

    /// <summary>A <c>#</c> beginning a directive, at the start of a line.</summary>
    Hash,

    /// <summary>Directive name following a <c>#</c>, such as <c>define</c> or <c>include</c>.</summary>
    DirectiveName,

    /// <summary>
    /// The free-text remainder of a <c>#warn</c> or <c>#error</c> line.
    /// </summary>
    /// <remarks>
    /// The compiler prints these verbatim and does not tokenize them, so apostrophes and unbalanced
    /// quotes are legal. Verified against dm.exe: <c>#warn this won't work and "unbalanced</c>
    /// compiles with 0 errors.
    /// </remarks>
    DirectiveText,

    // -- punctuation -------------------------------------------------------

    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    OpenBrace,
    CloseBrace,
    Comma,
    Semicolon,
    Colon,
    ColonColon,
    Question,
    QuestionDot,
    QuestionColon,
    QuestionOpenBracket,

    /// <summary>A single <c>.</c>. Path separator, member access, or the return-value variable.</summary>
    Dot,

    /// <summary>A <c>..</c>, as in the parent call <c>..()</c>.</summary>
    DotDot,

    // -- operators ---------------------------------------------------------

    Slash,
    Plus,
    Minus,
    Star,
    StarStar,
    Percent,

    /// <summary>The <c>%%</c> modulo variant. Documented in the DM Reference under /operator.</summary>
    PercentPercent,

    /// <summary>Three-way comparison, <c>&lt;=&gt;</c>.</summary>
    Spaceship,

    /// <summary>
    /// The <c>:=</c> operator. Distinct from <c>:</c> followed by <c>=</c>; it also appears as the
    /// name of an overloadable operator.
    /// </summary>
    ColonAssign,

    Assign,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    PercentPercentAssign,
    StarStarAssign,
    AndAssign,
    OrAssign,
    XorAssign,
    LeftShiftAssign,
    RightShiftAssign,
    AndAndAssign,
    OrOrAssign,

    Equal,
    NotEqual,
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    EquivalentTo,
    NotEquivalentTo,

    Not,
    AndAnd,
    OrOr,
    Amp,
    Pipe,
    Caret,
    Tilde,
    LeftShift,
    RightShift,

    PlusPlus,
    MinusMinus,
}
