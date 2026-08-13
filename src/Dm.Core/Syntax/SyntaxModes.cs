using System;
using System.Collections.Generic;

namespace Dm.Core.Syntax;

/// <summary>
/// Tracks the <c>#pragma syntax</c> state while a file is parsed.
/// </summary>
/// <remarks>
/// <para>
/// <c>#pragma syntax C for</c> and <c>#pragma syntax C switch</c> change the grammar from the line
/// they appear on, so the mode is <b>position-dependent state</b> rather than a per-file constant:
/// a proc above the pragma parses under the old grammar and one below it under the new. The pragma
/// also lives at file level while the grammar it changes is used inside proc bodies, so the state is
/// shared between <see cref="DeclarationParser"/> and <see cref="StatementParser"/> rather than
/// owned by either.
/// </para>
/// <para>
/// <c>#pragma push</c> and <c>#pragma pop</c> scope it. Pragmas do not propagate into included
/// libraries, so each file starts from the default.
/// </para>
/// </remarks>
internal sealed class SyntaxModes
{
    private readonly List<(bool CFor, bool CSwitch)> _saved = new();

    /// <summary>True when a comma chains statements in a <c>for</c> header instead of separating clauses.</summary>
    public bool CFor { get; private set; }

    /// <summary>True when <c>switch</c> arms are <c>case v:</c> / <c>default:</c> and fall through.</summary>
    public bool CSwitch { get; private set; }

    /// <summary>True when a <c>#pragma pop</c> had no matching push, which is worth reporting.</summary>
    public bool PopWithoutPush { get; private set; }

    /// <summary>Applies a directive's words, which are everything after the <c>#</c>.</summary>
    public void Apply(IReadOnlyList<string> words)
    {
        PopWithoutPush = false;

        if (words is null || words.Count == 0
            || !string.Equals(words[0], "pragma", StringComparison.Ordinal))
        {
            return;
        }

        if (words.Count >= 2 && string.Equals(words[1], "push", StringComparison.Ordinal))
        {
            _saved.Add((CFor, CSwitch));
            return;
        }

        if (words.Count >= 2 && string.Equals(words[1], "pop", StringComparison.Ordinal))
        {
            if (_saved.Count == 0)
            {
                PopWithoutPush = true;
                return;
            }

            (CFor, CSwitch) = _saved[^1];
            _saved.RemoveAt(_saved.Count - 1);
            return;
        }

        if (words.Count >= 4
            && string.Equals(words[1], "syntax", StringComparison.Ordinal)
            && string.Equals(words[2], "C", StringComparison.Ordinal))
        {
            if (string.Equals(words[3], "for", StringComparison.Ordinal))
                CFor = true;
            else if (string.Equals(words[3], "switch", StringComparison.Ordinal))
                CSwitch = true;
        }
    }
}
