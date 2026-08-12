using System;
using System.Collections.Generic;

namespace Dm.Core.Preprocessing;

/// <summary>What a <c>#pragma warn|ignore|error</c> did to one warning name.</summary>
public enum PragmaLevel
{
    /// <summary>Reported as the compiler reports it. The default for every name.</summary>
    Warn = 0,

    /// <summary>Silenced from that point on.</summary>
    Ignore = 1,

    /// <summary>Promoted to a hard error from that point on.</summary>
    Error = 2,
}

/// <summary>
/// Which warnings a project has silenced, and where.
/// </summary>
/// <remarks>
/// <para>
/// <c>#pragma warn|ignore|error &lt;names&gt;</c> shares one vocabulary with the compiler's
/// <c>-ignore</c>/<c>-warn</c>/<c>-error</c> flags, and PLAN §8a is explicit about what that means
/// for us: a project that silences a warning in source expects it silenced by the editor too, and
/// reporting it anyway reads as our bug rather than as a setting. That only became load-bearing when
/// checks started carrying the compiler's own names — <c>new_name</c>, <c>no_parent</c>.
/// </para>
/// <para>
/// <b>The level is positional, which a cheaper model would get wrong in both directions.</b>
/// Compiler-verified on 516.1686 in one file: with <c>ignore</c>, then <c>warn</c>, then a
/// <c>push</c>/<c>ignore</c>/<c>pop</c> around a third site, dm.exe silences the first and third
/// occurrences and reports the second and fourth. A project-wide union of everything ever ignored
/// would miss two; a last-one-wins map would invent two. So changes are recorded in walk order with
/// their position, and a lookup replays them.
/// </para>
/// <para>
/// State flows through <b>include order</b> like the macro table — §8 verified that an ignore in the
/// first-included file silences an offending var in the second, and that swapping the two
/// <c>#include</c> lines changes the answer. So each file records the level map it was entered
/// with, and a query starts from that.
/// </para>
/// <para>
/// Numeric ids work as well as names and the reference documents neither: <c>#pragma ignore 3006</c>
/// silences <c>unused_var</c>. An unknown NAME is a hard compiler error while an unknown NUMBER is
/// silently accepted, so an id this table does not know is simply dropped — matching a project that
/// can carry <c>#pragma ignore 9999</c> forever and never learn it does nothing.
/// </para>
/// </remarks>
public sealed class PragmaLevels
{
    /// <summary>
    /// Every numeric warning id the compiler has, mapped to its <c>#pragma</c> name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compiler's own warning table, read out of <c>byondcore.dll</c>'s <c>.rdata</c> — PLAN
    /// §8a. 30 live ids; 2007, 3003 and 4002 are retired gaps and are absent here for that reason
    /// rather than by oversight.
    /// </para>
    /// <para>
    /// This is a TRANSLATION table, not a list of what we honour — whether a warning is honoured is
    /// decided by whether we ever raise it. It carried only the four we emit until 2026-08-12, on
    /// the reasoning that listing more would imply more; the flaw in that showed up the day
    /// <c>unused_label</c> shipped, when its id had to be added in the same commit or a project
    /// already silencing 3005 by number would have started seeing it. A complete table removes a
    /// step somebody has to remember, and an id for a warning we never raise suppresses nothing.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> IdsToNames = new(StringComparer.Ordinal)
    {
        ["1001"] = "bad_cache_file",
        ["2001"] = "value_changed",
        ["2002"] = "empty_expression",
        ["2003"] = "filter_rightmost",
        ["2004"] = "anything_no_list",
        ["2005"] = "accidental_assign",
        ["2006"] = "list_without_filter",
        ["2008"] = "empty_else",
        ["3001"] = "list_double_init",
        ["3002"] = "override_before_def",
        ["3004"] = "unknown_var",
        ["3005"] = "unused_label",
        ["3006"] = "unused_var",
        ["3007"] = "no_effect",
        ["3008"] = "set_at_top",
        ["3009"] = "call_by_ref",
        ["3010"] = "empty_switch",
        ["3011"] = "var_before_def",
        ["3012"] = "loop_checks",
        ["3013"] = "no_parent",
        ["3014"] = "empty_parent",
        ["3015"] = "requires_version",
        ["4001"] = "old_new_syntax",
        ["4003"] = "redundant_waitfor",
        ["4004"] = "map_format",
        ["4005"] = "new_name",
        ["4006"] = "call_ext",
        ["5001"] = "init_proc",
        ["5002"] = "frequent_call",
        ["6001"] = "lint_type_mismatch",
    };

    private readonly Dictionary<string, Dictionary<string, PragmaLevel>> _entryLevels =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<(int Offset, string Name, PragmaLevel Level)>> _changes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Stack<Dictionary<string, PragmaLevel>> _pushed = new();

    private Dictionary<string, PragmaLevel> _current = new(StringComparer.Ordinal);

    /// <summary>Whether anything at all was set, so a consumer can skip the lookup entirely.</summary>
    public bool IsEmpty => _changes.Count == 0;

    /// <summary>Records the level map a file is walked with. Called as the walk enters it.</summary>
    public void EnterFile(string file)
        => _entryLevels[file] = new Dictionary<string, PragmaLevel>(_current, StringComparer.Ordinal);

    /// <summary><c>#pragma push</c> — the level map is restored by the matching pop.</summary>
    public void Push()
        => _pushed.Push(new Dictionary<string, PragmaLevel>(_current, StringComparer.Ordinal));

    /// <summary>
    /// <c>#pragma pop</c>. An unmatched pop is ignored rather than throwing.
    /// </summary>
    /// <remarks>
    /// The restore has to be recorded as a positional change like any other, or the replay in
    /// <see cref="IsIgnored"/> never sees it and everything after the pop keeps the pushed level.
    /// That is not hypothetical: it was the one miss left when this was first wired up, on a probe
    /// where dm.exe reports the site after the pop and we did not.
    /// </remarks>
    public void Pop(string file, int offset)
    {
        if (_pushed.Count == 0)
            return;

        Dictionary<string, PragmaLevel> restored = _pushed.Pop();

        HashSet<string> names = new(_current.Keys, StringComparer.Ordinal);
        names.UnionWith(restored.Keys);

        foreach (string name in names)
        {
            PragmaLevel was = _current.TryGetValue(name, out PragmaLevel c) ? c : PragmaLevel.Warn;
            PragmaLevel now = restored.TryGetValue(name, out PragmaLevel r) ? r : PragmaLevel.Warn;

            if (was != now)
                Record(file, offset, name, now);
        }

        _current = restored;
    }

    /// <summary>
    /// Records one <c>warn|ignore|error</c> for a name or numeric id, at the directive's position.
    /// </summary>
    public void Set(string file, int offset, string nameOrId, PragmaLevel level)
    {
        if (!IdsToNames.TryGetValue(nameOrId, out string? name))
            name = nameOrId;

        _current[name] = level;
        Record(file, offset, name, level);
    }

    private void Record(string file, int offset, string name, PragmaLevel level)
    {
        if (!_changes.TryGetValue(file, out List<(int, string, PragmaLevel)>? list))
        {
            list = new List<(int, string, PragmaLevel)>();
            _changes[file] = list;
        }

        list.Add((offset, name, level));
    }

    /// <summary>
    /// Whether <paramref name="name"/> is silenced at this point in this file.
    /// </summary>
    /// <remarks>
    /// Starts from the level the file was entered with and replays the changes made in it up to
    /// the offset asked about, which is what makes a <c>push</c>/<c>ignore</c>/<c>pop</c> around one
    /// declaration silence that declaration and nothing after it.
    /// </remarks>
    public bool IsIgnored(string? file, int offset, string name)
    {
        if (file is null)
            return false;

        PragmaLevel level = PragmaLevel.Warn;

        if (_entryLevels.TryGetValue(file, out Dictionary<string, PragmaLevel>? entry)
            && entry.TryGetValue(name, out PragmaLevel entered))
        {
            level = entered;
        }

        if (_changes.TryGetValue(file, out List<(int Offset, string Name, PragmaLevel Level)>? list))
        {
            foreach ((int at, string changed, PragmaLevel to) in list)
            {
                if (at > offset)
                    break;

                if (string.Equals(changed, name, StringComparison.Ordinal))
                    level = to;
            }
        }

        return level == PragmaLevel.Ignore;
    }
}
