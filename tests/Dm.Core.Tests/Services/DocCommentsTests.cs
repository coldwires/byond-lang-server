using Dm.Core.Services;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

/// <summary>
/// Both DM doc-comment forms. On /tg/station 4,870 files use <c>///</c> and 1,784 use
/// <c>/** ... */</c>, so recognising only the first silently returned nothing for a sixth of the
/// documented code.
/// </summary>
public class DocCommentsTests
{
    /// <summary>
    /// Reads the comment above the declaration, which is the last line with anything on it.
    /// </summary>
    /// <remarks>
    /// Not <c>LineCount - 1</c>: a trailing newline makes the final line empty, and asking about
    /// that one reports the declaration itself as the line above rather than the comment.
    /// </remarks>
    private static string Above(string source)
    {
        SourceText text = SourceText.From(source);

        int declaration = text.LineCount - 1;
        while (declaration > 0 && text.GetLineText(declaration).Trim().Length == 0)
            declaration--;

        return DocComments.Above(text, declaration);
    }

    [Fact]
    public void Reads_a_run_of_slash_comments()
    {
        Assert.Equal(
            "Restores health.\nSafe on a dead mob.",
            Above("/// Restores health.\n/// Safe on a dead mob.\nproc/heal()\n"));
    }

    [Fact]
    public void Reads_a_block_comment()
    {
        Assert.Equal(
            "Sets the sound's range.\nThen updates any listeners.",
            Above("/**\n * Sets the sound's range.\n * Then updates any listeners.\n */\nproc/f()\n"));
    }

    /// <summary>The one-line block form, which real code uses for short summaries.</summary>
    [Fact]
    public void Reads_a_single_line_block_comment()
    {
        Assert.Equal("Sets the range.", Above("/** Sets the range. */\nproc/f()\n"));
    }

    /// <summary>
    /// A plain <c>/*</c> is an ordinary comment, not documentation.
    /// </summary>
    /// <remarks>
    /// Treating one as documentation would attach commented-out code to whatever followed it.
    /// </remarks>
    [Fact]
    public void A_plain_block_comment_is_not_documentation()
    {
        Assert.Empty(Above("/*\n * just a note\n */\nproc/f()\n"));
    }

    [Fact]
    public void A_plain_line_comment_is_not_documentation()
    {
        Assert.Empty(Above("// just a note\nproc/f()\n"));
    }

    [Fact]
    public void A_blank_line_ends_a_slash_run()
    {
        Assert.Empty(Above("/// Unrelated.\n\nproc/f()\n"));
    }

    /// <summary>A stray <c>*/</c> with no opener above it is not a block.</summary>
    [Fact]
    public void An_unopened_block_yields_nothing()
    {
        Assert.Empty(Above("some_code = 1\n*/\nproc/f()\n"));
    }

    [Fact]
    public void No_comment_yields_nothing()
    {
        Assert.Empty(Above("var/x = 1\nproc/f()\n"));
    }
}
