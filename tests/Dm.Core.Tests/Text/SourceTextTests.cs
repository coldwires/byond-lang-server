using Dm.Core.Text;

namespace Dm.Core.Tests.Text;

public class SourceTextTests
{
    // -- line counting -----------------------------------------------------

    [Theory]
    [InlineData("", 1)]
    [InlineData("a", 1)]
    [InlineData("a\n", 2)]              // trailing terminator yields a final empty line
    [InlineData("a\nb", 2)]
    [InlineData("a\r\nb", 2)]
    [InlineData("a\rb", 2)]             // lone CR is a terminator too
    [InlineData("\n", 2)]
    [InlineData("\r\n", 2)]
    [InlineData("\r", 2)]
    [InlineData("a\r\nb\nc\rd", 4)]     // all three forms in one file
    public void Counts_lines_for_every_terminator_form(string content, int expected)
    {
        Assert.Equal(expected, SourceText.From(content).LineCount);
    }

    [Fact]
    public void Content_is_preserved_exactly()
    {
        const string original = "/mob/a\r\n\tvar/x = 1\n\tvar/y = 2\r\n";

        SourceText text = SourceText.From(original);

        Assert.Equal(original, text.Content);
        Assert.Equal(original.Length, text.Length);
    }

    // -- line spans --------------------------------------------------------

    [Fact]
    public void Line_spans_exclude_the_terminator()
    {
        SourceText text = SourceText.From("alpha\r\nbeta\ngamma\rdelta");

        Assert.Equal("alpha", text.GetLineText(0));
        Assert.Equal("beta", text.GetLineText(1));
        Assert.Equal("gamma", text.GetLineText(2));
        Assert.Equal("delta", text.GetLineText(3));
    }

    [Fact]
    public void Line_spans_including_terminator_cover_the_whole_file()
    {
        SourceText text = SourceText.From("alpha\r\nbeta\n");

        Assert.Equal("alpha\r\n", text.ToString(text.GetLineSpanIncludingTerminator(0)));
        Assert.Equal("beta\n", text.ToString(text.GetLineSpanIncludingTerminator(1)));
        Assert.Equal("", text.ToString(text.GetLineSpanIncludingTerminator(2)));
    }

    [Fact]
    public void Final_line_without_a_terminator_is_still_a_line()
    {
        SourceText text = SourceText.From("a\nb");

        Assert.Equal(2, text.LineCount);
        Assert.Equal("b", text.GetLineText(1));
    }

    [Fact]
    public void Empty_text_has_one_empty_line()
    {
        SourceText text = SourceText.From(string.Empty);

        Assert.Equal(1, text.LineCount);
        Assert.Equal("", text.GetLineText(0));
        Assert.Equal(0, text.GetLineStart(0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Out_of_range_line_throws(int line)
    {
        SourceText text = SourceText.From("a\nb");

        Assert.Throws<ArgumentOutOfRangeException>(() => text.GetLineStart(line));
    }

    // -- offset to line ----------------------------------------------------

    [Fact]
    public void Maps_offsets_to_lines()
    {
        //                                 0123 4 567890
        SourceText text = SourceText.From("ab\r\ncde\nf");

        Assert.Equal(0, text.GetLineIndex(0));
        Assert.Equal(0, text.GetLineIndex(1));
        Assert.Equal(0, text.GetLineIndex(2));   // the \r belongs to line 0
        Assert.Equal(0, text.GetLineIndex(3));   // so does the \n
        Assert.Equal(1, text.GetLineIndex(4));
        Assert.Equal(1, text.GetLineIndex(6));
        Assert.Equal(2, text.GetLineIndex(8));
    }

    [Fact]
    public void Offsets_outside_the_text_clamp_to_the_nearest_line()
    {
        SourceText text = SourceText.From("a\nb");

        Assert.Equal(0, text.GetLineIndex(-5));
        Assert.Equal(1, text.GetLineIndex(999));
    }

    // -- position conversion, ASCII ----------------------------------------

    [Fact]
    public void Converts_offsets_to_positions()
    {
        SourceText text = SourceText.From("ab\r\ncde");

        Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(0, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(4, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(1, 3), text.GetLinePosition(7, PositionEncoding.Utf16));
    }

    [Fact]
    public void Line_endings_do_not_shift_character_positions()
    {
        // The same logical content, two ending styles. Positions must agree, which is what makes
        // the ABI's line/column contract safe against editor normalisation.
        SourceText crlf = SourceText.From("alpha\r\nbeta\r\ngamma");
        SourceText lf = SourceText.From("alpha\nbeta\ngamma");

        foreach (PositionEncoding encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            for (int line = 0; line < 3; line++)
            {
                int crlfOffset = crlf.GetOffset(new LinePosition(line, 2), encoding);
                int lfOffset = lf.GetOffset(new LinePosition(line, 2), encoding);

                Assert.Equal(crlf.GetLinePosition(crlfOffset, encoding), lf.GetLinePosition(lfOffset, encoding));
                Assert.Equal(crlf.Content[crlfOffset], lf.Content[lfOffset]);
            }
        }
    }

    // -- position conversion, non-ASCII ------------------------------------

    [Fact]
    public void Utf8_and_utf16_columns_differ_for_multibyte_text()
    {
        // "é" is 1 UTF-16 unit and 2 UTF-8 bytes. "日" is 1 unit and 3 bytes.
        SourceText text = SourceText.From("éa日b");

        Assert.Equal(new LinePosition(0, 3), text.GetLinePosition(3, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(0, 6), text.GetLinePosition(3, PositionEncoding.Utf8));
    }

    [Fact]
    public void Handles_surrogate_pairs()
    {
        // U+1F600 is 2 UTF-16 units and 4 UTF-8 bytes.
        SourceText text = SourceText.From("\U0001F600x");

        Assert.Equal(3, text.Length);
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(0, 4), text.GetLinePosition(2, PositionEncoding.Utf8));

        Assert.Equal(2, text.GetOffset(new LinePosition(0, 4), PositionEncoding.Utf8));
    }

    [Fact]
    public void A_utf8_column_inside_a_multibyte_sequence_rounds_up_to_a_boundary()
    {
        SourceText text = SourceText.From("日x");

        // Byte 1 is mid-sequence. Rounding up lands after the character, never inside it.
        Assert.Equal(1, text.GetOffset(new LinePosition(0, 1), PositionEncoding.Utf8));
        Assert.Equal(1, text.GetOffset(new LinePosition(0, 3), PositionEncoding.Utf8));
    }

    [Theory]
    [InlineData("plain ascii only")]
    [InlineData("mixed é and 日 and \U0001F600 text")]
    [InlineData("line one\r\nline twö\nline 三\rend")]
    public void Offset_to_position_and_back_round_trips(string content)
    {
        SourceText text = SourceText.From(content);

        foreach (PositionEncoding encoding in new[] { PositionEncoding.Utf8, PositionEncoding.Utf16 })
        {
            for (int offset = 0; offset <= text.Length; offset++)
            {
                // Offsets inside a surrogate pair have no valid position.
                if (offset < text.Length && char.IsLowSurrogate(text[offset]))
                    continue;

                // Neither does an offset inside a line terminator: every such offset collapses to
                // the end of that line, so the mapping cannot be injective there.
                if (offset > text.GetLineSpan(text.GetLineIndex(offset)).End)
                    continue;

                LinePosition position = text.GetLinePosition(offset, encoding);
                Assert.Equal(offset, text.GetOffset(position, encoding));
            }
        }
    }

    // -- clamping ----------------------------------------------------------

    [Fact]
    public void Positions_past_the_end_of_a_line_clamp_to_the_line_end()
    {
        SourceText text = SourceText.From("ab\ncdef");

        Assert.Equal(2, text.GetOffset(new LinePosition(0, 99), PositionEncoding.Utf16));
        Assert.Equal(2, text.GetOffset(new LinePosition(0, 99), PositionEncoding.Utf8));
    }

    [Fact]
    public void Positions_past_the_last_line_clamp_to_the_end_of_the_text()
    {
        SourceText text = SourceText.From("ab\ncd");

        Assert.Equal(text.Length, text.GetOffset(new LinePosition(99, 0), PositionEncoding.Utf16));
    }

    [Fact]
    public void An_offset_inside_a_terminator_clamps_to_the_end_of_that_line()
    {
        //                                 0123 4 5
        SourceText text = SourceText.From("ab\r\ncd");

        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(2, PositionEncoding.Utf16)); // the \r
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(3, PositionEncoding.Utf16)); // the \n
        Assert.Equal(new LinePosition(1, 0), text.GetLinePosition(4, PositionEncoding.Utf16));
    }

    [Fact]
    public void Offsets_outside_the_text_clamp()
    {
        SourceText text = SourceText.From("ab");

        Assert.Equal(new LinePosition(0, 0), text.GetLinePosition(-10, PositionEncoding.Utf16));
        Assert.Equal(new LinePosition(0, 2), text.GetLinePosition(10, PositionEncoding.Utf16));
    }
}
