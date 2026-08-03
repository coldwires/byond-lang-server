using System;
using System.Collections.Generic;

namespace Dm.Core.Preprocessing;

/// <summary>
/// The set of macros currently defined, as the preprocessor moves through the include order.
/// </summary>
/// <remarks>
/// <para>
/// Order-sensitive by nature: whether a macro is defined at a given line depends on every file
/// processed before it. This is also what makes DM's <c>var.thing.T</c> ambiguity (PLAN.md §4a)
/// include-order dependent.
/// </para>
/// <para>
/// <see cref="StateHash"/> exists for M9. Re-running every downstream file after an edit is only
/// avoidable if we can cheaply tell whether a file's exit state actually changed.
/// </para>
/// </remarks>
public sealed class MacroTable
{
    private readonly Dictionary<string, MacroDefinition> _macros = new(StringComparer.Ordinal);
    private int _stateHash;

    /// <summary>Number of macros currently defined.</summary>
    public int Count => _macros.Count;

    /// <summary>
    /// A hash of every define and undef applied so far, in order. Two files that arrive at the same
    /// hash have the same macro environment for anything downstream.
    /// </summary>
    public int StateHash => _stateHash;

    public IReadOnlyCollection<string> Names => _macros.Keys;

    public void Define(MacroDefinition macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        _macros[macro.Name] = macro;
        Mix(macro.Name.GetHashCode(StringComparison.Ordinal));
        Mix(macro.Body.Count);
    }

    public bool Undefine(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        bool removed = _macros.Remove(name);
        Mix(~name.GetHashCode(StringComparison.Ordinal));
        return removed;
    }

    public bool IsDefined(string name) => _macros.ContainsKey(name);

    public bool TryGet(string name, out MacroDefinition macro) => _macros.TryGetValue(name, out macro!);

    /// <summary>
    /// Seeds the compiler's predefined macros.
    /// </summary>
    /// <remarks>
    /// Only these five are real preprocessor macros. <c>__TYPE__</c>, <c>__PROC__</c> and
    /// <c>__IMPLIED_TYPE__</c> look similar but the reference states plainly that "the preprocessor
    /// doesn't handle it directly" — they resolve at the parser layer with type and proc context, so
    /// defining them here would expand them wrongly.
    ///
    /// <c>__FILE__</c> and <c>__LINE__</c> are positional: their value depends on the use site, not
    /// the definition site, so they are handled by the expander rather than stored here.
    ///
    /// <c>DM_VERSION</c> is not constant across a build — <c>#pragma compatibility</c> lowers it.
    /// </remarks>
    public void SeedPredefined(int dmVersion = 516, int dmBuild = 1666)
    {
        Define(MacroBuilder.Number("DM_VERSION", dmVersion));
        Define(MacroBuilder.Number("DM_BUILD", dmBuild));
    }

    /// <summary>Order-sensitive mix, so define/undef sequences that differ produce different hashes.</summary>
    private void Mix(int value)
        => _stateHash = unchecked((_stateHash * 31) + value);
}
