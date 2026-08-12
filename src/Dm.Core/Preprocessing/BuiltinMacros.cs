using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Seeds the <c>#define</c> constants <c>stddef.dm</c> gives every project.
/// </summary>
/// <remarks>
/// <para>
/// <c>dm.exe</c> compiles <c>stddef.dm</c> ahead of all source, so <c>NORTH</c>, <c>ICON_ADD</c>,
/// <c>SOUND_STREAM</c>, <c>ASSERT</c> and 195 others are defined before a project's first line. We do
/// not compile that file — <c>builtins.txt</c> stands in for it — so without this a bare
/// <c>NORTH</c> resolves nowhere.
/// </para>
/// <para>
/// Each entry is parsed by wrapping it back into a <c>#define</c> line and running the same lex,
/// directive scan and <see cref="MacroDefinition.Parse"/> a real directive goes through, exactly as
/// <see cref="CommandLineDefine"/> does for a <c>-D</c> flag. Rebuilding the line is cheaper than
/// carrying a second parser that would drift, and it is what makes the four function-like ones work
/// without a special case.
/// </para>
/// <para>
/// Seeded before any file and before the <c>-D</c> flags, which is the compiler's own order: a
/// project may redefine one of these, and the later definition wins.
/// </para>
/// </remarks>
public static class BuiltinMacros
{
    /// <summary>Defines every bundled constant into <paramref name="table"/>.</summary>
    public static void Seed(MacroTable table)
    {
        System.ArgumentNullException.ThrowIfNull(table);

        foreach (string define in Builtins.Macros)
        {
            if (Parse(define) is { } macro)
                table.Define(macro);
        }
    }

    /// <summary>Parses one table entry — the text that followed <c>#define</c>.</summary>
    private static MacroDefinition? Parse(string define)
    {
        SourceText text = SourceText.From($"#define {define}\n", "<stddef>");
        LexResult lex = Lexer.Lex(text);
        IReadOnlyList<Directive> directives = DirectiveScanner.Scan(lex);

        if (directives.Count == 0 || directives[0].Kind != DirectiveKind.Define)
            return null;

        // Diagnostics are discarded rather than surfaced: this is our own bundled table, so a
        // malformed entry is a generator bug to fix rather than something to report against the
        // user's project, where it would appear at a file they cannot open.
        return MacroDefinition.Parse(lex, directives[0], new List<Diagnostic>());
    }
}
