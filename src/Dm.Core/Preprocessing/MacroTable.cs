using System;
using System.Collections.Generic;
using Dm.Core.Syntax;

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

    /// <summary>Names of every macro currently defined.</summary>
    public IReadOnlyCollection<string> Names => _macros.Keys;

    internal void Define(MacroDefinition macro)
    {
        ArgumentNullException.ThrowIfNull(macro);

        _macros[macro.Name] = macro;
        Mix(macro.Name.GetHashCode(StringComparison.Ordinal));
        Mix(macro.Body.Count);
        Mix(macro.Parameters?.Count ?? -1);

        // The body's CONTENT, not only its length. Two macros of the same name whose bodies are the
        // same size — `#define THING /obj/first` against `#define THING /obj/second` — are a
        // different program, and a hash that cannot tell them apart lets anything keyed on this
        // state reuse work that no longer applies. Caught by a test that edited exactly that.
        foreach (Token token in macro.Body)
        {
            Mix((int)token.Kind);
            Mix(macro.Source.ToString(token.Span).GetHashCode(StringComparison.Ordinal));
        }
    }

    /// <summary>Removes a macro; false if it was not defined. Mixes into the state hash either way.</summary>
    public bool Undefine(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        bool removed = _macros.Remove(name);
        Mix(~name.GetHashCode(StringComparison.Ordinal));
        return removed;
    }

    /// <summary>True if the name is defined here — in a workspace's table, at the end of the walk.</summary>
    public bool IsDefined(string name) => _macros.ContainsKey(name);

    internal bool TryGet(string name, out MacroDefinition macro) => _macros.TryGetValue(name, out macro!);

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
    ///
    /// <c>TRUE</c> and <c>FALSE</c> are built-in since BYOND 515 and behave as ordinary macros:
    /// compiler-verified on 516.1666 that <c>#if TRUE</c> is taken, <c>#if FALSE</c> is silently
    /// not, <c>#ifdef TRUE</c> is defined, and the runtime values are 1 and 0. tgstation never
    /// defines either name and writes <c>#define MERGERS_DEBUG FALSE</c> + <c>#if MERGERS_DEBUG</c>,
    /// which without this seed reported "'FALSE' is not defined".
    /// </remarks>
    public void SeedPredefined(int dmVersion = 516, int dmBuild = 1666)
    {
        Define(MacroBuilder.Number("DM_VERSION", dmVersion));
        Define(MacroBuilder.Number("DM_BUILD", dmBuild));
        Define(MacroBuilder.Number("TRUE", 1));
        Define(MacroBuilder.Number("FALSE", 0));
    }

    /// <summary>Order-sensitive mix, so define/undef sequences that differ produce different hashes.</summary>
    private void Mix(int value)
        => _stateHash = unchecked((_stateHash * 31) + value);
}
