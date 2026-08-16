using System.Collections.Generic;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Binding;

/// <summary>
/// Every expected value here was READ OFF 516.1687 rather than computed in C#, because DM's
/// arithmetic is not C#'s: 32-bit floats, six significant digits, a truncating <c>%</c>, and a
/// left-associative <c>**</c>. The same expressions run in <c>ok/constants.dm</c>, so the compiler
/// re-asserts them on every build that has BYOND.
/// </summary>
public class ConstantEvaluatorTests
{
    private static string? Fold(string source)
    {
        TokenSource tokens = TokenSource.FromLex(Lexer.Lex(SourceText.From(source)));
        List<Diagnostic> ignored = new();

        (ExpressionSyntax expression, _) = ExpressionParser.Parse(tokens.Tokens, tokens, ignored, 0);
        return ConstantEvaluator.Fold(expression);
    }

    [Theory]
    [InlineData("5 + 1", "6")]
    [InlineData("5 * 60", "300")]
    [InlineData("1 / 3", "0.333333")]
    [InlineData("1 / 10", "0.1")]
    [InlineData("0.1 + 0.2", "0.3")]
    [InlineData("2 ** 0.5", "1.41421")]
    [InlineData("1 << 10", "1024")]
    [InlineData("5 > 3", "1")]
    [InlineData("\"a\" + \"b\"", "ab")]
    public void It_folds_what_dm_folds(string source, string expected)
        => Assert.Equal(expected, Fold(source));

    /// <summary>
    /// The four DM-specific ones. `%` truncates BOTH operands, `%%` does not; `**` is
    /// left-associative and unary minus binds tighter than it. Getting any of them the C way round
    /// produces a wrong number with nothing to say so.
    /// </summary>
    [Theory]
    [InlineData("7.5 % 2", "1")]
    [InlineData("7.5 %% 2", "1.5")]
    [InlineData("2 ** 3 ** 2", "64")]
    [InlineData("-2 ** 2", "4")]
    public void Dm_arithmetic_is_not_c_arithmetic(string source, string expected)
        => Assert.Equal(expected, Fold(source));

    /// <summary>
    /// Six significant digits, scientific beyond them - so a large integer does NOT round-trip,
    /// which is exactly why a bare literal is left alone.
    /// </summary>
    [Fact]
    public void A_large_value_renders_the_way_dm_renders_it()
        => Assert.Equal("1.23457e+08", Fold("123456789 + 0"));

    /// <summary>
    /// A literal is its own best rendering: folding it would replace the author's text with
    /// something no better, and for a large number with something visibly worse.
    /// </summary>
    [Theory]
    [InlineData("5")]
    [InlineData("123456789")]
    [InlineData("\"text\"")]
    public void A_bare_literal_is_not_folded(string source)
        => Assert.Null(Fold(source));

    /// <summary>
    /// Nothing that needs the runtime, a symbol table or a list is constant. Each of these is a
    /// var dm.exe itself initialises in a hidden init proc rather than folding.
    /// </summary>
    [Theory]
    [InlineData("new /obj/item")]
    [InlineData("list(1, 2, 3)")]
    [InlineData("list()")]
    [InlineData("/obj/item")]
    [InlineData("some_name + 1")]
    [InlineData("world.maxx")]
    public void What_is_not_constant_folds_to_nothing(string source)
        => Assert.Null(Fold(source));

    /// <summary>A constant condition picks a branch, since both sides are in the source.</summary>
    [Fact]
    public void A_constant_ternary_picks_its_branch()
    {
        Assert.Equal("10", Fold("1 ? 10 : 20"));
        Assert.Equal("20", Fold("0 ? 10 : 20"));
    }

    /// <summary>Division by zero is a runtime error in DM, so it is not a constant here.</summary>
    [Fact]
    public void Division_by_zero_is_not_folded()
        => Assert.Null(Fold("1 / 0"));

    // -- names, resolved through the tree ---------------------------------------
    //
    // The per-file fold cannot answer a name; the tree finishes it. Everything below mirrors
    // errors/const_fold and ok/constants.dm, which pin the same facts against dm.exe: it FOLDS an
    // initialiser naming a const through every scope (init_proc stays silent under a live pragma),
    // and a non-const name there is "expected a constant expression".

    private static ObjectTree Tree(string source)
    {
        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);
        return tree;
    }

    private static string ConstantOf(ObjectTree tree, string owner, string name)
    {
        TypeSymbol type = tree.Find(owner)!;
        return tree.ConstantValueOf(type, type.FindVar(name)!);
    }

    private const string Scopes =
        "var/const/GLOBAL_MAX = 100\n"
        + "var/const/GLOBAL_HALF = GLOBAL_MAX / 2\n"
        + "var/const/STR_C = \"ab\"\n"
        + "/turf/probe\n"
        + "\tvar/const/TYPE_MAX = 40\n"
        + "\tvar/const/TYPE_TWICE = TYPE_MAX * 2\n"
        + "\tvar/own = TYPE_MAX - 5\n"
        + "\tvar/global_c = GLOBAL_MAX + 1\n"
        + "\tvar/const_of_const = TYPE_TWICE + GLOBAL_HALF\n"
        + "\tvar/bare = TYPE_MAX\n"
        + "\tvar/str = STR_C + \"x\"\n"
        + "\tvar/scoped = /turf/probe/child::TYPE_MAX + 1\n"
        + "/turf/probe/child\n"
        + "\tvar/inherited = TYPE_MAX + 1\n";

    [Theory]
    [InlineData("/turf/probe", "own", "35")]
    [InlineData("/turf/probe", "global_c", "101")]
    [InlineData("/turf/probe", "const_of_const", "130")]
    [InlineData("/turf/probe", "bare", "40")]
    [InlineData("/turf/probe", "str", "abx")]
    [InlineData("/turf/probe", "scoped", "41")]
    [InlineData("/turf/probe/child", "inherited", "41")]
    [InlineData("/", "GLOBAL_HALF", "50")]
    public void A_const_by_name_folds_through_every_scope(string owner, string name, string expected)
        => Assert.Equal(expected, ConstantOf(Tree(Scopes), owner, name));

    /// <summary>
    /// The per-file answer is left alone where a name is not involved: a bare literal still folds
    /// to nothing, and literal arithmetic still folds.
    /// </summary>
    [Fact]
    public void The_eager_fold_stands_where_nothing_is_named()
    {
        ObjectTree tree = Tree("/turf/probe\n\tvar/a = 5\n\tvar/b = 5 * 60\n");
        Assert.Equal(string.Empty, ConstantOf(tree, "/turf/probe", "a"));
        Assert.Equal("300", ConstantOf(tree, "/turf/probe", "b"));
    }

    /// <summary>
    /// A NON-const name is not folded, because dm.exe rejects it ("expected a constant
    /// expression" - errors/const_nonconst) and a value here would be one the program never has.
    /// A bare override on a subtype is untyped and un-const, so it stops the search too.
    /// </summary>
    [Fact]
    public void A_non_const_name_is_not_folded()
    {
        ObjectTree tree = Tree(
            "var/plain = 7\n/datum/holder\n\tvar/from_plain = plain + 1\n"
            + "\tvar/const/K = 3\n/datum/holder/sub\n\tK = 4\n\tvar/through_override = K + 1\n");

        Assert.Equal(string.Empty, ConstantOf(tree, "/datum/holder", "from_plain"));
        Assert.Equal(string.Empty, ConstantOf(tree, "/datum/holder/sub", "through_override"));
    }

    /// <summary>
    /// The 32-bit value travels between consts, not the six-digit rendering: 1/3 renders as
    /// 0.333333, and three of it must still be 1.
    /// </summary>
    [Fact]
    public void A_const_of_a_const_carries_the_float_not_the_rendering()
    {
        ObjectTree tree = Tree("var/const/THIRD = 1 / 3\nvar/const/WHOLE = THIRD * 3\n");
        Assert.Equal("0.333333", ConstantOf(tree, "/", "THIRD"));
        Assert.Equal("1", ConstantOf(tree, "/", "WHOLE"));
    }

    /// <summary>A cycle is a compile error in DM and must not be a stack overflow here.</summary>
    [Fact]
    public void A_cycle_folds_to_nothing()
    {
        ObjectTree tree = Tree("var/const/A = B + 1\nvar/const/B = A + 1\n");
        Assert.Equal(string.Empty, ConstantOf(tree, "/", "A"));
        Assert.Equal(string.Empty, ConstantOf(tree, "/", "B"));
    }
}
