using System.Collections.Generic;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
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
}
