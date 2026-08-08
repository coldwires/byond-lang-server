using System.Collections.Generic;
using System.Linq;
using Dm.Core.Services;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

public class ColorServiceTests
{
    private static IReadOnlyList<ColorInformation> Colors(string source)
        => ColorService.ColorsIn(new Document("test.dm", SourceText.From(source), fromBuffer: true));

    private static ColorInformation Only(string source) => Assert.Single(Colors(source));

    [Fact]
    public void A_six_digit_literal_is_a_colour()
    {
        ColorInformation color = Only("/obj/t\n\tcolor = \"#ff0080\"\n");

        Assert.Equal(255, color.Red);
        Assert.Equal(0, color.Green);
        Assert.Equal(128, color.Blue);
        Assert.Equal(255, color.Alpha);
        Assert.Equal(ColorForm.Literal, color.Form);
    }

    /// <summary>
    /// The one a shift-by-four implementation gets wrong. rgb2num("#f08") is [255,0,136] on
    /// 516.1686 — the digit is duplicated, so 8 becomes 0x88 and not 0x80.
    /// </summary>
    [Fact]
    public void A_short_literal_duplicates_each_digit_rather_than_shifting()
    {
        ColorInformation color = Only("/obj/t\n\tcolor = \"#f08\"\n");

        Assert.Equal(255, color.Red);
        Assert.Equal(0, color.Green);
        Assert.Equal(136, color.Blue);
    }

    /// <summary>
    /// Compiler-verified through rgb2num: "#ff008040" is [255,0,128,64] and the four-digit
    /// "#f088" is [255,0,136,136] — the alpha nibble duplicates like every other.
    /// </summary>
    [Fact]
    public void An_alpha_literal_is_read_in_both_lengths()
    {
        Assert.Equal(64, Only("/obj/t\n\tcolor = \"#ff008040\"\n").Alpha);
        Assert.Equal(136, Only("/obj/t\n\tcolor = \"#f088\"\n").Alpha);
    }

    /// <summary>
    /// Five characters is #RGBA, not a malformed #RRGG. rgb2num("#ff00") is [255,255,0,0] —
    /// a fully transparent yellow, which an implementation checking only 4/7/9 lengths would miss.
    /// </summary>
    [Fact]
    public void A_four_digit_literal_is_rgba_rather_than_malformed()
    {
        ColorInformation color = Only("/obj/t\n\tcolor = \"#ff00\"\n");

        Assert.Equal(255, color.Red);
        Assert.Equal(255, color.Green);
        Assert.Equal(0, color.Blue);
        Assert.Equal(0, color.Alpha);
    }

    [Fact]
    public void The_span_covers_the_quotes_so_a_presentation_replaces_the_whole_literal()
    {
        const string source = "/obj/t\n\tcolor = \"#ff0080\"\n";
        ColorInformation color = Only(source);

        Assert.Equal("\"#ff0080\"", source.Substring(color.Span.Start, color.Span.Length));
    }

    [Fact]
    public void An_rgb_call_is_a_colour_and_keeps_its_form()
    {
        ColorInformation color = Only("/obj/t\n\tvar/c = rgb(255, 0, 128)\n");

        Assert.Equal(255, color.Red);
        Assert.Equal(128, color.Blue);
        Assert.Equal(255, color.Alpha);
        Assert.Equal(ColorForm.RgbCall, color.Form);
    }

    [Fact]
    public void A_four_argument_rgb_call_carries_its_alpha()
    {
        Assert.Equal(64, Only("/obj/t\n\tvar/c = rgb(255, 0, 128, 64)\n").Alpha);
    }

    /// <summary>
    /// Compiler-verified: rgb(300,-20,0) is #ff0000 and rgb(-1,-1,-1) is #000000, so components
    /// clamp at both ends. The negative also pins the two-token shape — the lexer splits `-20`
    /// into Minus and Number.
    /// </summary>
    [Fact]
    public void Out_of_range_components_clamp_at_both_ends()
    {
        ColorInformation color = Only("/obj/t\n\tvar/c = rgb(300, -20, 0)\n");

        Assert.Equal(255, color.Red);
        Assert.Equal(0, color.Green);
        Assert.Equal(0, color.Blue);

        ColorInformation black = Only("/obj/t\n\tvar/c = rgb(-1, -1, -1)\n");

        Assert.Equal(0, black.Red);
        Assert.Equal(0, black.Blue);
    }

    /// <summary>
    /// Compiler-verified: rgb(1.4,1.5,1.6) is #010101. Rounding would give 1,2,2 and disagree with
    /// the compiler on the value most likely to be written.
    /// </summary>
    [Fact]
    public void Fractional_components_truncate_rather_than_round()
    {
        ColorInformation color = Only("/obj/t\n\tvar/c = rgb(1.4, 1.5, 1.6)\n");

        Assert.Equal(1, color.Red);
        Assert.Equal(1, color.Green);
        Assert.Equal(1, color.Blue);
    }

    /// <summary>
    /// The value is not knowable statically, so there is no swatch to draw. Excluded by the
    /// three-token shape rather than by a separate test for interpolation.
    /// </summary>
    [Fact]
    public void An_interpolated_string_is_not_a_colour()
    {
        Assert.Empty(Colors("/obj/t\n\tvar/c = \"[src]#ff0000\"\n"));
    }

    /// <summary>
    /// Another colour space entirely, and we model none of them. Reading the arguments as RGB
    /// would draw a red swatch beside a colour that is not red.
    /// </summary>
    [Fact]
    public void An_rgb_call_with_a_space_argument_is_skipped()
    {
        Assert.Empty(Colors("/obj/t\n\tvar/c = rgb(0, 100, 50, space=COLORSPACE_HSL)\n"));
        Assert.Empty(Colors("/obj/t\n\tvar/c = rgb(h=0, s=100, l=50)\n"));
    }

    [Fact]
    public void An_rgb_call_with_a_computed_argument_is_skipped()
    {
        Assert.Empty(Colors("/obj/t\n\tvar/c = rgb(shade, 0, 0)\n"));
        Assert.Empty(Colors("/obj/t\n\tvar/c = rgb(255 - 10, 0, 0)\n"));
    }

    [Fact]
    public void A_string_that_is_not_hex_is_not_a_colour()
    {
        Assert.Empty(Colors("/obj/t\n\tvar/c = \"#nothex\"\n"));
        Assert.Empty(Colors("/obj/t\n\tvar/c = \"ff0000\"\n"));
        Assert.Empty(Colors("/obj/t\n\tvar/c = \"#ff000\"\n"));
    }

    /// <summary>A named colour is real DM and is deliberately not offered yet — see the service.</summary>
    [Fact]
    public void A_named_colour_is_not_offered()
    {
        Assert.Empty(Colors("/obj/t\n\tcolor = \"red\"\n"));
    }

    /// <summary>Tokens, not text, so a colour inside a comment is not one.</summary>
    [Fact]
    public void A_colour_in_a_comment_is_not_a_colour()
    {
        Assert.Empty(Colors("/obj/t\n\t// paint it \"#ff0000\"\n\tvar/hp = 1\n"));
    }

    [Fact]
    public void Several_colours_in_one_file_all_come_back()
    {
        IReadOnlyList<ColorInformation> colors =
            Colors("/obj/t\n\tcolor = \"#ff0000\"\n\tvar/b = rgb(0, 0, 255)\n");

        Assert.Equal(2, colors.Count);
        Assert.Equal(ColorForm.Literal, colors[0].Form);
        Assert.Equal(ColorForm.RgbCall, colors[1].Form);
    }

    /// <summary>An opaque colour drops the alpha, or every edit rewrites #ff0000 as #ff0000ff.</summary>
    [Fact]
    public void Presentations_lead_with_the_form_it_was_written_in()
    {
        ColorInformation literal = Only("/obj/t\n\tcolor = \"#ff0080\"\n");
        Assert.Equal("\"#ff0080\"", ColorService.PresentationsFor(literal).First());

        ColorInformation call = Only("/obj/t\n\tvar/c = rgb(255, 0, 128)\n");
        Assert.Equal("rgb(255, 0, 128)", ColorService.PresentationsFor(call).First());
    }

    [Fact]
    public void A_translucent_colour_keeps_its_alpha_in_both_presentations()
    {
        ColorInformation color = Only("/obj/t\n\tcolor = \"#ff008040\"\n");
        IReadOnlyList<string> presentations = ColorService.PresentationsFor(color);

        Assert.Equal("\"#ff008040\"", presentations[0]);
        Assert.Equal("rgb(255, 0, 128, 64)", presentations[1]);
    }
}
