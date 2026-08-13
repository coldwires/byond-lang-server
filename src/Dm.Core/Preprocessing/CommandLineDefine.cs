using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Builds a macro from a <c>dm.exe -D</c> style specification.
/// </summary>
/// <remarks>
/// <para>
/// A project built with <c>-D</c> flags is a different program from the same project built without
/// them, because the flags decide which <c>#ifdef</c> branches exist. /tg/station is the standard
/// example: its build passes <c>-DCBT</c>, and <c>map_switch.dm</c> keys off it to choose which map
/// compiles. Analysing without the flags means analysing code the build never sees, and missing
/// code it does.
/// </para>
/// <para>
/// The spelling matches the compiler, verified against 516.1666: <c>NAME</c>, <c>NAME=value</c> and
/// the function-like <c>FN(x)=body</c> all work, and a bare <c>NAME</c> defines it <b>empty</b>
/// rather than to <c>1</c> — <c>#if NAME == 1</c> then fails with "unexpected token: ==". Mapping a
/// spec onto the equivalent <c>#define</c> line and running it through the ordinary directive path
/// keeps all three forms consistent with what <c>#define</c> itself accepts.
/// </para>
/// </remarks>
internal static class CommandLineDefine
{
    /// <summary>
    /// Parses one <c>-D</c> specification, or returns null if it does not name a macro.
    /// </summary>
    public static MacroDefinition? Parse(string? specification, List<Diagnostic>? diagnostics = null)
    {
        if (string.IsNullOrWhiteSpace(specification))
            return null;

        string spec = specification.Trim();

        // `NAME=value` becomes `#define NAME value`; `NAME` becomes `#define NAME`, which is an
        // empty body. The first `=` is the split, so a body containing `=` survives intact.
        int equals = spec.IndexOf('=');
        string line = equals < 0
            ? $"#define {spec}"
            : $"#define {spec[..equals]} {spec[(equals + 1)..]}";

        SourceText text = SourceText.From(line + "\n", $"<define:{spec}>");
        LexResult lex = Lexer.Lex(text);
        IReadOnlyList<Directive> directives = DirectiveScanner.Scan(lex);

        List<Diagnostic> collected = diagnostics ?? new List<Diagnostic>();

        if (directives.Count == 0 || directives[0].Kind != DirectiveKind.Define)
        {
            collected.Add(Diagnostic.Error(
                "DM0130", new TextSpan(0, 0), $"'{spec}' is not a valid macro definition"));
            return null;
        }

        return MacroDefinition.Parse(lex, directives[0], collected);
    }

    /// <summary>Parses several specifications, skipping any that are unusable.</summary>
    public static List<MacroDefinition> ParseAll(
        IEnumerable<string>? specifications, List<Diagnostic>? diagnostics = null)
    {
        List<MacroDefinition> macros = new();

        if (specifications is null)
            return macros;

        foreach (string specification in specifications)
        {
            if (Parse(specification, diagnostics) is { } macro)
                macros.Add(macro);
        }

        return macros;
    }
}
