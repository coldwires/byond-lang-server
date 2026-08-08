using System;
using System.Collections.Generic;

namespace Dm.Core.Diagnostics;

/// <summary>
/// Diagnostics we raise on purpose that <c>dm.exe</c> has no opinion about.
/// </summary>
/// <remarks>
/// <para>
/// Everything else we report on code the compiler accepts is a bug — that is the rule M11 is built
/// on. These are the exceptions, and they are listed rather than tolerated, because a column that is
/// never empty is one people stop reading. Adding to this should feel expensive.
/// </para>
/// <para>
/// One home for the set, since both the differential harness and the fixture tests have to agree
/// about it: two copies drifted apart the moment the second was written, and the symptom was a
/// fixture that compiles clean failing a zero-invented check for a warning we meant to emit.
/// </para>
/// </remarks>
public static class DeliberateDivergences
{
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["DM0102"] = "duplicate include - the compiler ignores the repeat silently, we surface it",
        ["DM0300"] = "proc block inside a var block - compiles clean and declares nothing",
        ["DM0301"] = "`= x in list(...)` on a local - a value restriction that reads as a membership test",
        ["DM0302"] = "member missing on a `new /path(...)` receiver - compiles clean, raises at runtime",
    };

    /// <summary>Whether this id is one we emit knowing the compiler will not.</summary>
    public static bool Contains(string id) => Reasons.ContainsKey(id);

    /// <summary>Why it is here, for the harness to print beside the count.</summary>
    public static bool TryGetReason(string id, out string reason) => Reasons.TryGetValue(id, out reason!);

    public static IReadOnlyCollection<string> Ids => Reasons.Keys;
}
