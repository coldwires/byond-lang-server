using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Core.Tests;

/// <summary>
/// F1 and F5 from <c>docs/dm-format.md</c>, and the guards that keep the formatter off the
/// whitespace that changes a DM program.
/// </summary>
/// <remarks>
/// The guard tests matter more than the rule tests. A formatter that misses a rule is a formatter
/// that did not help; one that reformats a <c>##</c> paste or a ternary colon has changed the
/// program, and in DM both compile.
/// </remarks>
public class FormattingTests
{
    /// <summary>Applies the edits so a test asserts on TEXT rather than on offsets.</summary>
    private static string Formatted(string source, FormatOptions? options = null)
    {
        Document document = new("mem.dm", SourceText.From(source), fromBuffer: true);
        IReadOnlyList<FormatEdit> edits = FormattingService.Format(document, options);

        string text = source;
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            FormatEdit edit = edits[i];
            text = text[..edit.Span.Start] + edit.NewText + text[edit.Span.End..];
        }

        return text;
    }

    // -- F1: spaces around `=` ---------------------------------------------

    [Theory]
    [InlineData("/proc/f()\n\tvar/x=1\n", "/proc/f()\n\tvar/x = 1\n")]
    [InlineData("/proc/f()\n\tvar/x =1\n", "/proc/f()\n\tvar/x = 1\n")]
    [InlineData("/proc/f()\n\tvar/x= 1\n", "/proc/f()\n\tvar/x = 1\n")]
    [InlineData("/proc/f()\n\tx  =  1\n", "/proc/f()\n\tx = 1\n")]
    public void F1_spaces_a_bare_assignment(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    [Fact]
    public void F1_leaves_a_conforming_file_alone()
    {
        Document document = new("mem.dm", SourceText.From("/proc/f()\n\tvar/x = 1\n"), fromBuffer: true);

        Assert.Empty(FormattingService.Format(document));
    }

    /// <summary>
    /// The compound forms are their own token kinds, so matching `Assign` cannot reach them. If
    /// they were matched by text, `+=` would become `+ =` and stop compiling.
    /// </summary>
    [Theory]
    [InlineData("/proc/f()\n\tx+=1\n")]
    [InlineData("/proc/f()\n\tx-=1\n")]
    [InlineData("/proc/f()\n\tx==1\n")]
    [InlineData("/proc/f()\n\tx!=1\n")]
    public void F1_does_not_touch_a_compound_or_comparison(string source)
        => Assert.Equal(source, Formatted(source));

    // -- F2: space after a comma -------------------------------------------

    [Theory]
    [InlineData("/proc/f()\n\treturn list(1,2)\n", "/proc/f()\n\treturn list(1, 2)\n")]
    [InlineData("/proc/f()\n\treturn list(1 ,2)\n", "/proc/f()\n\treturn list(1, 2)\n")]
    [InlineData("/proc/f()\n\treturn list(1  ,  2)\n", "/proc/f()\n\treturn list(1, 2)\n")]
    [InlineData("/proc/f(a,b)\n\treturn a\n", "/proc/f(a, b)\n\treturn a\n")]
    public void F2_spaces_after_a_comma_and_not_before(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// A list that ends on a comma keeps its closer tight: `f(a, )` is legal DM and reads as an
    /// oversight rather than as formatting.
    /// </summary>
    [Fact]
    public void F2_leaves_a_trailing_comma_tight_against_its_closer()
        => Assert.Equal("/proc/f()\n\treturn list(1,)\n", Formatted("/proc/f()\n\treturn list(1,)\n"));

    /// <summary>
    /// The guard that was vacuous until F2 existed. `#define PAIR(a,b) a##b` must survive
    /// untouched: spacing the parameter list changes what the macro accepts, and spacing the
    /// paste stops it gluing at all.
    /// </summary>
    [Fact]
    public void F2_does_not_reach_inside_a_macro_parameter_list()
        => Assert.Equal("#define PAIR(a,b) a##b\n", Formatted("#define PAIR(a,b) a##b\n"));

    /// <summary>
    /// A comma that is string CONTENT is part of a string token, so no gap exists for a rule to
    /// write into — including in an interpolated string, where the literal runs between holes are
    /// their own tokens.
    /// </summary>
    [Theory]
    [InlineData("/proc/f()\n\treturn \"a,b\"\n")]
    [InlineData("/proc/f(x, y)\n\treturn \"[x],[y]\"\n")]
    public void F2_does_not_reach_inside_a_string(string source)
        => Assert.Equal(source, Formatted(source));

    // -- F3: spaces around binary arithmetic -------------------------------

    [Theory]
    [InlineData("/proc/f(a, b)\n\treturn a+b\n", "/proc/f(a, b)\n\treturn a + b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a-b\n", "/proc/f(a, b)\n\treturn a - b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a*b\n", "/proc/f(a, b)\n\treturn a * b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a%b\n", "/proc/f(a, b)\n\treturn a % b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a**b\n", "/proc/f(a, b)\n\treturn a ** b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a  +  b\n", "/proc/f(a, b)\n\treturn a + b\n")]
    public void F3_spaces_a_binary_operator(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// A unary operator keeps its operand: `-1` must not become `- 1`.
    /// </summary>
    [Theory]
    [InlineData("/proc/f()\n\treturn -1\n")]
    [InlineData("/proc/f(a)\n\treturn a * -1\n")]
    [InlineData("/proc/f(a, b)\n\treturn a - -b\n")]
    public void F3_leaves_a_unary_minus_alone(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>
    /// DM has pointers, and their operators are the binary ones wearing a different hat: unary
    /// `*` dereferences and unary `&amp;` takes a reference, at precedence 4 while the binary twins
    /// sit at 6 and 11. Spacing `*p` into `* p` would be a different program.
    /// </summary>
    [Theory]
    [InlineData("/proc/f()\n\tvar/x = 5\n\tvar/p = &x\n\t*p = 99\n\treturn x\n")]
    [InlineData("/proc/f()\n\tvar/x = 5\n\tvar/p = &x\n\treturn *p\n")]
    public void F3_leaves_a_pointer_operator_alone(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>
    /// `/` is not an F3 operator: in DM it is overwhelmingly a path separator, and the first
    /// survey of this corpus counted `/mob/pc` as division 3,836 times before that was caught.
    /// </summary>
    [Theory]
    [InlineData("/proc/f()\n\tvar/mob/pc/m = null\n\treturn m\n")]
    [InlineData("/proc/f(a, b)\n\treturn a/b\n")]
    public void F3_never_touches_a_slash(string source)
        => Assert.Equal(source, Formatted(source));

    [Theory]
    [InlineData("/proc/f(a)\n\ta+=1\n\treturn a\n")]
    [InlineData("/proc/f(a)\n\ta++\n\treturn a\n")]
    public void F3_does_not_touch_a_compound_or_increment(string source)
        => Assert.Equal(source, Formatted(source));

    // -- F4 / F10: no space before a control keyword's paren ---------------

    [Theory]
    [InlineData("/proc/f(a)\n\tif (a)\n\t\treturn 1\n", "/proc/f(a)\n\tif(a)\n\t\treturn 1\n")]
    [InlineData("/proc/f(a)\n\twhile (a)\n\t\treturn 1\n", "/proc/f(a)\n\twhile(a)\n\t\treturn 1\n")]
    [InlineData("/proc/f(a)\n\tfor (a)\n\t\treturn 1\n", "/proc/f(a)\n\tfor(a)\n\t\treturn 1\n")]
    [InlineData("/proc/f(a)\n\tif   (a)\n\t\treturn 1\n", "/proc/f(a)\n\tif(a)\n\t\treturn 1\n")]
    public void F4_tightens_a_keyword_against_its_paren(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// F10: `switch` rides F4. Measured at only 57% tight, so this is consistency with the other
    /// three rather than a convention read off the code.
    /// </summary>
    [Fact]
    public void F10_tightens_switch_too()
        => Assert.Equal(
            "/proc/f(a)\n\tswitch(a)\n\t\tif(1)\n\t\t\treturn 1\n",
            Formatted("/proc/f(a)\n\tswitch (a)\n\t\tif (1)\n\t\t\treturn 1\n"));

    /// <summary>
    /// An ordinary call keeps whatever the author wrote. F4 is about the control heads, and
    /// tightening every identifier before a `(` would reformat call sites the spec never claimed.
    /// </summary>
    [Fact]
    public void F4_does_not_tighten_an_ordinary_call()
        => Assert.Equal("/proc/f()\n\treturn list (1)\n", Formatted("/proc/f()\n\treturn list (1)\n"));

    // -- F5: trailing whitespace -------------------------------------------

    [Fact]
    public void F5_trims_trailing_whitespace()
        => Assert.Equal("/proc/f()\n\treturn 1\n", Formatted("/proc/f()   \n\treturn 1\t\n"));

    // -- the guards --------------------------------------------------------

    /// <summary>
    /// A `##` paste is whitespace-sensitive — `a##b` glues and `a ## b` does not — so a directive
    /// line is skipped whole. This project has already paid 32 invented diagnostics for a lost
    /// whitespace fact on that path.
    /// </summary>
    /// <remarks>
    /// <b>Was vacuous until F2, and is not any more.</b> With only F1 and F5 implemented this
    /// passed even with the directive guard removed — nothing touched `##` or the comma in a
    /// parameter list. F2 spaces commas, so `PAIR(a,b)` became reachable, and the control run now
    /// fails all three directive tests instead of one. Recorded because the prediction was made
    /// before the rule landed and then checked rather than assumed.
    /// </remarks>
    [Fact]
    public void A_preprocessor_line_is_never_touched()
        => Assert.Equal("#define PAIR(a,b) a##b\n", Formatted("#define PAIR(a,b) a##b\n"));

    [Fact]
    public void A_define_with_a_tight_assignment_is_left_alone()
        => Assert.Equal("#define GREET x=1\n", Formatted("#define GREET x=1\n"));

    /// <summary>
    /// Leading indentation is semantic in DM: a `proc` block indented one level too far into a
    /// `var` block declares nothing and compiles clean. F7 holds it in v1.
    /// </summary>
    [Fact]
    public void Leading_indentation_is_never_touched()
    {
        const string source = "/datum/d\n\tvar\n\t\tkept = 1\n";

        Assert.Equal(source, Formatted(source));
    }

    [Fact]
    public void A_string_interior_is_never_touched()
        => Assert.Equal("/proc/f()\n\treturn \"a=1,  b\"\n", Formatted("/proc/f()\n\treturn \"a=1,  b\"\n"));

    /// <summary>
    /// `1 ? b : c` is a conditional and `1 ? b:c` is a compile error, so whitespace around a colon
    /// is never touched — the one place in DM where spacing changes a parse.
    /// </summary>
    [Fact]
    public void Whitespace_around_a_colon_is_never_touched()
    {
        const string source = "/proc/f(c, b, d)\n\treturn c ? b : d\n";

        Assert.Equal(source, Formatted(source));
    }

    /// <summary>
    /// The whitespace after a `\` continuation is discarded by the compiler, so what looks like
    /// layout is string content. Gaps carrying one are skipped.
    /// </summary>
    [Fact]
    public void A_continuation_is_never_touched()
    {
        const string source = "/proc/f()\n\tvar/s = \"one \\\n\ttwo\"\n";

        Assert.Equal(source, Formatted(source));
    }

    [Fact]
    public void Options_can_turn_every_rule_off()
    {
        const string source = "/proc/f()\n\tvar/x=1   \n";

        Assert.Equal(source, Formatted(source, FormatOptions.None));
    }
}
