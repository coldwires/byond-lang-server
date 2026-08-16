namespace Dm.Core.Syntax;

/// <summary>Token classifications the parsers share, so they cannot drift apart.</summary>
internal static class SyntaxFacts
{
    /// <summary>
    /// How deep an expression, a statement block or a declaration block may nest before the
    /// parser stops descending and reports <c>DM0205</c> instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The parsers are recursive descent, and a recursive parser on an unbounded input is a stack
    /// overflow — which the .NET runtime cannot catch and which, across the C ABI, kills the host.
    /// Measured 2026-08-16: 5,000 nested parentheses overflowed the Debug build at ~900 levels and
    /// the Release build between 1,200 and 2,000; nested calls, indexes, interpolations, ternaries,
    /// indented and braced statement blocks and indented type blocks all overflowed at 5,000. The
    /// host's thread stack is not ours to size, so the limit has to sit well under the smallest one
    /// seen (1 MB, Windows' default main thread).
    /// </para>
    /// <para>
    /// 256 is under every measured overflow by three times or more, and no compiling program is
    /// deeper: <c>dm.exe</c> itself dies (exit 127, no summary) between 1,040 and 1,060 nested
    /// groups, so a limit anywhere below that invents nothing on code the compiler accepts. The
    /// three parsers count separately — a declaration 256 deep holding a statement 256 deep holding
    /// an expression 256 deep is still under a megabyte at the measured frame sizes.
    /// </para>
    /// </remarks>
    internal const int MaxNesting = 256;

    /// <summary>The message <c>DM0205</c> carries; one wording for all three parsers.</summary>
    internal const string NestingMessage = "nesting deeper than 256 levels; the parser stops here";

    /// <summary>
    /// The output methods that are RESERVED WORDS: legal only as the right side of <c>&lt;&lt;</c>,
    /// an error anywhere else, and not declarable as a proc even on a type.
    /// </summary>
    /// <remarks>
    /// Probed 2026-08-16 on 516.1687, one assignment per candidate: <c>var/x = message("a")</c>,
    /// <c>link</c>, <c>run</c> and <c>ftp</c> are each <i>"output method has no effect here"</i>,
    /// and <c>/proc/message()</c>, <c>/proc/link()</c>, <c>/proc/run()</c>, <c>/proc/ftp()</c> and
    /// <c>/datum/proc/link()</c> are each <i>"invalid proc name: reserved word"</i>. The other
    /// output procs — <c>browse</c>, <c>output</c>, <c>load_resource</c>, <c>browse_rsc</c> — are
    /// documented procs and behave differently (the compiler reads a standalone
    /// <c>browse("a")</c> as a label, of all things), so they are not in this set. <c>message</c>
    /// is the one the reference never documents, which is why it was in no table until now.
    /// </remarks>
    internal static bool IsOutputMethod(string name)
        => name is "message" or "link" or "run" or "ftp";

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
    /// <summary>
    /// Every input type an <c>as</c> clause accepts, in the reference's own order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A CLOSED vocabulary, so a completion list after <c>as</c> is exact rather than a guess. The
    /// reference documents that these exist and that <c>|</c> combines them, but never enumerates
    /// them in one place, so the list was established by compiling one verb parameter per
    /// candidate — <c>bogus_xyz</c> as the control that must fail.
    /// </para>
    /// <para>
    /// <b>It is not the type system, and three results say so.</b> <c>as datum</c>, <c>as list</c>
    /// and <c>as client</c> are all REJECTED, while <c>as movable</c> and <c>as atom</c> are
    /// accepted. No rule about types predicts that split: these are the filters DreamSeeker knows
    /// how to prompt for, which is a different question from what a value can be. Verified on
    /// 516.1686.
    /// </para>
    /// </remarks>
    internal static readonly string[] InputTypes =
    {
        "anything", "null", "text", "message", "num", "icon", "sound", "file",
        "key", "color", "command_text", "password", "mob", "obj", "turf", "area",
        "movable", "atom",
    };

    /// <summary>
    /// The names a <c>set</c> statement accepts. A name outside this list is dm.exe's plain
    /// <i>"X: undefined var"</i> on the <c>set</c> line.
    /// </summary>
    /// <remarks>
    /// Probed 2026-08-13, all ten in one verb AND the same ten in a global proc — both compile
    /// clean on 516.1686, so there is no verb/proc split to model. <c>loop_checks</c>, which the
    /// reference once documented, now errors as undefined (probe <c>w3012_loop_checks</c>), so it
    /// is deliberately not here.
    /// </remarks>
    internal static readonly string[] SetNames =
    {
        "name", "desc", "category", "hidden", "instant", "invisibility",
        "popup_menu", "background", "waitfor", "src",
    };

    /// <summary>
    /// The var modifier words — a header or path segment that modifies rather than types. Both
    /// parsers hold their own sets for their own contexts; this one serves the tree builder,
    /// which must skip them when reading a group header's segments as the children's TYPE.
    /// </summary>
    internal static bool IsVarModifier(string word)
        => word is "const" or "final" or "global" or "static" or "tmp";

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
