using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Syntax;

/// <summary>
/// The parsers stop descending at <see cref="SyntaxFacts.MaxNesting"/> and say so once, rather
/// than overflowing the stack.
/// </summary>
/// <remarks>
/// <para>
/// Every input here overflowed the Debug build on 2026-08-16 — a process crash, which the .NET
/// runtime cannot catch and which across the C ABI kills the host. A test in this class that
/// regresses does not fail; it takes the test host down with it, which is loud enough.
/// </para>
/// <para>
/// The other half of the contract is that the limit invents nothing on code that compiles: a
/// hundred levels is well past anything a real project writes and must parse clean, and dm.exe
/// itself dies between 1,040 and 1,060 nested groups, so no compiling program is deeper than the
/// limit sees. All four corpora hold at zero invented with the guard live.
/// </para>
/// </remarks>
public class NestingLimitTests
{
    private const int Deep = 5000;

    private static IReadOnlyList<Diagnostic> Parse(string source)
        => DeclarationParser.Parse(Lexer.Lex(SourceText.From(source))).Diagnostics;

    private static string Repeat(string text, int count)
    {
        StringBuilder builder = new(text.Length * count);

        for (int i = 0; i < count; i++)
            builder.Append(text);

        return builder.ToString();
    }

    public static TheoryData<string, string> Shapes()
    {
        TheoryData<string, string> data = new();

        data.Add("parentheses", "/proc/f()\n\treturn " + Repeat("(", Deep) + "1" + Repeat(")", Deep) + "\n");
        data.Add("unclosed parentheses", "/proc/f()\n\treturn " + Repeat("(", Deep) + "\n");
        data.Add("calls", "/proc/f()\n\treturn " + Repeat("f(", Deep) + "1" + Repeat(")", Deep) + "\n");
        data.Add("indexes", "/proc/f()\n\treturn L" + Repeat("[L", Deep) + Repeat("]", Deep) + "\n");
        data.Add("list()", "var/x = " + Repeat("list(", Deep) + "1" + Repeat(")", Deep) + "\n");
        data.Add("interpolation", "/proc/f()\n\treturn " + Repeat("\"[", Deep) + "1" + Repeat("]\"", Deep) + "\n");
        data.Add("ternaries", "/proc/f()\n\treturn " + Repeat("1 ? ", Deep) + "1" + Repeat(" : 0", Deep) + "\n");
        data.Add("assignments", "/proc/f()\n\tvar/a\n\ta " + Repeat("= a ", Deep) + "= 1\n");
        data.Add("unary", "/proc/f()\n\treturn " + Repeat("- ", Deep) + "1\n");
        data.Add("statement blocks by indentation",
            "/proc/f()\n" + string.Concat(Enumerable.Range(0, Deep).Select(i => new string('\t', i + 1) + "if(1)\n"))
            + new string('\t', Deep + 1) + "return\n");
        data.Add("statement brace blocks", "/proc/f()\n\t" + Repeat("{", Deep) + "return" + Repeat("}", Deep) + "\n");
        data.Add("types by indentation",
            string.Concat(Enumerable.Range(0, Deep).Select(i => new string('\t', i) + $"sub{i}\n")));
        data.Add("type brace blocks", "obj " + Repeat("{ sub ", Deep) + Repeat("}", Deep) + "\n");

        return data;
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Deep_nesting_is_one_diagnostic_rather_than_a_crash(string shape, string source)
    {
        IReadOnlyList<Diagnostic> diagnostics = Parse(source);

        Assert.True(
            diagnostics.Count(d => d.Id == "DM0205") == 1,
            $"{shape}: expected exactly one DM0205, got {diagnostics.Count(d => d.Id == "DM0205")}");
    }

    /// <summary>
    /// Just under the limit — far past anything written by hand or by macro — parses with nothing
    /// to say, in every counted shape, so the guard is not a diagnostic real code can meet and each
    /// shape costs one level per nesting rather than two.
    /// </summary>
    [Fact]
    public void Just_under_the_limit_parses_clean()
    {
        int n = SyntaxFacts.MaxNesting - 5;

        Assert.Empty(Parse("/proc/f()\n\treturn " + Repeat("(", n) + "1" + Repeat(")", n) + "\n"));
        Assert.Empty(Parse("/proc/f()\n\treturn " + Repeat("f(", n) + "1" + Repeat(")", n) + "\n"));
        Assert.Empty(Parse("/proc/f()\n\treturn " + Repeat("1 ? ", n) + "1" + Repeat(" : 0", n) + "\n"));
        Assert.Empty(Parse("/proc/f()\n\treturn " + Repeat("- ", n) + "1\n"));
        Assert.Empty(Parse(
            "/proc/f()\n" + string.Concat(Enumerable.Range(0, n).Select(i => new string('\t', i + 1) + "if(1)\n"))
            + new string('\t', n + 1) + "return\n"));
        Assert.Empty(Parse(string.Concat(Enumerable.Range(0, n).Select(i => new string('\t', i) + $"sub{i}\n"))));
    }

    /// <summary>
    /// The guard skips the too-deep subtree to its own closer, so the enclosing levels find theirs:
    /// nothing after the deep expression on the same line or the next is lost.
    /// </summary>
    [Fact]
    public void What_follows_the_deep_subtree_still_parses()
    {
        ParseResult result = DeclarationParser.Parse(Lexer.Lex(SourceText.From(
            "/proc/f()\n\tvar/x = " + Repeat("(", Deep) + "1" + Repeat(")", Deep) + "\n\treturn 2\n"
            + "/proc/g()\n\treturn 3\n")));

        Assert.Single(result.Diagnostics);
        Assert.Equal(2, result.Root.Declarations.OfType<ProcDeclarationSyntax>().Count());
        Assert.Equal(2, result.Root.Declarations.OfType<ProcDeclarationSyntax>().First().Body!.Statements.Count);
    }
}
