using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Preprocessing;

public class MacroExpanderTests
{
    /// <summary>
    /// Defines the given macros, then expands <paramref name="code"/> and returns the resulting
    /// token text joined by spaces.
    /// </summary>
    private static string Expand(string code, out List<Diagnostic> diagnostics, params string[] defines)
    {
        MacroTable table = new();

        foreach (string define in defines)
        {
            LexResult defLex = Lexer.Lex(SourceText.From($"#define {define}"));
            List<Diagnostic> ignored = new();
            if (MacroDefinition.Parse(defLex, DirectiveScanner.Scan(defLex)[0], ignored) is { } macro)
                table.Define(macro);
        }

        SourceText text = SourceText.From(code);
        LexResult lex = Lexer.Lex(text);

        List<Token> significant = new();
        foreach (Token token in lex.Tokens)
        {
            if (token.Kind is not (TokenKind.EndOfFile or TokenKind.Newline or TokenKind.Indent
                or TokenKind.Dedent or TokenKind.Comment))
            {
                significant.Add(token);
            }
        }

        diagnostics = new List<Diagnostic>();
        IReadOnlyList<ExpandedToken> expanded = MacroExpander.Expand(text, significant, table, diagnostics);

        return string.Join(" ", expanded.Select(t => t.Text));
    }

    private static string Expand(string code, params string[] defines) => Expand(code, out _, defines);

    // -- object-like -------------------------------------------------------

    [Fact]
    public void Expands_an_object_like_macro()
    {
        Assert.Equal("10", Expand("MAX", "MAX 10"));
    }

    [Fact]
    public void Leaves_unknown_identifiers_alone()
    {
        Assert.Equal("SOMETHING", Expand("SOMETHING"));
    }

    [Fact]
    public void Expands_a_macro_that_expands_to_another()
    {
        Assert.Equal("7", Expand("OUTER", "INNER 7", "OUTER INNER"));
    }

    // -- __FILE__ / __LINE__ -----------------------------------------------
    // Position-dependent, so no table entry can carry them; the expander makes them at the use.

    [Fact]
    public void Line_expands_to_the_use_line()
    {
        Assert.Equal("x 2 y", Expand("x\n__LINE__ y"));
    }

    /// <summary>A body token reports at the invocation, which is dm.exe's value too.</summary>
    [Fact]
    public void Line_in_a_macro_body_is_the_invocation_line()
    {
        Assert.Equal("a b 3", Expand("a\nb\nWHERE", "WHERE __LINE__"));
    }

    /// <summary>The harness's SourceText has no path, so the value is an empty string literal.</summary>
    [Fact]
    public void File_expands_to_a_string_literal()
    {
        Assert.Equal("\" \"", Expand("__FILE__"));
    }

    /// <summary>
    /// The tgstation shape: `__LINE__` inside a macro-body string's interpolation hole. These
    /// flowed to the parser as identifiers until 2026-08-13 — 5,878 of them at once when the
    /// bare-identifier check first ran there.
    /// </summary>
    [Fact]
    public void Line_expands_inside_a_macro_body_string_hole()
    {
        Assert.Contains(" 1 ", Expand("A(x)", "A(m) f(\"[__LINE__]\")"));
    }

    /// <summary>A project define wins over the built-in reading.</summary>
    [Fact]
    public void A_project_define_of_line_wins()
    {
        Assert.Equal("9", Expand("__LINE__", "__LINE__ 9"));
    }

    [Fact]
    public void An_empty_macro_expands_to_nothing()
    {
        Assert.Equal("a b", Expand("a FLAG b", "FLAG"));
    }

    // -- function-like -----------------------------------------------------

    [Fact]
    public void Expands_a_function_like_macro()
    {
        Assert.Equal("( ( 2 ) * ( 2 ) )", Expand("SQR(2)", "SQR(X) ((X)*(X))"));
    }

    [Fact]
    public void Substitutes_multiple_parameters()
    {
        Assert.Equal("1 + 2", Expand("ADD(1,2)", "ADD(a,b) a + b"));
    }

    /// <summary>
    /// A bare mention of a function-like macro is not an invocation. dm.exe reports it as an
    /// undefined var, which means it was never expanded.
    /// </summary>
    [Fact]
    public void A_function_like_macro_without_parentheses_is_not_expanded()
    {
        Assert.Equal("SQR", Expand("SQR", "SQR(X) ((X)*(X))"));
    }

    /// <summary>
    /// Commas inside nested parens or brackets belong to the inner construct, not the argument
    /// list.
    /// </summary>
    [Fact]
    public void Nested_commas_do_not_split_arguments()
    {
        Assert.Equal("f ( a , b )", Expand("ID(f(a,b))", "ID(x) x"));
        Assert.Equal("L [ a , b ]", Expand("ID(L[a,b])", "ID(x) x"));
    }

    /// <summary>Verified against dm.exe: <c>ID(INNER)</c> yields the expanded value of INNER.</summary>
    [Fact]
    public void Arguments_are_expanded_before_substitution()
    {
        Assert.Equal("5", Expand("ID(INNER)", "INNER 5", "ID(x) x"));
    }

    [Fact]
    public void A_missing_argument_substitutes_as_nothing()
    {
        Assert.Equal("+", Expand("ADD()", "ADD(a,b) a + b"));
    }

    [Fact]
    public void Too_many_arguments_are_reported()
    {
        Expand("ONE(1,2)", out List<Diagnostic> diagnostics, "ONE(a) a");
        Assert.Contains(diagnostics, d => d.Id == "DM0132");
    }

    // -- variadic ----------------------------------------------------------

    [Fact]
    public void A_variadic_tail_absorbs_the_remaining_arguments()
    {
        Assert.Equal("g ( 1 , 2 , 3 )", Expand("V(1,2,3)", "V(x, rest...) g(x, rest)"));
    }

    [Fact]
    public void A_variadic_tail_may_be_empty()
    {
        Assert.Equal("g ( 1 , )", Expand("V(1)", "V(x, rest...) g(x, rest)"));
    }

    // -- # and ## ----------------------------------------------------------

    /// <summary>
    /// Stringification preserves the argument's original spacing. dm.exe turns <c>a + b</c> into
    /// <c>"a + b"</c> and <c>f(1,2)</c> into <c>"f(1,2)"</c>.
    /// </summary>
    [Theory]
    [InlineData("S(usr.x)", "usr.x")]
    [InlineData("S(a + b)", "a + b")]
    [InlineData("S(f(1,2))", "f(1,2)")]
    public void Hash_stringifies_with_the_original_spacing(string code, string expected)
    {
        Assert.Equal($"\" {expected} \"", Expand(code, "S(v) #v"));
    }

    /// <summary>
    /// Stringification does not expand its argument. Verified: <c>TOTEXT(SAYTWICE(hi))</c> prints
    /// the literal <c>SAYTWICE(hi)</c>, which is also why the reference's own <c>hihi</c> example
    /// for this construct is wrong.
    /// </summary>
    [Fact]
    public void Hash_does_not_expand_its_argument()
    {
        Assert.Equal("\" INNER \"", Expand("S(INNER)", "INNER 5", "S(v) #v"));
    }

    /// <summary>
    /// Pasting produces one token, not two adjacent ones. Verified against dm.exe:
    /// <c>MACROVAR(right)</c> declares a var named <c>macro_state_right</c>, and referencing
    /// <c>macro_state_</c> alone fails with "undefined var".
    /// </summary>
    [Fact]
    public void Double_hash_pastes_into_a_single_token()
    {
        Assert.Equal("var / macro_state_right", Expand("MACROVAR(right)", "MACROVAR(k) var/macro_state_##k"));
    }

    /// <summary>
    /// <c>N###param</c> repeats and pastes. Verified: <c>2###hi</c> yields the single identifier
    /// <c>hihi</c> and <c>3###hi</c> yields <c>hihihi</c>.
    /// </summary>
    [Theory]
    [InlineData("2###t", "hihi")]
    [InlineData("3###t", "hihihi")]
    [InlineData("1###t", "hi")]
    public void Triple_hash_repeats_and_pastes(string body, string expected)
    {
        Assert.Equal(expected, Expand("REP(hi)", $"REP(t) {body}"));
    }

    /// <summary>
    /// The reference's documented variadic behaviour: an empty replacement drops the preceding
    /// comma so no dangling separator is left.
    /// </summary>
    [Fact]
    public void An_empty_pasted_variadic_drops_the_preceding_comma()
    {
        Assert.Equal("list ( 4 , src )", Expand("PREFIX(4)", "PREFIX(x, y...) list(x, src, ##y)"));
    }

    [Fact]
    public void A_non_empty_pasted_variadic_keeps_its_arguments()
    {
        Assert.Equal("list ( 1 , src , 2 , 3 )", Expand("PREFIX(1,2,3)", "PREFIX(x, y...) list(x, src, ##y)"));
    }

    /// <summary>
    /// A pasted run keeps each token's whitespace-before fact. Spacing decides a conditional's
    /// <c>:</c> (PLAN §4c), and tgstation routes ternaries through <c>##</c> constantly —
    /// <c>INVOKE_ASYNC(..., (a) ? u : v)</c> — so losing the fact misparses the colon as member
    /// access on code the compiler accepts.
    /// </summary>
    [Fact]
    public void A_pasted_run_keeps_the_whitespace_fact()
    {
        MacroTable table = new();
        LexResult defLex = Lexer.Lex(SourceText.From("#define LISTIFY(args...) list(##args)"));
        List<Diagnostic> ignored = new();
        table.Define(MacroDefinition.Parse(defLex, DirectiveScanner.Scan(defLex)[0], ignored)!);

        SourceText text = SourceText.From("LISTIFY(u, null, (a) ? u : v)");
        LexResult lex = Lexer.Lex(text);

        List<Token> significant = lex.Tokens
            .Where(t => t.Kind is not (TokenKind.EndOfFile or TokenKind.Newline or TokenKind.Indent
                or TokenKind.Dedent or TokenKind.Comment))
            .ToList();

        List<Diagnostic> diagnostics = new();
        IReadOnlyList<ExpandedToken> expanded = MacroExpander.Expand(text, significant, table, diagnostics);
        TokenSource source = TokenSource.FromExpanded(text, expanded);

        int colon = -1;
        for (int i = 0; i < source.Tokens.Count; i++)
        {
            if (source.Tokens[i].Kind == TokenKind.Colon)
                colon = i;
        }

        Assert.True(colon > 0, "no colon in the expansion");
        Assert.True(source.HasWhitespaceBefore(colon), "the pasted colon lost its whitespace fact");
    }

    // -- recursion ---------------------------------------------------------

    /// <summary>
    /// DM has no equivalent of C's blue-paint rule: it expands until it gives up with "macro
    /// recursion level too deep". We stop at the same point rather than spinning.
    /// </summary>
    [Fact]
    public void A_self_referential_macro_is_reported_rather_than_looping()
    {
        Expand("A", out List<Diagnostic> diagnostics, "A A");

        Assert.Contains(diagnostics, d => d.Id == "DM0131");
        Assert.Contains(diagnostics, d => d.Message.Contains("recursion level too deep"));
    }

    [Fact]
    public void Mutual_recursion_is_reported()
    {
        Expand("A", out List<Diagnostic> diagnostics, "A B", "B A");
        Assert.Contains(diagnostics, d => d.Id == "DM0131");
    }

    [Fact]
    public void A_recursive_function_like_macro_is_reported()
    {
        Expand("F(1)", out List<Diagnostic> diagnostics, "F(x) F(x)");
        Assert.Contains(diagnostics, d => d.Id == "DM0131");
    }

    // -- source mapping ----------------------------------------------------

    /// <summary>
    /// The reason the expander exists in this shape. Without an origin per token, every diagnostic
    /// in macro-heavy code points at the macro definition instead of the line being edited.
    /// </summary>
    [Fact]
    public void Expanded_tokens_report_at_the_invocation_not_the_definition()
    {
        MacroTable table = new();
        LexResult defLex = Lexer.Lex(SourceText.From("#define MAX 10"));
        List<Diagnostic> ignored = new();
        table.Define(MacroDefinition.Parse(defLex, DirectiveScanner.Scan(defLex)[0], ignored)!);

        SourceText use = SourceText.From("var/x = MAX", "use.dm");
        LexResult lex = Lexer.Lex(use);

        List<Token> significant = new();
        foreach (Token token in lex.Tokens)
        {
            if (token.Kind is not (TokenKind.EndOfFile or TokenKind.Newline))
                significant.Add(token);
        }

        IReadOnlyList<ExpandedToken> expanded =
            MacroExpander.Expand(use, significant, table, new List<Diagnostic>());

        ExpandedToken number = expanded.Single(t => t.Kind == TokenKind.Number);

        Assert.True(number.IsFromMacro);
        Assert.Equal("MAX", number.Expansion!.Macro.Name);

        // Its characters live in the defining file...
        Assert.Equal("10", number.Text);

        // ...but it must be reported against the file and span where MAX was written.
        Assert.Equal(use, number.ReportAt.Source);
        Assert.Equal("MAX", use.ToString(number.ReportAt.Span));
    }

    [Fact]
    public void Verbatim_tokens_carry_no_expansion()
    {
        MacroTable table = new();
        SourceText text = SourceText.From("var/x = 1");
        LexResult lex = Lexer.Lex(text);

        List<Token> significant = new();
        foreach (Token token in lex.Tokens)
        {
            if (token.Kind is not (TokenKind.EndOfFile or TokenKind.Newline))
                significant.Add(token);
        }

        IReadOnlyList<ExpandedToken> expanded =
            MacroExpander.Expand(text, significant, table, new List<Diagnostic>());

        Assert.All(expanded, t => Assert.False(t.IsFromMacro));
    }

    [Fact]
    public void A_nested_expansion_chains_out_to_the_outermost_use()
    {
        MacroTable table = new();

        foreach (string define in new[] { "INNER 7", "OUTER INNER" })
        {
            LexResult defLex = Lexer.Lex(SourceText.From($"#define {define}"));
            List<Diagnostic> ignored = new();
            table.Define(MacroDefinition.Parse(defLex, DirectiveScanner.Scan(defLex)[0], ignored)!);
        }

        SourceText use = SourceText.From("OUTER", "use.dm");
        LexResult lex = Lexer.Lex(use);

        List<Token> significant = new();
        foreach (Token token in lex.Tokens)
        {
            if (token.Kind is not (TokenKind.EndOfFile or TokenKind.Newline))
                significant.Add(token);
        }

        ExpandedToken number = MacroExpander
            .Expand(use, significant, table, new List<Diagnostic>())
            .Single(t => t.Kind == TokenKind.Number);

        Assert.Equal("INNER", number.Expansion!.Macro.Name);
        Assert.Equal("OUTER", number.Expansion.Outermost.Macro.Name);
        Assert.Equal(use, number.ReportAt.Source);
    }

    /// <summary>
    /// The anonymous <c>M(...)</c> variadic, which is a different binding from <c>M(a, rest...)</c>.
    /// </summary>
    /// <remarks>
    /// /tg/station defines <c>PERFORM_ALL_TESTS</c> both ways, one per branch of an <c>#ifdef</c>,
    /// so which is live depends on the build's flags. Rejecting the anonymous form made every call
    /// an arity error — 54 on that project. Then accepting it was not enough: a named rest absorbs
    /// the remaining arguments while an anonymous one has nowhere to put them and discards them, so
    /// treating the two alike made the first parameter swallow the lot.
    /// </remarks>
    [Fact]
    public void An_anonymous_variadic_takes_any_arity_and_discards_the_extras()
    {
        Assert.Equal("( 1 )", Expand("ANON(a, b, c)", "ANON(...) (1)"));
        Assert.Equal("( 1 )", Expand("ANON()", "ANON(...) (1)"));
        Assert.Equal("( 7 )", Expand("MIXED(7, 8, 9)", "MIXED(a, ...) (a)"));
    }

    /// <summary>A named rest parameter still absorbs the remainder, commas included.</summary>
    [Fact]
    public void A_named_rest_parameter_still_absorbs()
        => Assert.Equal("( 8 , 9 )", Expand("NAMED(7, 8, 9)", "NAMED(a, rest...) (rest)"));

    /// <summary>
    /// An argument containing <c>?[</c> keeps everything after the matching <c>]</c>.
    /// </summary>
    /// <remarks>
    /// <c>?[</c> is a single token and still opens a bracket. The argument scanner counted only
    /// <c>OpenBracket</c>, so the depth went negative on the closing <c>]</c> and it took that as
    /// the end of the invocation — silently dropping the rest of the argument. /tg/station's
    /// <c>OFFSET_RENDER_TARGET</c> lost its whole <c>? 0 : off</c> tail this way, and the parse then
    /// failed on a stream that was simply missing tokens. 174 diagnostics.
    /// </remarks>
    [Fact]
    public void An_argument_containing_a_null_conditional_index_is_not_truncated()
    {
        string expanded = Expand(
            "OUTER(k, fallback)",
            "OUTER(rt, off) (blacklist?[\"[rt]\"] ? 0 : off)");

        Assert.Contains("fallback", expanded);
        Assert.Contains("?", expanded);
    }
}
