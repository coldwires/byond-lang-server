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
    public void Del_and_throw_take_a_bare_operand(string source, TokenKind expected)
    {
        UnaryStatementSyntax statement = Single<UnaryStatementSyntax>(source);

        Assert.Equal(expected, statement.Keyword);
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

    // -- recovery -----------------------------------------------------------

    [Fact]
    public void A_broken_statement_costs_only_its_line()
    {
        BlockStatementSyntax body = Body("/mob/proc/F()\n\ta()\n\t$$$ ???\n\tb()\n", out _);

        // The two good calls survive on either side of the bad line.
        Assert.True(body.Statements.Count >= 2);
    }
}
