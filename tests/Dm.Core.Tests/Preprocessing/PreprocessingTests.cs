using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Preprocessing;

public class DirectiveScannerTests
{
    private static IReadOnlyList<Directive> Scan(string source)
        => DirectiveScanner.Scan(Lexer.Lex(SourceText.From(source)));

    [Theory]
    [InlineData("#define A 1", DirectiveKind.Define)]
    [InlineData("#undef A", DirectiveKind.Undef)]
    [InlineData("#if 1", DirectiveKind.If)]
    [InlineData("#ifdef A", DirectiveKind.Ifdef)]
    [InlineData("#ifndef A", DirectiveKind.Ifndef)]
    [InlineData("#elif 1", DirectiveKind.Elif)]
    [InlineData("#else", DirectiveKind.Else)]
    [InlineData("#endif", DirectiveKind.Endif)]
    [InlineData("#include \"a.dm\"", DirectiveKind.Include)]
    [InlineData("#warn text", DirectiveKind.Warn)]
    [InlineData("#error text", DirectiveKind.Error)]
    [InlineData("#pragma multiple", DirectiveKind.Pragma)]
    public void Classifies_each_directive(string source, object expected)
    {
        Assert.Equal((DirectiveKind)expected, Assert.Single(Scan(source)).Kind);
    }

    [Fact]
    public void An_unrecognised_directive_is_still_reported()
    {
        Directive directive = Assert.Single(Scan("#nonsense foo"));

        Assert.Equal(DirectiveKind.Unknown, directive.Kind);
        Assert.Equal("nonsense", directive.Name);
    }

    /// <summary>
    /// A bare <c>#</c> is stringification inside a macro body, not a directive. The lexer only
    /// emits DirectiveName at the start of a logical line, and the scanner relies on that.
    /// </summary>
    [Fact]
    public void A_hash_without_a_directive_name_is_not_a_directive()
    {
        Assert.Empty(Scan("var/s = \"[#c]\""));
    }

    [Fact]
    public void A_directive_inside_a_comment_is_not_found()
    {
        Assert.Empty(Scan("/*\n#include \"a.dm\"\n*/\n"));
    }

    [Fact]
    public void Arguments_stop_at_the_end_of_the_line()
    {
        LexResult lex = Lexer.Lex(SourceText.From("#define A 1\n/mob/b\n"));
        Directive directive = Assert.Single(DirectiveScanner.Scan(lex));

        for (int i = directive.ArgumentStart; i < directive.ArgumentEnd; i++)
            Assert.NotEqual(TokenKind.Newline, lex.Tokens[i].Kind);

        Assert.Equal(2, directive.ArgumentEnd - directive.ArgumentStart);
    }

    /// <summary>
    /// A column-0 <c>#endif</c> inside an indented block must not absorb the Dedent tokens that
    /// belong to the surrounding code.
    /// </summary>
    [Fact]
    public void Layout_tokens_are_not_swallowed_into_a_directive()
    {
        LexResult lex = Lexer.Lex(SourceText.From("/mob\n\tproc/f()\n\t\tvar/a = 1\n#endif\n"));
        Directive directive = Assert.Single(DirectiveScanner.Scan(lex));

        for (int i = directive.ArgumentStart; i < directive.ArgumentEnd; i++)
            Assert.NotEqual(TokenKind.Dedent, lex.Tokens[i].Kind);
    }

    [Fact]
    public void Finds_every_directive_in_order()
    {
        IReadOnlyList<Directive> directives = Scan("#ifdef A\n#define B 1\n#else\n#undef B\n#endif\n");

        Assert.Equal(
            new[] { DirectiveKind.Ifdef, DirectiveKind.Define, DirectiveKind.Else, DirectiveKind.Undef, DirectiveKind.Endif },
            directives.Select(d => d.Kind).ToArray());
    }
}

public class MacroDefinitionTests
{
    private static MacroDefinition? Parse(string source, out List<Diagnostic> diagnostics)
    {
        LexResult lex = Lexer.Lex(SourceText.From(source));
        Directive directive = DirectiveScanner.Scan(lex)[0];
        diagnostics = new List<Diagnostic>();
        return MacroDefinition.Parse(lex, directive, diagnostics);
    }

    [Fact]
    public void Parses_an_object_like_macro()
    {
        MacroDefinition macro = Parse("#define MAX 10", out List<Diagnostic> diagnostics)!;

        Assert.Empty(diagnostics);
        Assert.Equal("MAX", macro.Name);
        Assert.False(macro.IsFunctionLike);
        Assert.Single(macro.Body);
    }

    [Fact]
    public void Parses_a_function_like_macro()
    {
        MacroDefinition macro = Parse("#define SQR(X) ((X)*(X))", out List<Diagnostic> diagnostics)!;

        Assert.Empty(diagnostics);
        Assert.True(macro.IsFunctionLike);
        Assert.Equal(new[] { "X" }, macro.Parameters!.ToArray());
    }

    /// <summary>
    /// Verified against dm.exe: with a space before the paren the macro is object-like and expands
    /// to <c>(x)</c>; calling it as <c>A(1)</c> fails. Without the space it is function-like and a
    /// bare <c>B</c> fails.
    /// </summary>
    [Fact]
    public void A_space_before_the_paren_makes_it_object_like()
    {
        MacroDefinition spaced = Parse("#define A (x)", out _)!;
        MacroDefinition tight = Parse("#define B(x) x", out _)!;

        Assert.False(spaced.IsFunctionLike);
        Assert.True(tight.IsFunctionLike);

        // The paren is part of the replacement text for the object-like form.
        Assert.Equal(3, spaced.Body.Count);
    }

    [Fact]
    public void Parses_multiple_parameters()
    {
        MacroDefinition macro = Parse("#define F(a, b, c) a+b+c", out List<Diagnostic> diagnostics)!;

        Assert.Empty(diagnostics);
        Assert.Equal(new[] { "a", "b", "c" }, macro.Parameters!.ToArray());
    }

    [Fact]
    public void Parses_a_variadic_macro()
    {
        MacroDefinition macro = Parse("#define V(x, rest...) f(x, rest)", out List<Diagnostic> diagnostics)!;

        Assert.Empty(diagnostics);
        Assert.True(macro.IsVariadic);
        Assert.Equal(new[] { "x", "rest" }, macro.Parameters!.ToArray());
    }

    [Fact]
    public void A_macro_may_have_an_empty_body()
    {
        MacroDefinition macro = Parse("#define FLAG", out List<Diagnostic> diagnostics)!;

        Assert.Empty(diagnostics);
        Assert.Empty(macro.Body);
        Assert.False(macro.IsFunctionLike);
    }

    [Fact]
    public void A_define_with_no_name_is_reported()
    {
        Assert.Null(Parse("#define", out List<Diagnostic> diagnostics));
        Assert.Contains(diagnostics, d => d.Id == "DM0110");
    }

    [Fact]
    public void Comments_are_stripped_from_the_body()
    {
        MacroDefinition macro = Parse("#define MAX 10 // the limit", out _)!;

        Assert.Single(macro.Body);
    }
}

public class ConditionalEvaluatorTests
{
    private static bool Evaluate(string condition, out List<Diagnostic> diagnostics, params string[] defines)
    {
        MacroTable table = new();

        foreach (string define in defines)
        {
            LexResult defLex = Lexer.Lex(SourceText.From($"#define {define}"));
            List<Diagnostic> ignored = new();
            MacroDefinition? macro = MacroDefinition.Parse(defLex, DirectiveScanner.Scan(defLex)[0], ignored);
            if (macro is not null)
                table.Define(macro);
        }

        LexResult lex = Lexer.Lex(SourceText.From($"#if {condition}"));
        diagnostics = new List<Diagnostic>();
        return ConditionalEvaluator.Evaluate(lex, DirectiveScanner.Scan(lex)[0], table, diagnostics);
    }

    private static bool Evaluate(string condition, params string[] defines)
        => Evaluate(condition, out _, defines);

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("-1", true)]
    [InlineData("1.5", true)]
    [InlineData("0.0", false)]
    [InlineData("!0", true)]
    [InlineData("!1", false)]
    [InlineData("2 > 1", true)]
    [InlineData("1 >= 1", true)]
    [InlineData("1 < 1", false)]
    [InlineData("1 == 1", true)]
    [InlineData("1 != 1", false)]
    [InlineData("(1+2) == 3", true)]
    [InlineData("2*3 == 6", true)]
    [InlineData("6/2 == 3", true)]
    [InlineData("5-5", false)]
    [InlineData("1 && 0", false)]
    [InlineData("1 || 0", true)]
    [InlineData("0 || 0", false)]
    public void Evaluates_the_supported_grammar(string condition, bool expected)
    {
        Assert.Equal(expected, Evaluate(condition, out List<Diagnostic> diagnostics));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Precedence_matches_arithmetic()
    {
        Assert.True(Evaluate("1 + 2 * 3 == 7"));
        Assert.True(Evaluate("(1 + 2) * 3 == 9"));
    }

    [Fact]
    public void Defined_reports_whether_a_macro_exists()
    {
        Assert.True(Evaluate("defined(FIVE)", "FIVE 5"));
        Assert.False(Evaluate("defined(ABSENT)", "FIVE 5"));
        Assert.False(Evaluate("!defined(FIVE)", "FIVE 5"));
    }

    /// <summary>
    /// The parentheses are required; dm.exe rejects <c>defined FIVE</c> with "expected (".
    /// </summary>
    [Fact]
    public void Defined_requires_parentheses()
    {
        Assert.False(Evaluate("defined FIVE", out List<Diagnostic> diagnostics, "FIVE 5"));
        Assert.Contains(diagnostics, d => d.Message.Contains("expected '('"));
    }

    [Fact]
    public void A_defined_macro_expands_to_its_value()
    {
        Assert.True(Evaluate("FIVE", "FIVE 5"));
        Assert.False(Evaluate("ZERO", "ZERO 0"));
        Assert.True(Evaluate("FIVE > 3", "FIVE 5"));
        Assert.True(Evaluate("FIVE + 1 == 6", "FIVE 5"));
    }

    [Fact]
    public void A_macro_expanding_to_another_macro_resolves()
    {
        Assert.True(Evaluate("OUTER", "INNER 7", "OUTER INNER"));
    }

    /// <summary>
    /// Unlike C, DM rejects a bare undefined name here rather than treating it as 0. That is why
    /// real code guards with <c>#ifdef</c> instead of <c>#if NAME</c>.
    /// </summary>
    [Fact]
    public void An_undefined_name_is_an_error_not_zero()
    {
        Assert.False(Evaluate("NOT_DEFINED", out List<Diagnostic> diagnostics));

        Diagnostic error = Assert.Single(diagnostics);
        Assert.Contains("not defined", error.Message);
        Assert.Contains("#ifdef", error.Message);
    }

    [Theory]
    [InlineData("5 % 2")]
    [InlineData("1 << 3")]
    [InlineData("\"text\"")]
    public void Constructs_the_compiler_rejects_are_reported(string condition)
    {
        Evaluate(condition, out List<Diagnostic> diagnostics);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    public void An_empty_condition_is_reported()
    {
        Assert.False(Evaluate("", out List<Diagnostic> diagnostics));
        Assert.Contains(diagnostics, d => d.Id == "DM0120");
    }

    [Fact]
    public void A_self_referential_macro_terminates()
    {
        // Guard against runaway expansion rather than hanging the language server.
        Assert.False(Evaluate("LOOP", out List<Diagnostic> diagnostics, "LOOP LOOP"));
        Assert.Contains(diagnostics, d => d.Message.Contains("too deep"));
    }

    [Fact]
    public void A_function_like_macro_without_arguments_is_reported()
    {
        Assert.False(Evaluate("F", out List<Diagnostic> diagnostics, "F(x) x"));
        Assert.Contains(diagnostics, d => d.Message.Contains("function-like"));
    }

    [Fact]
    public void Division_by_zero_is_reported_rather_than_thrown()
    {
        Assert.False(Evaluate("1 / 0", out List<Diagnostic> diagnostics));
        Assert.Contains(diagnostics, d => d.Id == "DM0122");
    }
}
