using System.Collections.Generic;
using System.Linq;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Syntax;

/// <summary>
/// Covers the precedence table in PLAN.md §4c and the shapes §8 established by compiling.
/// </summary>
public class ExpressionParserTests
{
    private static ExpressionSyntax Parse(string source, out IReadOnlyList<Diagnostic> diagnostics)
    {
        TokenSource tokenSource = TokenSource.FromLex(Lexer.Lex(SourceText.From(source)));
        List<Diagnostic> collected = new();

        (ExpressionSyntax expression, _) = ExpressionParser.Parse(tokenSource.Tokens, tokenSource, collected, 0);
        diagnostics = collected;
        return expression;
    }

    private static ExpressionSyntax Parse(string source)
    {
        ExpressionSyntax expression = Parse(source, out IReadOnlyList<Diagnostic> diagnostics);
        Assert.Empty(diagnostics);
        return expression;
    }

    // -- the precedence traps ----------------------------------------------

    /// <summary>
    /// <c>in</c> is the lowest-precedence operator in the language, below assignment. Compiler
    /// verified: <c>var/whole = (has = 2 in L)</c> leaves <c>has</c> holding 2, so it parsed as
    /// <c>(has = 2) in L</c>. Getting this wrong produces a plausible-looking wrong answer.
    /// </summary>
    [Fact]
    public void In_binds_looser_than_assignment()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("has = 2 in L"));

        Assert.Equal(TokenKind.KeywordIn, root.OperatorToken);
        Assert.IsType<AssignmentExpressionSyntax>(root.Left);
        Assert.Equal("L", Assert.IsType<IdentifierExpressionSyntax>(root.Right).Name);
    }

    /// <summary>The same trap catches negation: <c>if(!A in L)</c> is <c>if((!A) in L)</c>.</summary>
    [Fact]
    public void In_binds_looser_than_negation()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("!A in L"));

        Assert.Equal(TokenKind.KeywordIn, root.OperatorToken);
        Assert.Equal(UnaryOperatorKind.Not, Assert.IsType<UnaryExpressionSyntax>(root.Left).Kind);
    }

    /// <summary>
    /// Unary <c>*</c> and <c>&amp;</c> are pointer operators at level 4; the same glyphs are binary
    /// at levels 6 and 11. DM has real pointers from 515 — <c>var/p = &amp;x</c> then <c>*p = 99</c>.
    /// </summary>
    [Theory]
    [InlineData("*p", UnaryOperatorKind.Dereference)]
    [InlineData("&x", UnaryOperatorKind.AddressOf)]
    public void Star_and_amp_are_pointer_operators_in_unary_position(string source, UnaryOperatorKind expected)
    {
        Assert.Equal(expected, Assert.IsType<UnaryExpressionSyntax>(Parse(source)).Kind);
    }

    [Theory]
    [InlineData("a * b", TokenKind.Star)]
    [InlineData("a & b", TokenKind.Amp)]
    [InlineData("a %% b", TokenKind.PercentPercent)]
    [InlineData("a <=> b", TokenKind.Spaceship)]
    public void The_same_glyphs_are_binary_between_operands(string source, TokenKind expected)
    {
        Assert.Equal(expected, Assert.IsType<BinaryExpressionSyntax>(Parse(source)).OperatorToken);
    }

    /// <summary><c>~=</c> is an equivalence test at level 10, not a compound assignment.</summary>
    [Fact]
    public void Equivalence_is_a_test_not_an_assignment()
    {
        Assert.IsType<BinaryExpressionSyntax>(Parse("a ~= b"));
        Assert.IsType<AssignmentExpressionSyntax>(Parse("a += b"));
    }

    // -- ordinary precedence ------------------------------------------------

    [Fact]
    public void Multiplication_binds_tighter_than_addition()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("a + b * c"));

        Assert.Equal(TokenKind.Plus, root.OperatorToken);
        Assert.Equal(TokenKind.Star, Assert.IsType<BinaryExpressionSyntax>(root.Right).OperatorToken);
    }

    [Fact]
    public void And_binds_tighter_than_or()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("a || b && c"));

        Assert.Equal(TokenKind.OrOr, root.OperatorToken);
        Assert.Equal(TokenKind.AndAnd, Assert.IsType<BinaryExpressionSyntax>(root.Right).OperatorToken);
    }

    [Fact]
    public void Binary_operators_are_left_associative()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("a - b - c"));

        Assert.IsType<BinaryExpressionSyntax>(root.Left);
        Assert.IsType<IdentifierExpressionSyntax>(root.Right);
    }

    /// <summary>Assignment is the one right-associative level, so <c>a = b = c</c> is <c>a = (b = c)</c>.</summary>
    [Fact]
    public void Assignment_is_right_associative()
    {
        AssignmentExpressionSyntax root = Assert.IsType<AssignmentExpressionSyntax>(Parse("a = b = c"));

        Assert.IsType<IdentifierExpressionSyntax>(root.Target);
        Assert.IsType<AssignmentExpressionSyntax>(root.Value);
    }

    [Fact]
    public void Conditional_parses_all_three_parts()
    {
        ConditionalExpressionSyntax root = Assert.IsType<ConditionalExpressionSyntax>(Parse("a ? b : c"));

        Assert.Equal("a", Assert.IsType<IdentifierExpressionSyntax>(root.Condition).Name);
        Assert.Equal("b", Assert.IsType<IdentifierExpressionSyntax>(root.WhenTrue).Name);
        Assert.Equal("c", Assert.IsType<IdentifierExpressionSyntax>(root.WhenFalse).Name);
    }

    /// <summary>
    /// Whitespace before the colon decides whether it closes a conditional or starts a member
    /// access — the one place in DM where spacing changes a parse. All four spacings were compiled
    /// against dm.exe 516.1666 with <c>b:c</c> a valid member access.
    /// </summary>
    [Theory]
    [InlineData("1 ? b : c")]
    [InlineData("1 ? b :c")]
    public void A_spaced_colon_closes_the_conditional(string source)
    {
        Assert.IsType<ConditionalExpressionSyntax>(Parse(source));
    }

    /// <summary>
    /// <c>1 ? b:c</c> takes the colon as member access, which leaves the conditional without one.
    /// dm.exe reports "expected ':'" on exactly these two spacings.
    /// </summary>
    [Theory]
    [InlineData("1 ? b:c")]
    [InlineData("1 ? b: c")]
    public void A_tight_colon_is_member_access_and_leaves_the_conditional_incomplete(string source)
    {
        ExpressionSyntax expression = Parse(source, out IReadOnlyList<Diagnostic> diagnostics);

        ConditionalExpressionSyntax conditional = Assert.IsType<ConditionalExpressionSyntax>(expression);
        Assert.IsType<MemberAccessExpressionSyntax>(conditional.WhenTrue);
        Assert.Contains(diagnostics, d => d.Message.Contains("':'"));
    }

    /// <summary>Compiler verified: <c>2 ** 3 ** 2</c> is 64, so <c>**</c> is left-associative.</summary>
    [Fact]
    public void Exponentiation_is_left_associative()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("2 ** 3 ** 2"));

        Assert.Equal(TokenKind.StarStar, root.OperatorToken);
        Assert.IsType<BinaryExpressionSyntax>(root.Left);
        Assert.Equal("2", Assert.IsType<LiteralExpressionSyntax>(root.Right).Text);
    }

    /// <summary>Compiler verified: <c>-2 ** 2</c> is 4, so the unary minus binds tighter.</summary>
    [Fact]
    public void Unary_minus_binds_tighter_than_exponentiation()
    {
        BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(Parse("-2 ** 2"));

        Assert.Equal(TokenKind.StarStar, root.OperatorToken);
        Assert.Equal(UnaryOperatorKind.Negate, Assert.IsType<UnaryExpressionSyntax>(root.Left).Kind);
    }

    // -- member access ------------------------------------------------------

    /// <summary>
    /// <c>.</c> checks the declared type, <c>:</c> widens the check to its subtypes. They are
    /// different checks, so they stay different nodes — PLAN.md §4a.
    /// </summary>
    [Theory]
    [InlineData("a.b", MemberAccessKind.Dot)]
    [InlineData("a:b", MemberAccessKind.Colon)]
    [InlineData("a?.b", MemberAccessKind.NullDot)]
    [InlineData("a?:b", MemberAccessKind.NullColon)]
    public void Member_access_forms_stay_distinct(string source, MemberAccessKind expected)
    {
        MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(Parse(source));

        Assert.Equal(expected, access.Kind);
        Assert.Equal("b", access.Name);
    }

    /// <summary><c>A::B()</c> names a proc rather than calling it, so it is not an invocation.</summary>
    [Fact]
    public void Scope_with_parens_is_a_proc_reference_not_a_call()
    {
        MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(Parse("A::B()"));

        Assert.Equal(MemberAccessKind.Scope, access.Kind);
        Assert.True(access.IsProcReference);
    }

    [Fact]
    public void A_leading_scope_form_has_no_target()
    {
        MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(Parse("::A"));

        Assert.Null(access.Target);
        Assert.Equal("A", access.Name);
    }

    [Theory]
    [InlineData("L[1]", false)]
    [InlineData("L?[1]", true)]
    public void Indexing_records_the_null_conditional_form(string source, bool nullConditional)
    {
        Assert.Equal(nullConditional, Assert.IsType<IndexExpressionSyntax>(Parse(source)).IsNullConditional);
    }

    // -- primaries ----------------------------------------------------------

    /// <summary>
    /// Mid-path <c>/</c> and <c>.</c> are the same separator, so these produce identical segments.
    /// Compiler verified: <c>/obj/item/sword == /obj.item.sword</c> is 1.
    /// </summary>
    [Theory]
    [InlineData("/obj/item/sword")]
    [InlineData("/obj.item.sword")]
    [InlineData("/obj/item.sword")]
    public void Mid_path_separators_are_interchangeable(string source)
    {
        PathExpressionSyntax path = Assert.IsType<PathExpressionSyntax>(Parse(source));

        Assert.Equal(PathAnchor.Absolute, path.Path.Anchor);
        Assert.Equal(new[] { "obj", "item", "sword" }, path.Path.Segments.ToArray());
    }

    /// <summary>A leading <c>.</c> is an upward search, which a mid-path <c>.</c> is not.</summary>
    [Fact]
    public void A_leading_dot_path_is_an_upward_search()
    {
        PathExpressionSyntax path = Assert.IsType<PathExpressionSyntax>(Parse(".sword"));

        Assert.Equal(PathAnchor.UpwardSearch, path.Path.Anchor);
    }

    /// <summary>A bare <c>.</c> is the enclosing proc's return value, not a path.</summary>
    [Fact]
    public void A_bare_dot_is_the_return_value()
    {
        Assert.IsType<ReturnValueExpressionSyntax>(Parse("."));
    }

    [Fact]
    public void New_takes_a_path_and_arguments()
    {
        NewExpressionSyntax expression = Assert.IsType<NewExpressionSyntax>(Parse("new /obj/thing(1, 2)"));

        Assert.Equal("/obj/thing", Assert.IsType<PathExpressionSyntax>(expression.Type).Path.Text);
        Assert.Equal(2, expression.Arguments.Count);
    }

    /// <summary><c>var/list/L = new</c> — the target's declared type supplies the type.</summary>
    [Fact]
    public void New_may_name_no_type_at_all()
    {
        NewExpressionSyntax expression = Assert.IsType<NewExpressionSyntax>(Parse("new"));

        Assert.Null(expression.Type);
        Assert.Empty(expression.Arguments);
    }

    /// <summary>
    /// Modified-type initialisers, <c>new /obj/thing{hp = 42; label = "set"}</c>. Braces are
    /// mandatory here and <c>;</c> separates entries written on one line — compiler verified.
    /// </summary>
    [Fact]
    public void New_accepts_a_modified_type_initializer()
    {
        NewExpressionSyntax expression =
            Assert.IsType<NewExpressionSyntax>(Parse("new /obj/thing{hp = 42; label = \"set\"}"));

        ModifiedTypeExpressionSyntax modified = Assert.IsType<ModifiedTypeExpressionSyntax>(expression.Type);
        Assert.Equal(2, modified.Assignments.Count);
    }

    /// <summary>Empty parentheses forward the current arguments, so this is not a zero-argument call.</summary>
    [Fact]
    public void Parent_call_parses_with_empty_parens()
    {
        Assert.Empty(Assert.IsType<ParentCallExpressionSyntax>(Parse("..()")).Arguments);
    }

    /// <summary><c>list(a = 1)</c> builds an associative entry, so the key is kept separate.</summary>
    [Fact]
    public void Associative_arguments_keep_their_key()
    {
        InvocationExpressionSyntax call = Assert.IsType<InvocationExpressionSyntax>(Parse("list(a = 1, 2)"));

        Assert.Equal(2, call.Arguments.Count);
        Assert.Equal("a", Assert.IsType<IdentifierExpressionSyntax>(call.Arguments[0].Name).Name);
        Assert.Null(call.Arguments[1].Name);
    }

    [Fact]
    public void Interpolation_holes_parse_as_expressions()
    {
        InterpolatedStringExpressionSyntax text =
            Assert.IsType<InterpolatedStringExpressionSyntax>(Parse("\"hp [src.hp] left\""));

        InterpolatedStringPartSyntax hole = Assert.Single(text.Parts, p => p.Expression is not null);
        MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(hole.Expression);
        Assert.Equal("hp", access.Name);
    }

    [Fact]
    public void A_string_without_holes_is_a_literal()
    {
        Assert.Equal(LiteralKind.String, Assert.IsType<LiteralExpressionSyntax>(Parse("\"plain\"")).Kind);
    }

    [Theory]
    [InlineData("42", LiteralKind.Number)]
    [InlineData("0x1F", LiteralKind.Number)]
    [InlineData("null", LiteralKind.Null)]
    [InlineData("'icons/mob.dmi'", LiteralKind.Resource)]
    public void Literals_carry_their_kind(string source, LiteralKind expected)
    {
        Assert.Equal(expected, Assert.IsType<LiteralExpressionSyntax>(Parse(source)).Kind);
    }

    /// <summary><c>input(...) as text|null</c> — the clause belongs to the call it follows.</summary>
    [Fact]
    public void An_as_clause_collects_its_input_types()
    {
        AsExpressionSyntax expression = Assert.IsType<AsExpressionSyntax>(Parse("input(\"pick\") as text|null"));

        Assert.Equal(new[] { "text", "null" }, expression.InputTypes.ToArray());
        Assert.IsType<InvocationExpressionSyntax>(expression.Expression);
    }

    // -- spans --------------------------------------------------------------

    /// <summary>
    /// A postfix form spans from where its <b>target</b> begins. The span used to start at the
    /// operator, so an invocation covered <c>(1, 2)</c> rather than <c>f(1, 2)</c> — and that span
    /// is what a hover range, a go-to-definition range and any diagnostic on a call point at.
    /// </summary>
    [Theory]
    [InlineData("f(1, 2)")]
    [InlineData("L[1]")]
    [InlineData("a.b")]
    [InlineData("a.b()")]
    [InlineData("obj.name++")]
    [InlineData("input(\"pick\") as text")]
    [InlineData("f(1)(2)")]
    public void A_postfix_expression_spans_its_whole_text(string source)
    {
        ExpressionSyntax expression = Parse(source);

        Assert.Equal(0, expression.Span.Start);
        Assert.Equal(source.Length, expression.Span.End);
    }

    /// <summary>The target keeps its own span, so a caller can still point at the callee alone.</summary>
    [Fact]
    public void The_target_of_a_call_keeps_its_own_span()
    {
        InvocationExpressionSyntax call = Assert.IsType<InvocationExpressionSyntax>(Parse("myproc(1)"));

        Assert.Equal(0, call.Target.Span.Start);
        Assert.Equal("myproc".Length, call.Target.Span.End);
    }

    // -- recovery -----------------------------------------------------------

    /// <summary>An editor buffer is malformed on every keystroke, so a bad operand still returns a node.</summary>
    [Fact]
    public void An_unparseable_operand_reports_and_returns_a_node()
    {
        ExpressionSyntax expression = Parse("a + ", out IReadOnlyList<Diagnostic> diagnostics);

        Assert.NotEmpty(diagnostics);
        Assert.IsType<BinaryExpressionSyntax>(expression);
    }

    [Fact]
    public void An_unclosed_call_reports_rather_than_hanging()
    {
        Parse("f(1, 2", out IReadOnlyList<Diagnostic> diagnostics);

        Assert.NotEmpty(diagnostics);
    }
}
