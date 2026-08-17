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
    [InlineData("/proc/f()\n\tx*=1\n")]
    [InlineData("/proc/f()\n\tx||=1\n")]
    public void F1_does_not_touch_a_compound_assignment(string source)
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

    // -- F8: spaces around comparison and logical operators ----------------

    [Theory]
    [InlineData("/proc/f(a, b)\n\treturn a==b\n", "/proc/f(a, b)\n\treturn a == b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a!=b\n", "/proc/f(a, b)\n\treturn a != b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a<>b\n", "/proc/f(a, b)\n\treturn a <> b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a<b\n", "/proc/f(a, b)\n\treturn a < b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a>=b\n", "/proc/f(a, b)\n\treturn a >= b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a<=>b\n", "/proc/f(a, b)\n\treturn a <=> b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a&&b\n", "/proc/f(a, b)\n\treturn a && b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a||b\n", "/proc/f(a, b)\n\treturn a || b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a  ==  b\n", "/proc/f(a, b)\n\treturn a == b\n")]
    public void F8_spaces_a_comparison_or_logical_operator(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// `!` is unary, so F8 leaves it alone — and the bitwise family with it: `&amp;x` takes a
    /// reference, `|` separates the `as num|text` input filters, and `&lt;&lt;` is DM's output
    /// operator as often as a shift.
    /// </summary>
    [Theory]
    [InlineData("/proc/f(a)\n\treturn !a\n")]
    [InlineData("/proc/f(a, b)\n\treturn a&b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a|b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a^b\n")]
    [InlineData("/proc/f(a, b)\n\treturn a<<b\n")]
    [InlineData("/mob/verb/say(msg as text|null)\n\tusr<<msg\n")]
    public void F8_leaves_the_unary_and_bitwise_operators_alone(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>
    /// An `#include &lt;lib&gt;` carries a `&lt;` and a `&gt;` that are not comparisons at all.
    /// The directive guard covers it, which is worth pinning: F8 is the first rule whose operators
    /// appear on a directive line in ordinary code.
    /// </summary>
    [Fact]
    public void F8_does_not_reach_inside_an_include()
        => Assert.Equal("#include <deadron/characterhandling>\n", Formatted("#include <deadron/characterhandling>\n"));

    // -- F9: one space after a line comment's slashes -----------------------

    [Theory]
    [InlineData("/proc/f()\n\t//comment\n\treturn 1\n", "/proc/f()\n\t// comment\n\treturn 1\n")]
    [InlineData("//header\n/proc/f()\n\treturn 1\n", "// header\n/proc/f()\n\treturn 1\n")]
    [InlineData("/proc/f()\n\treturn 1 //trailing\n", "/proc/f()\n\treturn 1 // trailing\n")]
    public void F9_spaces_a_line_comment(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// A `///` doc comment is the same rule one slash further along: hover and completion read
    /// these, and splitting the marker into `// /` would stop them being doc comments at all.
    /// </summary>
    [Fact]
    public void F9_steps_over_the_whole_run_of_slashes()
        => Assert.Equal("/// The hit points.\n/mob\n\tvar/hp = 1\n", Formatted("///The hit points.\n/mob\n\tvar/hp = 1\n"));

    /// <summary>
    /// Insert-only. An existing space or tab is left exactly as written, so a comment's own
    /// alignment survives a format — collapsing it would reflow bullet lists and ASCII tables
    /// that carry meaning to the only reader a comment has.
    /// </summary>
    [Theory]
    [InlineData("/proc/f()\n\t//   indented note\n\treturn 1\n")]
    [InlineData("/proc/f()\n\t//\tafter a tab\n\treturn 1\n")]
    public void F9_never_collapses_whitespace_the_author_wrote(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>A banner is nothing but slashes, and a bare `//` has no text to separate.</summary>
    [Theory]
    [InlineData("////////////////////////\n/proc/f()\n\treturn 1\n")]
    [InlineData("/proc/f()\n\t//\n\treturn 1\n")]
    public void F9_leaves_a_banner_and_an_empty_comment_alone(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>A block comment is a different rule, and the spec does not have one.</summary>
    [Fact]
    public void F9_does_not_touch_a_block_comment()
        => Assert.Equal("/*note*/\n/proc/f()\n\treturn 1\n", Formatted("/*note*/\n/proc/f()\n\treturn 1\n"));

    // -- F6: collapse runs of three or more blank lines ---------------------

    [Theory]
    [InlineData("/proc/a()\n\treturn 1\n\n\n\n/proc/b()\n\treturn 2\n", "/proc/a()\n\treturn 1\n\n/proc/b()\n\treturn 2\n")]
    [InlineData("/proc/a()\n\treturn 1\n\n\n\n\n\n/proc/b()\n\treturn 2\n", "/proc/a()\n\treturn 1\n\n/proc/b()\n\treturn 2\n")]
    public void F6_collapses_a_run_of_three_or_more(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// One and two are left as written: the corpus is 2,501 single against 278 double, so a double
    /// is a real spacing choice rather than an accident.
    /// </summary>
    [Theory]
    [InlineData("/proc/a()\n\treturn 1\n\n/proc/b()\n\treturn 2\n")]
    [InlineData("/proc/a()\n\treturn 1\n\n\n/proc/b()\n\treturn 2\n")]
    public void F6_leaves_one_and_two_blank_lines_alone(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>
    /// A blank line inside a `{" ... "}` string is program DATA — the string carries its newlines
    /// as content — so the run is not a run at all.
    /// </summary>
    [Fact]
    public void F6_never_collapses_inside_a_multiline_string()
    {
        const string source = "/proc/a()\n\treturn {\"one\n\n\n\ntwo\"}\n";

        Assert.Equal(source, Formatted(source));
    }

    [Fact]
    public void F6_never_collapses_inside_a_block_comment()
    {
        const string source = "/*\n\n\n\n*/\n/proc/a()\n\treturn 1\n";

        Assert.Equal(source, Formatted(source));
    }

    /// <summary>
    /// A run at the end of the file is the file's trailing newlines, which sit next to the
    /// never-touch rule about a final newline rather than under this one.
    /// </summary>
    [Fact]
    public void F6_leaves_a_trailing_run_alone()
    {
        const string source = "/proc/a()\n\treturn 1\n\n\n\n";

        Assert.Equal(source, Formatted(source));
    }

    /// <summary>
    /// Blank lines made of spaces are both F5's business and F6's. The edits must not overlap, and
    /// the result must be the same as if the whitespace had never been there.
    /// </summary>
    [Fact]
    public void F5_and_F6_do_not_edit_the_same_characters()
    {
        Document document = new(
            "mem.dm",
            SourceText.From("/proc/a()\n\treturn 1\n   \n\t\n  \n/proc/b()\n\treturn 2\n"),
            fromBuffer: true);

        IReadOnlyList<FormatEdit> edits = FormattingService.Format(document);

        for (int i = 1; i < edits.Count; i++)
            Assert.True(edits[i - 1].Span.End <= edits[i].Span.Start, "edits overlap");

        Assert.Equal(
            "/proc/a()\n\treturn 1\n\n/proc/b()\n\treturn 2\n",
            Formatted("/proc/a()\n\treturn 1\n   \n\t\n  \n/proc/b()\n\treturn 2\n"));
    }

    // -- F11: a blank line before a proc or verb ---------------------------

    [Theory]
    [InlineData(
        "/proc/a()\n\treturn 1\n/proc/b()\n\treturn 2\n",
        "/proc/a()\n\treturn 1\n\n/proc/b()\n\treturn 2\n")]
    [InlineData(
        "/mob\n\tvar/hp = 1\n\tproc/heal()\n\t\treturn 1\n",
        "/mob\n\tvar/hp = 1\n\n\tproc/heal()\n\t\treturn 1\n")]
    [InlineData(
        "/mob\n\tverb/say()\n\t\treturn 1\n\tverb/shout()\n\t\treturn 2\n",
        "/mob\n\tverb/say()\n\t\treturn 1\n\n\tverb/shout()\n\t\treturn 2\n")]
    public void F11_inserts_a_blank_line_before_a_declaration_that_has_none(string source, string expected)
        => Assert.Equal(expected, Formatted(source));

    /// <summary>
    /// An OVERRIDE is the commonest proc declaration in DM and carries no `proc` segment, which is
    /// why this rule reads the outline rather than matching a token pattern.
    /// </summary>
    [Fact]
    public void F11_sees_an_override()
        => Assert.Equal(
            "/mob/Login()\n\treturn 1\n\n/mob/Logout()\n\treturn 2\n",
            Formatted("/mob/Login()\n\treturn 1\n/mob/Logout()\n\treturn 2\n"));

    [Theory]
    [InlineData("/proc/a()\n\treturn 1\n\n/proc/b()\n\treturn 2\n")]
    [InlineData("/proc/a()\n\treturn 1\n\n\n/proc/b()\n\treturn 2\n")]
    public void F11_never_removes_spacing_the_author_wrote(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>
    /// The first member under the header that opens its block stays tight. 501 of the 1,308
    /// unspaced declarations in the reference projects are this shape, and splitting `proc` from
    /// its first child reads as damage rather than as spacing.
    /// </summary>
    [Theory]
    [InlineData("/mob\n\tproc\n\t\theal()\n\t\t\treturn 1\n")]
    [InlineData("/mob\n\tproc/heal()\n\t\treturn 1\n")]
    public void F11_leaves_a_first_member_under_its_own_header_alone(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>
    /// <b>Not a style exemption.</b> A blank line ends a doc-comment run, so inserting one between
    /// a `///` and its declaration takes the documentation off the symbol — hover and completion
    /// would stop showing it.
    /// </summary>
    [Theory]
    [InlineData("/proc/a()\n\treturn 1\n/// The hit points.\n/mob/proc/heal()\n\treturn 2\n")]
    [InlineData("/proc/a()\n\treturn 1\n// an ordinary note\n/mob/proc/heal()\n\treturn 2\n")]
    [InlineData("/proc/a()\n\treturn 1\n/** a block form */\n/mob/proc/heal()\n\treturn 2\n")]
    public void F11_never_separates_a_comment_from_what_it_documents(string source)
        => Assert.Equal(source, Formatted(source));

    /// <summary>The inserted terminator is the file's own, so a CRLF file stays a CRLF file.</summary>
    [Fact]
    public void F11_inserts_the_files_own_line_ending()
        => Assert.Equal(
            "/proc/a()\r\n\treturn 1\r\n\r\n/proc/b()\r\n\treturn 2\r\n",
            Formatted("/proc/a()\r\n\treturn 1\r\n/proc/b()\r\n\treturn 2\r\n"));

    [Fact]
    public void F11_does_not_touch_a_declaration_that_opens_the_file()
        => Assert.Equal("/proc/a()\n\treturn 1\n", Formatted("/proc/a()\n\treturn 1\n"));

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
