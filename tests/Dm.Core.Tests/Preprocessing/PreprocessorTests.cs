using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;

namespace Dm.Core.Tests.Preprocessing;

public class PreprocessorTests
{
    private static string Text(PreprocessResult result)
    {
        List<string> parts = new();
        foreach (ExpandedToken token in result.Tokens)
        {
            if (token.Kind is not (TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent))
                parts.Add(token.Text);
        }

        return string.Join(" ", parts);
    }

    [Fact]
    public void Expands_macros_across_the_project()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"defs.dm\"\n#include \"code.dm\"\n");
        temp.Write("defs.dm", "#define MAX 10\n");
        temp.Write("code.dm", "var/x = MAX\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / x = 10", Text(result));
    }

    /// <summary>
    /// The reason expansion is interleaved with the directive walk rather than deferred to the end
    /// of a file. Code above a redefinition must see the earlier value.
    /// </summary>
    [Fact]
    public void Each_run_of_code_uses_the_macro_state_that_applied_to_it()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n");
        temp.Write("a.dm", "#define X 1\nvar/a = X\n#undef X\n#define X 2\nvar/b = X\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / a = 1 var / b = 2", Text(result));
    }

    [Fact]
    public void Tokens_appear_in_compile_order_across_files()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "var/first = 1\n#include \"middle.dm\"\nvar/last = 3\n");
        temp.Write("middle.dm", "var/second = 2\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / first = 1 var / second = 2 var / last = 3", Text(result));
    }

    /// <summary>
    /// dm.exe RE-PROCESSES macro expansions for directives — `#define int #define` then
    /// `int DEAD 2` defines DEAD, which is how madridspy builds its whole status-flag
    /// vocabulary. Probed 2026-08-13. The directive line itself must not leak into the stream,
    /// and the use on the NEXT line of the same run must already see the macro.
    /// </summary>
    [Fact]
    public void A_macro_made_define_is_reprocessed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#define int #define\nint DEAD 2\nvar/x = DEAD\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / x = 2", Text(result));
    }

    [Fact]
    public void A_macro_made_undef_is_reprocessed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme",
            "#define U #undef\n#define FOO 2\nvar/a = FOO\nU FOO\nvar/b = FOO\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / a = 2 var / b = FOO", Text(result));
    }

    /// <summary>A macro whose body is a COMPLETE directive works from a bare line.</summary>
    [Fact]
    public void A_macro_carrying_a_whole_directive_is_reprocessed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#define MK #define DEAD 2\nMK\nvar/x = DEAD\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / x = 2", Text(result));
    }

    [Fact]
    public void Directive_lines_are_not_in_the_output()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#define A 1\nvar/x = A\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.DoesNotContain(result.Tokens, t => t.Kind is TokenKind.Hash or TokenKind.DirectiveName);
    }

    [Fact]
    public void Code_in_a_false_branch_is_not_emitted()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#ifdef NEVER\nvar/dead = 1\n#else\nvar/live = 2\n#endif\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / live = 2", Text(result));
    }

    [Fact]
    public void Comments_are_dropped()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "var/x = 1 // note\n/* block */\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.DoesNotContain(result.Tokens, t => t.Kind == TokenKind.Comment);
    }

    /// <summary>
    /// The source map. A token's characters live in the defining file, but it must be reported
    /// against the file and span where the macro was used.
    /// </summary>
    [Fact]
    public void Expanded_tokens_report_at_the_use_site_in_the_using_file()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"defs.dm\"\n#include \"code.dm\"\n");
        temp.Write("defs.dm", "#define GREETING \"hello\"\n");
        temp.Write("code.dm", "var/a = 1\nvar/b = GREETING\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        ExpandedToken expanded = result.Tokens.First(t => t.IsFromMacro);

        Assert.Equal("GREETING", expanded.Expansion!.Macro.Name);
        Assert.EndsWith("code.dm", expanded.ReportAt.Source.Path!);

        // Line 1, zero-based: the `var/b = GREETING` line, not the definition in defs.dm.
        Assert.Equal(1, expanded.ReportAt.Source.GetLinePosition(expanded.ReportAt.Span.Start).Line);
        Assert.Equal("GREETING", expanded.ReportAt.Source.ToString(expanded.ReportAt.Span));
    }

    [Fact]
    public void A_macro_defined_in_one_file_is_visible_to_later_files()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"b.dm\"\n");
        temp.Write("a.dm", "#define SHARED 42\n");
        temp.Write("b.dm", "var/x = SHARED\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains(result.Tokens, t => t.IsFromMacro && t.Text == "42");
    }

    /// <summary>
    /// A proc guarded by an inactive <c>#ifdef</c>, with live code after it and a sibling block
    /// below. The guarded line is the only thing at its depth.
    /// </summary>
    private const string SkippedRegion =
        "/mob\n\tproc\n\t\tf()\n#ifdef NOPE\n\t\t\tdeep()\n#endif\n\t\t\treturn 1\n\tverb\n\t\tg()\n\t\t\treturn 2\n";

    /// <summary>
    /// A skipped region takes its <c>Indent</c> tokens with it while the matching <c>Dedent</c>s
    /// survive in live code, so the stream pops levels it never pushed.
    /// </summary>
    /// <remarks>
    /// Silent and expensive: on a real 100-file project this moved 128 procs and 113 vars off their
    /// owning types with no diagnostic at all, because the result still parses — just at the wrong
    /// depth. Depth never going negative is the invariant that catches it.
    /// </remarks>
    [Fact]
    public void An_inactive_conditional_does_not_unbalance_indentation()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", SkippedRegion);

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        int depth = 0;
        int lowest = 0;

        foreach (ExpandedToken token in result.Tokens)
        {
            if (token.Kind == TokenKind.Indent)
                depth++;
            else if (token.Kind == TokenKind.Dedent)
                depth--;

            lowest = System.Math.Min(lowest, depth);
        }

        Assert.Equal(0, lowest);
        Assert.Equal(0, depth);
    }

    /// <summary>
    /// The newline after a skipped region's <c>#endif</c> must not collect the level debt.
    /// </summary>
    /// <remarks>
    /// Directive lines are layout-neutral in the lexer, so that newline still sits at the SKIPPED
    /// content's depth until the next live code line dedents. Levelling before it materialised an
    /// Indent that opened a block with nothing in it — "expected a declaration" reported on the
    /// <c>#endif</c> line of every inactive region whose content was indented. On /tg/station that
    /// was four directive-line diagnostics, and the misparse of <c>_logging.dm</c> behind one of
    /// them cost the declarations the binder's eleven <c>log_message</c> reports resolved against.
    /// </remarks>
    [Fact]
    public void The_newline_after_a_skipped_region_carries_no_level()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write(
            "code.dm",
            "#define FALSE 0\n#if FALSE\n/datum/never\n\tvar/x = 1\n#endif\n/datum/always\n\tvar/y = 2\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        // The surviving stream must open exactly one block: /datum/always's. A second Indent is
        // the skipped region's depth leaking through the directive's newline.
        int indents = result.Tokens.Count(t => t.Kind == TokenKind.Indent);

        Assert.Equal(1, indents);
        Assert.DoesNotContain(result.Tokens, t => t.Text == "never");
        Assert.Contains(result.Tokens, t => t.Text == "always");
    }

    /// <summary>
    /// <c>TRUE</c> and <c>FALSE</c> are built-in macros since BYOND 515: <c>#if TRUE</c> is taken,
    /// <c>#if FALSE</c> is silently not, and neither needs a define anywhere. tgstation defines
    /// neither and writes <c>#define MERGERS_DEBUG FALSE</c> + <c>#if MERGERS_DEBUG</c>.
    /// </summary>
    [Fact]
    public void True_and_false_are_predefined()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write(
            "code.dm",
            "#define NEVER_ON FALSE\n#if NEVER_ON\n/datum/never\n#endif\n#if TRUE\n/datum/always\n#endif\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Tokens, t => t.Text == "never");
        Assert.Contains(result.Tokens, t => t.Text == "always");
    }

    /// <summary>
    /// An <c>#include</c> inside an open bracket splices the file into the surrounding
    /// expression — tgstation's <c>ApiVersion()</c> wraps <c>new /datum/tgs_version(</c> around an
    /// included version literal. The spliced file's tokens must join the INCLUDING file's run, or
    /// the parent parses with a hole mid-expression and the fragment parses alone as a bogus
    /// declaration.
    /// </summary>
    [Fact]
    public void An_expression_position_include_splices_into_the_parent()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"api.dm\"\n");
        temp.Write(
            "api.dm",
            "/datum/ver\n\tvar/raw\n\n/datum/ver/New(raw_parameter)\n\traw = raw_parameter\n\n"
            + "/proc/apiver()\n\treturn new /datum/ver(\n\t\t#include \"ver.dm\"\n\t)\n");
        temp.Write("ver.dm", "\"5.11.0\"\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        // The spliced token sits inside the parent's stream, in place.
        Assert.Contains(result.Tokens, t => t.Text == "5.11.0");

        // The spliced file contributes no run of its own to parse as declarations, and the
        // parent's run parses whole.
        List<(string File, TokenSource Source)> files = PreprocessedSplitter.Split(result).ToList();

        Assert.DoesNotContain(files, f => f.File.EndsWith("ver.dm", StringComparison.Ordinal));

        (string _, TokenSource parent) = files.Single(
            f => f.File.EndsWith("api.dm", StringComparison.Ordinal));
        ParseResult parse = DeclarationParser.Parse(parent);

        Assert.Empty(parse.Diagnostics);

        // The include still appears in the file list, which is what dm.exe -l reports.
        Assert.Contains(result.Graph.Files, f => f.Path.EndsWith("ver.dm", StringComparison.Ordinal));
    }

    /// <summary>
    /// A <c>-D</c> flag decides which branch exists, so it has to be defined before the first line
    /// of the <c>.dme</c> is read.
    /// </summary>
    [Fact]
    public void An_injected_define_selects_the_conditional_branch()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", "#ifdef CBT\nvar/from_cbt = 1\n#else\nvar/without_cbt = 1\n#endif\n");

        string dme = Path.Combine(temp.Path, "game.dme");

        Assert.Contains("without_cbt", Text(Preprocessor.Run(dme)));
        Assert.Contains(
            "from_cbt",
            Text(Preprocessor.Run(dme, new Dm.Core.Includes.IncludeOptions { Defines = new[] { "CBT" } })));
    }

    /// <summary>
    /// A bare <c>-DNAME</c> defines it <b>empty</b>, not to <c>1</c>. Verified against dm.exe
    /// 516.1666, where <c>#if NAME == 1</c> then fails with "unexpected token: ==".
    /// </summary>
    [Fact]
    public void A_bare_injected_define_has_an_empty_body_not_one()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", "var/x = FLAG\n");

        PreprocessResult result = Preprocessor.Run(
            Path.Combine(temp.Path, "game.dme"),
            new Dm.Core.Includes.IncludeOptions { Defines = new[] { "FLAG" } });

        Assert.Equal("var / x =", Text(result).TrimEnd());
    }

    [Fact]
    public void An_injected_define_can_carry_a_value_or_parameters()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", "var/x = SIZE\nvar/y = DOUBLE(4)\n");

        PreprocessResult result = Preprocessor.Run(
            Path.Combine(temp.Path, "game.dme"),
            new Dm.Core.Includes.IncludeOptions { Defines = new[] { "SIZE=7", "DOUBLE(n)=((n)*2)" } });

        string text = Text(result);

        Assert.Contains("7", text);
        Assert.Contains("( ( 4 ) * 2 )", text);
    }

    /// <summary>What the imbalance actually costs: members land on the root instead of their type.</summary>
    [Fact]
    public void Declarations_after_an_inactive_conditional_keep_their_owner()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", SkippedRegion);

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));
        Dm.Core.Symbols.ObjectTree tree = new();

        foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(result))
            Dm.Core.Symbols.TypeTreeBuilder.AddFile(tree, file, DeclarationParser.Parse(source));

        Dm.Core.Symbols.TypeSymbol? mob = tree.Find("/mob");

        Assert.NotNull(mob);
        Assert.NotNull(mob!.FindProc("f"));

        // The one that used to end up on the root.
        Assert.NotNull(mob.FindProc("g"));
    }

    // -- #pragma syntax has to survive preprocessing -------------------------

    /// <summary>
    /// A C-style <c>switch</c>, which only parses under the pragma. Written as one string because
    /// the pragma and the body it governs are the whole point.
    /// </summary>
    private const string CSwitch =
        "#pragma push\n"
        + "#pragma syntax C switch\n"
        + "/proc/f(n)\n"
        + "\tswitch(n)\n"
        + "\t\tcase 1:\n"
        + "\t\t\treturn \"one\"\n"
        + "\t\tcase 2:\n"
        + "\t\t\treturn \"two\"\n"
        + "#pragma pop\n";

    /// <remarks>
    /// Every other directive is consumed by preprocessing. This one changes the grammar the parser
    /// reads the stream with, and the parser only ever sees the stream, so it has to survive as
    /// data. Without it the body above parses under DM's own switch grammar and reports errors on
    /// code dm.exe compiles with none.
    /// </remarks>
    [Fact]
    public void A_grammar_pragma_survives_into_the_stream()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", CSwitch);

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Contains("pragma", Text(result));
        Assert.Contains("syntax", Text(result));
    }

    [Fact]
    public void A_body_under_a_syntax_pragma_parses_from_the_preprocessed_stream()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", CSwitch);

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        foreach ((string _, TokenSource source) in PreprocessedSplitter.Split(result))
            Assert.Empty(DeclarationParser.Parse(source).Diagnostics);
    }

    /// <summary>
    /// The pragma is still not a declaration. It has to be stepped over, not parsed, and it carries
    /// no indentation of its own — so the proc under it is a sibling of what came before, not a
    /// child of the directive line.
    /// </summary>
    [Fact]
    public void The_kept_pragma_does_not_become_a_declaration()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", CSwitch);

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));
        Dm.Core.Symbols.ObjectTree tree = new();

        foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(result))
            Dm.Core.Symbols.TypeTreeBuilder.AddFile(tree, file, DeclarationParser.Parse(source));

        Assert.NotNull(tree.Find("/")!.FindProc("f"));
        Assert.Null(tree.Find("/pragma"));
        Assert.Null(tree.Find("/syntax"));
    }

    /// <summary>
    /// Pragmas the parser has no use for stay consumed. <c>multiple</c> is the preprocessor's own,
    /// and leaving it in the stream would put a stray directive in front of the parser for nothing.
    /// </summary>
    [Fact]
    public void A_pragma_the_parser_does_not_need_is_still_consumed()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", "#pragma multiple\nvar/x = 1\n");

        PreprocessResult result = Preprocessor.Run(Path.Combine(temp.Path, "game.dme"));

        Assert.Equal("var / x = 1", Text(result));
    }
}
