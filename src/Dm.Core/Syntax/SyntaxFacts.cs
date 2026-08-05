namespace Dm.Core.Syntax;

/// <summary>Token classifications the parsers share, so they cannot drift apart.</summary>
internal static class SyntaxFacts
{
    /// <summary>
    /// Statement keywords <c>dm.exe</c> accepts as a segment of a type path.
    /// </summary>
    /// <remarks>
    /// Probed one keyword per compilation unit on 516.1666 (results identical on 516.1686):
    /// declare <c>/datum/&lt;kw&gt;</c> with a var, then read that var through
    /// <c>var/datum/&lt;kw&gt;/x</c> — a clean compile of the declaration alone proves nothing.
    /// Thirteen are accepted, and real code uses one of them: tgstation declares
    /// <c>/datum/manipulator_task/cargo/dropoff_base/throw</c>.
    ///
    /// Rejected, for the record: <c>in</c>/<c>to</c> ("missing expression"), <c>as</c> (breaks at
    /// the use), <c>return</c>/<c>break</c>/<c>continue</c>/<c>del</c>/<c>new</c>/<c>goto</c>
    /// ("instruction not allowed here"), and <c>var</c>/<c>list</c>/<c>tmp</c>/<c>global</c>/
    /// <c>static</c>/<c>const</c>/<c>proc</c>/<c>verb</c>, which read as modifiers or group
    /// markers and declare no type at all.
    ///
    /// A keyword is still not a variable NAME: <c>var/throw = 1</c> is dm.exe's "missing left-hand
    /// argument to =", so a local-var reader may take these only as non-final segments.
    /// </remarks>
    internal static bool IsPathSegmentKeyword(TokenKind kind) => kind
        is TokenKind.KeywordThrow
        or TokenKind.KeywordSet
        or TokenKind.KeywordStep
        or TokenKind.KeywordIf
        or TokenKind.KeywordElse
        or TokenKind.KeywordFor
        or TokenKind.KeywordWhile
        or TokenKind.KeywordSwitch
        or TokenKind.KeywordCatch
        or TokenKind.KeywordTry
        or TokenKind.KeywordDo
        or TokenKind.KeywordSpawn
        or TokenKind.KeywordNull;
}
