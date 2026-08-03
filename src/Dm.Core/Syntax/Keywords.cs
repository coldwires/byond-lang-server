using System.Collections.Generic;

namespace Dm.Core.Syntax;

/// <summary>
/// Maps identifier text to a keyword token kind.
/// </summary>
/// <remarks>
/// DM has very few genuinely reserved words. Notably absent here are <c>proc</c> and <c>verb</c>,
/// which are ordinary segments in the type tree — <c>mob/proc/attack()</c> and
/// <c>mob.proc.attack()</c> declare the same proc (PLAN.md §4a). Several entries below are
/// contextual rather than reserved (<c>in</c>, <c>to</c>, <c>step</c>, <c>as</c>, <c>set</c>), so
/// the parser accepts a keyword token anywhere an identifier is legal.
/// </remarks>
internal static class Keywords
{
    private static readonly Dictionary<string, TokenKind> Map = new(System.StringComparer.Ordinal)
    {
        ["var"] = TokenKind.KeywordVar,
        ["new"] = TokenKind.KeywordNew,
        ["del"] = TokenKind.KeywordDel,
        ["if"] = TokenKind.KeywordIf,
        ["else"] = TokenKind.KeywordElse,
        ["for"] = TokenKind.KeywordFor,
        ["while"] = TokenKind.KeywordWhile,
        ["do"] = TokenKind.KeywordDo,
        ["switch"] = TokenKind.KeywordSwitch,
        ["return"] = TokenKind.KeywordReturn,
        ["break"] = TokenKind.KeywordBreak,
        ["continue"] = TokenKind.KeywordContinue,
        ["spawn"] = TokenKind.KeywordSpawn,
        ["goto"] = TokenKind.KeywordGoto,
        ["try"] = TokenKind.KeywordTry,
        ["catch"] = TokenKind.KeywordCatch,
        ["throw"] = TokenKind.KeywordThrow,
        ["set"] = TokenKind.KeywordSet,
        ["in"] = TokenKind.KeywordIn,
        ["to"] = TokenKind.KeywordTo,
        ["step"] = TokenKind.KeywordStep,
        ["as"] = TokenKind.KeywordAs,
        ["null"] = TokenKind.KeywordNull,
        ["src"] = TokenKind.KeywordSrc,
        ["usr"] = TokenKind.KeywordUsr,
        ["world"] = TokenKind.KeywordWorld,
        ["global"] = TokenKind.KeywordGlobal,
    };

    public static TokenKind Lookup(string text)
        => Map.TryGetValue(text, out TokenKind kind) ? kind : TokenKind.Identifier;

    public static bool IsKeyword(string text) => Map.ContainsKey(text);
}
