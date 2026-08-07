using System.Collections.Generic;
using System.Linq;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Syntax;

/// <summary>
/// Constructs found by diffing our diagnostics against <c>dm.exe</c>'s on /tg/station.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these compiles with 0 errors and we reported an error on it. None was produced by
/// writing tests: they came out of 1.5M lines of real code, which is the same lesson the lexer and
/// the object tree each learned separately.
/// </para>
/// <para>
/// They assert <b>silence</b>, because that is the failure that mattered — a project that builds
/// clean while we complain is a tool nobody trusts.
/// </para>
/// </remarks>
public class CorpusConstructTests
{
    private static IReadOnlyList<Diagnostic> Parse(string source)
        => DeclarationParser.Parse(Lexer.Lex(SourceText.From(source))).Diagnostics;

    private static void ParsesClean(string source)
    {
        IReadOnlyList<Diagnostic> diagnostics = Parse(source);

        Assert.True(
            diagnostics.Count == 0,
            "expected no diagnostics, got: " + string.Join("; ", diagnostics.Select(d => $"{d.Id} {d.Message}")));
    }

    /// <summary>
    /// A `\`-continued macro body has no lines to put a label on, so /tg/station's
    /// SEARCH_ADJ_IN_DIR writes the label against a brace block. Worth 754 diagnostics.
    /// </summary>
    [Fact]
    public void A_label_may_be_followed_by_a_brace_block()
        => ParsesClean(
            "/obj/thing\n\tproc/f()\n\t\tdo {\n\t\t\touter: {\n\t\t\t\tbreak outer\n\t\t\t}\n\t\t} while(0)\n");

    [Fact]
    public void A_label_on_its_own_line_still_works()
        => ParsesClean("/obj/thing\n\tproc/f()\n\t\touter:\n\t\t\tbreak outer\n");

    /// <summary>`pick(20;"brown", 1;"albino")` weights each choice with a semicolon.</summary>
    [Fact]
    public void Pick_takes_weighted_arguments()
        => ParsesClean("/obj/thing\n\tproc/f()\n\t\treturn pick(20;\"brown\", 20;\"hazel\", 1;\"albino\")\n");

    [Fact]
    public void A_weighted_argument_keeps_its_weight_and_still_counts_as_one()
    {
        ParseResult result = DeclarationParser.Parse(Lexer.Lex(SourceText.From(
            "/obj/thing\n\tproc/f()\n\t\treturn pick(20;\"brown\", 1;\"albino\")\n")));

        InvocationExpressionSyntax call = result.Root.Declarations
            .OfType<TypeDeclarationSyntax>().Single()
            .Members.OfType<ProcDeclarationSyntax>().Single()
            .Body!.Statements.OfType<ReturnStatementSyntax>().Single()
            .Value as InvocationExpressionSyntax
            ?? throw new Xunit.Sdk.XunitException("expected a call");

        Assert.Equal(2, call.Arguments.Count);
        Assert.All(call.Arguments, a => Assert.NotNull(a.Weight));
    }

    /// <summary>`if(icon_x in 12 to 20)` — a range test in ordinary expression position.</summary>
    [Fact]
    public void In_accepts_a_to_range()
        => ParsesClean("/obj/thing\n\tproc/f(x)\n\t\tif(x in 12 to 20)\n\t\t\treturn 1\n");

    /// <summary>
    /// `for(x in a to b step c)` with x already declared. The header's `in` belongs to the loop:
    /// letting the expression parser take it collapsed the whole header into one expression, and
    /// the loop was modelled as a bare `for` — which parsed silently and so went unnoticed.
    /// </summary>
    [Fact]
    public void A_for_header_over_a_range_keeps_its_own_in()
    {
        ParsesClean("/obj/thing\n\tproc/f(n)\n\t\tfor(n in n - 1 to 1 step -1)\n\t\t\treturn n\n");

        ParseResult result = DeclarationParser.Parse(Lexer.Lex(SourceText.From(
            "/obj/thing\n\tproc/f(n, list/L)\n\t\tfor(n in L)\n\t\t\treturn n\n")));

        ForStatementSyntax loop = result.Root.Declarations
            .OfType<TypeDeclarationSyntax>().Single()
            .Members.OfType<ProcDeclarationSyntax>().Single()
            .Body!.Statements.OfType<ForStatementSyntax>().Single();

        Assert.Equal(ForKind.In, loop.Kind);
        Assert.NotNull(loop.Sequence);
    }

    /// <summary>
    /// `0. SECONDS` expands to `0. *10`, so the trailing dot is part of the number. Split, it reads
    /// as member access and asks for a member name.
    /// </summary>
    [Fact]
    public void A_trailing_dot_is_part_of_the_number()
        => ParsesClean("/obj/thing\n\tproc/f()\n\t\treturn 0. *10\n");

    /// <summary>The counterpart: a name after the dot is still member access, not a number.</summary>
    [Fact]
    public void A_dot_followed_by_a_name_is_still_member_access()
    {
        IReadOnlyList<Token> tokens = Lexer.Lex(SourceText.From("1.Foo")).Tokens;

        Assert.Equal(TokenKind.Number, tokens[0].Kind);
        Assert.Equal(TokenKind.Dot, tokens[1].Kind);
        Assert.Equal(TokenKind.Identifier, tokens[2].Kind);
    }

    /// <summary>
    /// `step` lexes as a keyword only because `step()` is also a builtin proc. /tg/station writes
    /// `for(var/step in 1 to steps)`, and it works: compiled and run, the local holds 41 and the
    /// loop sums to 10.
    /// </summary>
    [Fact]
    public void Step_is_a_legal_variable_name()
    {
        ParsesClean("/obj/thing\n\tproc/f(n)\n\t\tfor(var/step in 1 to n)\n\t\t\treturn step\n");
        ParsesClean("/obj/thing\n\tproc/f()\n\t\tvar/step = 40\n\t\tstep += 1\n\t\treturn step\n");
    }

    /// <summary>
    /// And `step` is the ONLY one. `in`, `as` and `set` all accept the declaration and then fail on
    /// the first use — `in += 1` is "missing left-hand argument to in." — so declaring one is an
    /// error dm.exe reports and we should not quietly accept. `to` fails at the declaration itself.
    /// </summary>
    /// <remarks>
    /// This is the §8 rule applied to our own probe: a first pass wrote `var/in = 1; return in`,
    /// which compiles, and concluded all four were legal. Using the variable is what separated them.
    /// </remarks>
    [Fact]
    public void The_other_contextual_keywords_are_not_variable_names()
    {
        Assert.NotEmpty(Parse("/obj/thing\n\tproc/f()\n\t\tvar/in = 1\n\t\treturn 0\n"));
        Assert.NotEmpty(Parse("/obj/thing\n\tproc/f()\n\t\tvar/as = 1\n\t\treturn 0\n"));
        Assert.NotEmpty(Parse("/obj/thing\n\tproc/f()\n\t\tvar/to = 1\n\t\treturn 0\n"));
    }

    /// <summary>
    /// `?[` is one token and still opens a bracket. Inside an interpolation hole the lexer counted
    /// `[` by character, so the `]` closing a `?[` ended the hole instead — and the macro argument
    /// scanner had the same hole, dropping everything after that `]` from the argument.
    /// /tg/station's OFFSET_RENDER_TARGET nests exactly this, for 174 diagnostics.
    /// </summary>
    [Fact]
    public void A_null_conditional_index_inside_an_interpolation_hole_closes_its_own_bracket()
        => ParsesClean(
            "/datum/probe\n\tvar/list/blacklist = list()\n"
            + "\tproc/f(k)\n\t\treturn \"[( blacklist?[\"[k]\"] ? 0 : 1 )] tail\"\n");

    /// <summary>
    /// A doubled separator collapses: `TYPE_PROC_REF(/datum/beam/, Start)` expands to
    /// `/datum/beam/.proc/Start`, and stopping the path at the first separator handed the rest to
    /// member access.
    /// </summary>
    [Fact]
    public void A_doubled_path_separator_collapses()
        => ParsesClean("/obj/thing\n\tproc/f()\n\t\treturn nameof(/datum/beam/.proc/Start)\n");

    /// <summary>
    /// 516.1686 rejects a numeric literal as an associative list key — a breaking change, since
    /// 516.1666 compiled the same source. `alist()` is the escape hatch the message names.
    /// </summary>
    [Theory]
    [InlineData("list(1 = \"a\")")]
    [InlineData("list(0 = \"a\")")]
    [InlineData("list(1.5 = \"a\")")]
    [InlineData("list(-1 = \"a\")")]          // a unary minus over the literal, rejected the same
    [InlineData("list(\"k\" = list(1 = \"a\"))")]  // nested, so every invocation is checked
    public void A_numeric_associative_list_key_is_rejected(string expression)
    {
        IReadOnlyList<Diagnostic> diagnostics =
            Parse($"/obj/thing\n\tproc/f()\n\t\tvar/list/L = {expression}\n\t\treturn L\n");

        Assert.Contains(diagnostics, d => d.Id == "DM0404");
    }

    /// <summary>
    /// The boundaries, each compiled against dm.exe: `alist()` takes numeric keys, a string key is
    /// fine, and a VARIABLE key is accepted because the compiler cannot know statically what it
    /// holds — so only a literal is reported.
    /// </summary>
    [Theory]
    [InlineData("alist(1 = \"a\")")]
    [InlineData("list(\"k\" = \"a\")")]
    [InlineData("list(v = \"a\")")]
    public void A_numeric_key_is_only_reported_for_a_literal_in_list(string expression)
    {
        IReadOnlyList<Diagnostic> diagnostics =
            Parse($"/obj/thing\n\tproc/f()\n\t\tvar/v = 1\n\t\tvar/list/L = {expression}\n\t\treturn L\n");

        Assert.DoesNotContain(diagnostics, d => d.Id == "DM0404");
    }

    /// <summary>
    /// ONE diagnostic per list, not one per key: dm.exe reports a three-numeric-key list once, on
    /// the line the call opens. Reporting per key invented 12 on madridspy against its 2.
    /// </summary>
    [Fact]
    public void A_list_with_several_numeric_keys_reports_once()
    {
        IReadOnlyList<Diagnostic> diagnostics = Parse(
            "/obj/thing\n\tproc/f()\n\t\tvar/list/L = list(1 = \"a\", 2 = \"b\", 3 = \"c\")\n\t\treturn L\n");

        Assert.Single(diagnostics, d => d.Id == "DM0404");
    }
}
