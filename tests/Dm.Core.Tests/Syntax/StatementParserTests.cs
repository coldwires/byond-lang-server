using System.Collections.Generic;
using System.Linq;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Syntax;

/// <summary>
/// Covers the statement forms in PLAN.md M4, including the two grammars <c>#pragma syntax</c>
/// changes and the shapes §8 established by compiling.
/// </summary>
public class StatementParserTests
{
    /// <summary>Parses a file and returns the first proc's body.</summary>
    private static BlockStatementSyntax Body(string source, out IReadOnlyList<Diagnostic> diagnostics)
    {
        ParseResult result = DeclarationParser.Parse(Lexer.Lex(SourceText.From(source)));
        diagnostics = result.Diagnostics;

        ProcDeclarationSyntax proc = result.Root.Declarations
            .OfType<ProcDeclarationSyntax>()
            .First();

        Assert.NotNull(proc.Body);
        return proc.Body!;
    }

    private static BlockStatementSyntax Body(string source)
    {
        BlockStatementSyntax body = Body(source, out IReadOnlyList<Diagnostic> diagnostics);
        Assert.Empty(diagnostics);
        return body;
    }

    /// <summary>Wraps statements in a proc so a fragment can be parsed on its own.</summary>
    private static BlockStatementSyntax InProc(string statements)
        => Body("/mob/proc/F()\n" + statements);

    private static T Single<T>(string statements) where T : StatementSyntax
        => Assert.IsType<T>(Assert.Single(InProc(statements).Statements));

    // -- the for-header shape checks ---------------------------------------

    /// <summary>
    /// dm.exe rejects a FOURTH clause under the default grammar — including the C idiom
    /// <c>i++, j++</c>, where the comma separates a clause rather than chaining. Probed as a
    /// matrix on 516.1686 (PLAN §8).
    /// </summary>
    [Theory]
    [InlineData("\tfor(i = 0; i < 3; i++; j++)\n\t\tr++\n")]
    [InlineData("\tfor(i = 0; i < 3; i++, j++)\n\t\tr++\n")]
    [InlineData("\tfor(i = 0, j = 9, i < 3, i++)\n\t\tr++\n")]
    public void A_fourth_for_clause_is_too_many_args(string statements)
    {
        Body("/mob/proc/F()\n\tvar/i\n\tvar/j\n\tvar/r\n" + statements,
            out IReadOnlyList<Diagnostic> diagnostics);

        Assert.Contains(diagnostics, d => d.Message == "for: too many args");
    }

    /// <summary>Up to three clauses are fine with either separator, mixed included.</summary>
    [Theory]
    [InlineData("\tfor(i = 0, i < 3, i++)\n\t\tr++\n")]
    [InlineData("\tfor(i = 0; i < 3; i++)\n\t\tr++\n")]
    [InlineData("\tfor(i = 0; i < 3)\n\t\tr++\n")]
    [InlineData("\tfor(i = 0, j = 9, i < 3)\n\t\tr++\n")]
    public void Three_for_clauses_stay_silent(string statements)
    {
        Body("/mob/proc/F()\n\tvar/i\n\tvar/j\n\tvar/r\n" + statements,
            out IReadOnlyList<Diagnostic> diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Message.StartsWith("for:"));
    }

    /// <summary>
    /// Under <c>#pragma syntax C for</c> a comma chains statements instead of separating
    /// clauses, so a header built ONLY from commas is malformed however few it has — while
    /// chained commas beside semicolons are the C idiom working, and stay silent.
    /// </summary>
    [Fact]
    public void A_comma_only_header_under_C_for_is_malformed()
    {
        Body(
            "/mob/proc/F()\n\tvar/i\n\tvar/r\n\t#pragma syntax C for\n"
            + "\tfor(i = 0, i < 3, i++)\n\t\tr++\n",
            out IReadOnlyList<Diagnostic> diagnostics);

        Assert.Contains(diagnostics, d => d.Message == "for: malformed for statement");

        Body(
            "/mob/proc/F()\n\tvar/i\n\tvar/j\n\tvar/r\n\t#pragma syntax C for\n"
            + "\tfor(i = 0, j = 0; i < 3; i++, j++)\n\t\tr++\n",
            out IReadOnlyList<Diagnostic> clean);

        Assert.DoesNotContain(clean, d => d.Message.StartsWith("for:"));
    }

    // -- control flow ------------------------------------------------------

    [Fact]
    public void If_else_parses_both_branches()
    {
        IfStatementSyntax statement = Single<IfStatementSyntax>("\tif(x)\n\t\ta()\n\telse\n\t\tb()\n");

        Assert.NotNull(statement.Then);
        Assert.NotNull(statement.Otherwise);
    }

    [Fact]
    public void Else_if_chains_into_a_nested_if()
    {
        IfStatementSyntax statement =
            Single<IfStatementSyntax>("\tif(x)\n\t\ta()\n\telse if(y)\n\t\tb()\n\telse\n\t\tc()\n");

        IfStatementSyntax inner = Assert.IsType<IfStatementSyntax>(statement.Otherwise);
        Assert.NotNull(inner.Otherwise);
    }

    [Fact]
    public void A_body_may_sit_on_the_header_line()
    {
        // stddef.dm writes `Multiply(m) return matrix(src, m)`.
        IfStatementSyntax statement = Single<IfStatementSyntax>("\tif(x) return\n");

        BlockStatementSyntax then = Assert.IsType<BlockStatementSyntax>(statement.Then);
        Assert.IsType<ReturnStatementSyntax>(Assert.Single(then.Statements));
    }

    [Fact]
    public void While_and_do_while_both_parse()
    {
        Assert.NotNull(Single<WhileStatementSyntax>("\twhile(x)\n\t\ta()\n").Body);
        Assert.NotNull(Single<DoWhileStatementSyntax>("\tdo\n\t\ta()\n\twhile(x)\n").Condition);
    }

    // -- for, in all four shapes -------------------------------------------

    /// <summary>
    /// The default clause separator is the <b>comma</b>, not the semicolon — the correction that
    /// came out of the author's compiler testing. Semicolons work too.
    /// </summary>
    [Theory]
    [InlineData("\tfor(var/i = 1, i <= 5, i++)\n\t\ta()\n")]
    [InlineData("\tfor(var/i = 1; i <= 5; i++)\n\t\ta()\n")]
    public void A_clause_for_takes_commas_or_semicolons(string source)
    {
        ForStatementSyntax statement = Single<ForStatementSyntax>(source);

        Assert.Equal(ForKind.Clauses, statement.Kind);
        Assert.Single(statement.Initializers);
        Assert.NotNull(statement.Condition);
        Assert.Single(statement.Increments);
    }

    [Fact]
    public void A_for_in_records_its_sequence()
    {
        ForStatementSyntax statement = Single<ForStatementSyntax>("\tfor(var/mob/M in world)\n\t\ta()\n");

        Assert.Equal(ForKind.In, statement.Kind);
        Assert.NotNull(statement.Sequence);
    }

    [Fact]
    public void A_for_range_records_its_bounds_and_step()
    {
        ForStatementSyntax statement = Single<ForStatementSyntax>("\tfor(var/i = 1 to 10 step 2)\n\t\ta()\n");

        Assert.Equal(ForKind.Range, statement.Kind);
        Assert.NotNull(statement.RangeEnd);
        Assert.NotNull(statement.Step);
    }

    /// <summary><c>for(var/j in 1 to L.len)</c> combines the two forms.</summary>
    [Fact]
    public void A_for_in_may_carry_a_range()
    {
        ForStatementSyntax statement = Single<ForStatementSyntax>("\tfor(var/j in 1 to L.len)\n\t\ta()\n");

        Assert.Equal(ForKind.In, statement.Kind);
        Assert.NotNull(statement.RangeEnd);
    }

    /// <summary>
    /// A bare <c>for</c> has no clause at all and iterates the world's contents, so anything in
    /// nullspace is invisible to it — compiler-verified, PLAN.md §8.
    /// </summary>
    [Fact]
    public void A_bare_for_has_no_clause()
    {
        Assert.Equal(ForKind.Bare, Single<ForStatementSyntax>("\tfor(var/obj/marker/M)\n\t\ta()\n").Kind);
    }

    // -- switch -------------------------------------------------------------

    /// <summary>
    /// A DM-style switch that ends with no arms is dm.exe's "empty switch statement" warning on
    /// the switch's own line, beside whatever error the non-arm content already drew. Probed from
    /// the mined corpus in three shapes: no body, a statement for a body, a body with no arms.
    /// </summary>
    [Theory]
    [InlineData("\tswitch(x)\n\treturn\n")]
    [InlineData("\tswitch(x)\n\t\tvar/y = 1\n")]
    public void An_empty_switch_is_a_warning(string source)
    {
        Body("/mob/proc/F(x)\n" + source, out IReadOnlyList<Diagnostic> diagnostics);

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Warning && d.Message == "empty switch statement");
    }

    [Fact]
    public void A_switch_reads_its_arms_and_ranges()
    {
        SwitchStatementSyntax statement = Single<SwitchStatementSyntax>(
            "\tswitch(n)\n\t\tif(1)\n\t\t\ta()\n\t\tif(2,3)\n\t\t\tb()\n\t\tif(4 to 6)\n\t\t\tc()\n\t\telse\n\t\t\td()\n");

        Assert.False(statement.IsCStyle);
        Assert.Equal(4, statement.Cases.Count);
        Assert.Equal(2, statement.Cases[1].Values.Count);
        Assert.NotNull(statement.Cases[2].RangeEnds[0]);
        Assert.True(statement.Cases[3].IsDefault);
    }

    /// <summary>
    /// The arm list may be a brace block — the form a <c>\</c>-continued macro body has to write,
    /// as tgstation's <c>CONVERT_PH_TO_COLOR</c> does. Arms, ranges and <c>else</c> all work there,
    /// and the braces may open on the header line or the next one with indented arms inside.
    /// </summary>
    [Theory]
    [InlineData("\tswitch(n) { if(7 to 10) { a() } if(2 to 7) { b() } else { c() } }\n", 3)]
    [InlineData("\tswitch(n)\n\t{\n\t\tif(7 to 10)\n\t\t\ta()\n\t\telse\n\t\t\tc()\n\t}\n", 2)]
    public void A_switch_arm_list_may_be_a_brace_block(string source, int arms)
    {
        SwitchStatementSyntax statement = Single<SwitchStatementSyntax>(source);

        Assert.Equal(arms, statement.Cases.Count);
        Assert.NotNull(statement.Cases[0].RangeEnds[0]);
        Assert.True(statement.Cases[arms - 1].IsDefault);
    }

    // -- the pragma-dependent grammars --------------------------------------

    /// <summary>
    /// <c>#pragma syntax C switch</c> replaces <c>if</c>/<c>else</c> arms with <c>case v:</c> and
    /// <c>default:</c>. The pragma sits at file level while the grammar it changes is used inside a
    /// body, so the state has to survive between the two parsers.
    /// </summary>
    [Fact]
    public void The_pragma_switches_to_c_style_cases()
    {
        BlockStatementSyntax body = Body(
            "#pragma syntax C switch\n/mob/proc/F(n)\n\tswitch(n)\n\t\tcase 1:\n\t\t\ta()\n\t\tdefault:\n\t\t\tb()\n");

        SwitchStatementSyntax statement = Assert.IsType<SwitchStatementSyntax>(Assert.Single(body.Statements));

        Assert.True(statement.IsCStyle);
        Assert.Equal(2, statement.Cases.Count);
        Assert.True(statement.Cases[1].IsDefault);
    }

    /// <summary><c>#pragma pop</c> restores the grammar, so DM arms parse again after it.</summary>
    [Fact]
    public void A_pragma_pop_restores_the_default_grammar()
    {
        ParseResult result = DeclarationParser.Parse(Lexer.Lex(SourceText.From(
            "#pragma push\n#pragma syntax C switch\n/mob/proc/F(n)\n\tswitch(n)\n\t\tcase 1:\n\t\t\ta()\n"
            + "#pragma pop\n/mob/proc/G(n)\n\tswitch(n)\n\t\tif(1)\n\t\t\tb()\n")));

        Assert.Empty(result.Diagnostics);

        List<ProcDeclarationSyntax> procs = result.Root.Declarations.OfType<ProcDeclarationSyntax>().ToList();
        Assert.Equal(2, procs.Count);

        Assert.True(Assert.IsType<SwitchStatementSyntax>(procs[0].Body!.Statements[0]).IsCStyle);
        Assert.False(Assert.IsType<SwitchStatementSyntax>(procs[1].Body!.Statements[0]).IsCStyle);
    }

    /// <summary>
    /// The <c>for</c> pragma swaps what the comma means: it stops separating clauses and starts
    /// chaining statements within one. Compiler-verified — with it on, <c>for(i=0, j=0; i&lt;3; …)</c>
    /// has two initialisers, and the comma form is rejected outright.
    /// </summary>
    [Fact]
    public void The_pragma_makes_a_comma_chain_for_clauses()
    {
        BlockStatementSyntax body = Body(
            "#pragma syntax C for\n/mob/proc/F()\n\tfor(var/i = 0, var/j = 100; i < 3; i++, j += 10)\n\t\ta()\n");

        ForStatementSyntax statement = Assert.IsType<ForStatementSyntax>(Assert.Single(body.Statements));

        Assert.Equal(2, statement.Initializers.Count);
        Assert.Equal(2, statement.Increments.Count);
    }

    // -- simple statements --------------------------------------------------

    [Fact]
    public void Break_and_continue_take_an_optional_label()
    {
        Assert.Equal("outer", Single<BreakStatementSyntax>("\tbreak outer\n").Label);
        Assert.True(Single<BreakStatementSyntax>("\tcontinue\n").IsContinue);
    }

    /// <summary><c>del</c> and <c>throw</c> take a bare operand with no parentheses.</summary>
    [Theory]
    [InlineData("\tdel src\n", TokenKind.KeywordDel)]
    [InlineData("\tthrow EXCEPTION(\"bad\")\n", TokenKind.KeywordThrow)]
    public void Del_and_throw_take_a_bare_operand(string source, object expected)
    {
        UnaryStatementSyntax statement = Single<UnaryStatementSyntax>(source);

        Assert.Equal((TokenKind)expected, statement.Keyword);
        Assert.NotNull(statement.Operand);
    }

    [Theory]
    [InlineData("\tspawn()\n\t\ta()\n")]
    [InlineData("\tspawn(10)\n\t\ta()\n")]
    [InlineData("\tspawn(rand(0, 2)) a()\n")]
    public void Spawn_parses_with_or_without_a_delay(string source)
    {
        Assert.NotNull(Single<SpawnStatementSyntax>(source).Body);
    }

    [Fact]
    public void Try_catch_parses_both_blocks()
    {
        TryStatementSyntax statement =
            Single<TryStatementSyntax>("\ttry\n\t\ta()\n\tcatch(var/exception/e)\n\t\tb()\n");

        Assert.NotNull(statement.Body);
        Assert.NotNull(statement.CatchBody);
    }

    [Fact]
    public void Set_records_its_name_and_value()
    {
        Assert.Equal("category", Single<SetStatementSyntax>("\tset category = \"Debug\"\n").Name);
    }

    // -- separators before a continuation keyword ----------------------------
    //
    // dm.exe tolerates any run of `;` and blank lines between a body and its else / while / catch.
    // A `\`-continued macro body forces the idiom — `if(a) { b; }; else { c; };` is what /tg/station
    // writes — and it was worth 126 invented diagnostics there. Compiler-verified on 516.1666.

    [Theory]
    [InlineData("\tif(x) { a(); }; else { b(); };\n")]
    [InlineData("\tif(x) { a(); };; else { b(); };\n")]
    [InlineData("\tif(x) { a(); };\n\telse { b(); }\n")]
    [InlineData("\tif(x) a(); else b();\n")]
    [InlineData("\tif(x)\n\t\ta()\n\t;\n\telse\n\t\tb()\n")]
    public void A_semicolon_run_before_else_belongs_to_the_if(string source)
    {
        IfStatementSyntax statement = Single<IfStatementSyntax>(source);

        Assert.NotNull(statement.Then);
        Assert.NotNull(statement.Otherwise);
    }

    [Theory]
    [InlineData("\tdo { a(); }; while(x)\n")]
    [InlineData("\tdo a(); while(x)\n")]
    public void A_semicolon_before_while_still_closes_the_do(string source)
    {
        DoWhileStatementSyntax statement = Single<DoWhileStatementSyntax>(source);

        // The while is the do's closer, not a nested loop over what follows.
        BlockStatementSyntax body = Assert.IsType<BlockStatementSyntax>(statement.Body);
        Assert.IsType<ExpressionStatementSyntax>(Assert.Single(body.Statements));
        Assert.NotNull(statement.Condition);
    }

    [Theory]
    [InlineData("\ttry { a(); }; catch(var/exception/e) { b(); };\n")]
    [InlineData("\ttry a(); catch(var/exception/e) b();\n")]
    public void A_semicolon_before_catch_still_binds_it(string source)
    {
        TryStatementSyntax statement = Single<TryStatementSyntax>(source);

        Assert.NotNull(statement.Body);
        Assert.NotNull(statement.CatchBody);
    }

    /// <summary>
    /// The separator is required: <c>if(a) r = 1 else r = 2</c> is dm.exe's
    /// "expected end of statement", so accepting it silently would diverge the other way.
    /// </summary>
    [Fact]
    public void An_unseparated_else_is_still_a_diagnostic()
    {
        Body("/mob/proc/F()\n\tif(x) a() else b()\n", out IReadOnlyList<Diagnostic> diagnostics);

        Assert.NotEmpty(diagnostics);
    }

    /// <summary>
    /// Thirteen statement keywords are legal type-path segments (SyntaxFacts has the probe
    /// results) — tgstation declares <c>/datum/manipulator_task/cargo/dropoff_base/throw</c> and
    /// then writes locals of that type. The keyword is a SEGMENT only: <c>var/throw = 1</c> is
    /// rejected by dm.exe, so the name slot stays narrow.
    /// </summary>
    [Fact]
    public void A_keyword_may_be_a_type_segment_of_a_local()
    {
        LocalVarStatementSyntax statement =
            Single<LocalVarStatementSyntax>("\tvar/datum/task/throw/T = thing\n");

        Assert.Equal("T", statement.Name);
        Assert.Equal("/datum/task/throw", statement.DeclaredType!.Text);
    }

    [Fact]
    public void A_keyword_is_still_not_a_local_name()
    {
        Body("/mob/proc/F()\n\tvar/throw = 1\n", out IReadOnlyList<Diagnostic> diagnostics);

        Assert.NotEmpty(diagnostics);
    }

    /// <summary>
    /// A modifier word is a modifier only when a separator follows: <c>var/final/x</c> carries
    /// 516's final modifier, <c>var/final = ""</c> declares a var NAMED final. All five modifier
    /// words are legal names, compiler-verified with uses; /tg/station writes `var/final = ""`.
    /// </summary>
    [Theory]
    [InlineData("final")]
    [InlineData("const")]
    [InlineData("tmp")]
    [InlineData("static")]
    [InlineData("global")]
    public void A_modifier_word_without_a_separator_is_a_name(string word)
    {
        LocalVarStatementSyntax statement = Single<LocalVarStatementSyntax>($"\tvar/{word} = 1\n");

        Assert.Equal(word, statement.Name);
        Assert.Empty(statement.Modifiers);
    }

    [Fact]
    public void A_modifier_word_before_a_separator_is_still_a_modifier()
    {
        LocalVarStatementSyntax statement = Single<LocalVarStatementSyntax>("\tvar/final/x = 1\n");

        Assert.Equal("x", statement.Name);
        Assert.Contains("final", statement.Modifiers);
    }

    // -- local variables ----------------------------------------------------

    [Fact]
    public void A_local_var_records_its_type_and_initializer()
    {
        LocalVarStatementSyntax statement = Single<LocalVarStatementSyntax>("\tvar/mob/test/t = new\n");

        Assert.Equal("t", statement.Name);
        Assert.Equal("/mob/test", statement.DeclaredType!.Text);
        Assert.IsType<NewExpressionSyntax>(statement.Initializer);
    }

    /// <summary>The slot after <c>var</c> also accepts a space as its separator — PLAN.md §4a.</summary>
    [Fact]
    public void A_local_var_may_use_a_space_separator()
    {
        Assert.Equal("i_dir", Single<LocalVarStatementSyntax>("\tvar i_dir = pick(1, 2)\n").Name);
    }

    /// <summary>A <c>var</c> carrying only modifiers heads a block, and children inherit them.</summary>
    [Fact]
    public void A_var_block_gives_its_modifiers_to_each_child()
    {
        BlockStatementSyntax block =
            Assert.IsType<BlockStatementSyntax>(Assert.Single(InProc("\tvar/tmp\n\t\ta = 1\n\t\tb = 2\n").Statements));

        Assert.Equal(2, block.Statements.Count);
        Assert.All(block.Statements, s => Assert.Contains("tmp", Assert.IsType<LocalVarStatementSyntax>(s).Modifiers));
    }

    /// <summary>A child of a var block can head its own group, acting as a type prefix.</summary>
    [Fact]
    public void A_var_block_child_may_open_a_nested_group()
    {
        BlockStatementSyntax block = Assert.IsType<BlockStatementSyntax>(
            Assert.Single(InProc("\tvar\n\t\tobj/small/egg\n\t\t\tE\n\t\t\tmine = null\n\t\tcount = 0\n").Statements));

        Assert.Equal(4, block.Statements.Count);
    }

    [Fact]
    public void A_var_list_declares_each_name()
    {
        LocalVarStatementSyntax statement = Single<LocalVarStatementSyntax>("\tvar/a = 1, b = 2\n");

        Assert.Equal("a", statement.Name);
        Assert.Equal("b", Assert.Single(statement.Siblings).Name);
    }

    // -- the local-initializer `in` rule -------------------------------------
    // dm.exe 516.1666 rejects a top-level relational `in` after a LOCAL var's initializer with
    // "unexpected 'in' expression", whatever sits on the operator's left — while accepting the
    // same text as a statement, a global, or a type-level var, and accepting the parenthesised
    // whole and the `locate(X) in L` unit here. Fixture: errors/local_in.

    [Theory]
    [InlineData("\tvar/r = y in L\n")]
    [InlineData("\tvar/r = (y) in L\n")]
    [InlineData("\tvar/r = c ? y : 9 in L\n")]
    [InlineData("\tvar/r = (c ? y : 9) in L\n")]
    public void A_local_initializer_rejects_a_top_level_in(string statement)
    {
        Body("/mob/proc/F()\n" + statement, out IReadOnlyList<Diagnostic> diagnostics);

        Diagnostic reported = Assert.Single(diagnostics);
        Assert.Contains("unexpected 'in' expression", reported.Message);
    }

    [Theory]
    [InlineData("\tvar/r = (y in L)\n")]
    [InlineData("\tvar/r = locate(/obj) in L\n")]
    [InlineData("\tvar/r = input(\"t\") in L\n")]
    [InlineData("\tvar/r = input(\"t\") as null|anything in L\n")]
    [InlineData("\tr = c ? y : 9 in L\n")]
    [InlineData("\tfor(var/x = 1 to 3)\n\t\tf()\n")]
    [InlineData("\tfor(var/obj/O = locate() in L)\n\t\tf()\n")]
    public void The_legal_neighbours_stay_clean(string statement)
    {
        Body("/mob/proc/F()\n" + statement, out IReadOnlyList<Diagnostic> diagnostics);

        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// A literal <c>list(...)</c> on the right is the one RHS dm.exe accepts — the value-restriction
    /// clause, which assigns the LEFT value and tests nothing. We match the compiler and warn.
    /// </summary>
    [Fact]
    public void A_list_rhs_is_the_restriction_clause_and_warns()
    {
        Body("/mob/proc/F()\n\tvar/r = 2 in list(4,5)\n", out IReadOnlyList<Diagnostic> diagnostics);

        Diagnostic reported = Assert.Single(diagnostics);
        Assert.Equal("DM0301", reported.Id);
        Assert.Equal(DiagnosticSeverity.Warning, reported.Severity);
    }

    // -- the rand statement -------------------------------------------------

    /// <summary>
    /// <c>rand(…)</c> at statement start governs the ONE expression that follows, wherever it
    /// sits: same line, next line at the same indent, or indented. Probed 2026-08-16 (PLAN §8);
    /// read as an expression statement the indented body was a silent stray block.
    /// </summary>
    [Theory]
    [InlineData("\trand(50)\n\t\tx = 1\n")]
    [InlineData("\trand(50) x = 1\n")]
    [InlineData("\trand(50)\n\tx = 1\n")]
    public void Rand_governs_the_next_expression(string statements)
    {
        RandStatementSyntax rand = Single<RandStatementSyntax>(statements);

        Assert.Equal("rand", Assert.IsType<IdentifierExpressionSyntax>(rand.Call.Target).Name);
        Assert.IsType<AssignmentExpressionSyntax>(rand.Body);
    }

    /// <summary>
    /// The body must be an expression, and dm.exe's own words say which way it failed:
    /// <c>return 1</c> is "missing expression", <c>if(x)</c> "invalid expression", a second
    /// indented line "invalid expression". The non-expression is left for the block, so
    /// <c>return 2</c> still parses as the statement it is.
    /// </summary>
    [Fact]
    public void A_rand_body_that_is_not_an_expression_is_reported_and_left()
    {
        BlockStatementSyntax body = Body(
            "/mob/proc/F()\n\trand(50)\n\treturn 2\n", out IReadOnlyList<Diagnostic> diagnostics);

        Assert.Equal(2, body.Statements.Count);
        Assert.IsType<RandStatementSyntax>(body.Statements[0]);
        Assert.IsType<ReturnStatementSyntax>(body.Statements[1]);
        Assert.Contains(diagnostics, d => d.Message == "missing expression");

        Body("/mob/proc/F()\n\trand(1, 2)\n\t\tx = 1\n\t\tx = 2\n", out diagnostics);
        Assert.Contains(diagnostics, d => d.Message == "invalid expression");
    }

    /// <summary>An expression use of <c>rand()</c> is a call like any other.</summary>
    [Fact]
    public void Rand_in_expression_position_is_not_the_statement()
    {
        Assert.IsType<AssignmentExpressionSyntax>(
            Single<ExpressionStatementSyntax>("\tx = rand(50)\n").Expression);
        Assert.IsType<IfStatementSyntax>(Single<IfStatementSyntax>("\tif(rand(50))\n\t\tx = 1\n"));
    }

    // -- recovery -----------------------------------------------------------

    [Fact]
    public void A_broken_statement_costs_only_its_line()
    {
        BlockStatementSyntax body = Body("/mob/proc/F()\n\ta()\n\t$$$ ???\n\tb()\n", out _);

        // The two good calls survive on either side of the bad line.
        Assert.True(body.Statements.Count >= 2);
    }
}
