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
}
