using System.Collections.Generic;
using System.Linq;
using Dm.Core.Services;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

public class FoldingServiceTests
{
    private static IReadOnlyList<FoldingRange> Fold(string source)
        => FoldingService.RangesFor(new Document("test.dm", SourceText.From(source), fromBuffer: true));

    [Fact]
    public void A_type_with_members_folds()
    {
        IReadOnlyList<FoldingRange> ranges = Fold("/obj/item\n\tvar/hp = 1\n\tvar/weight = 2\n");

        FoldingRange region = Assert.Single(ranges, r => r.Kind == FoldKind.Region);
        Assert.Equal(0, region.StartLine);
        Assert.Equal(2, region.EndLine);
    }

    /// <summary>Nothing to hide on one line, and a fold arrow beside it reads as a bug.</summary>
    [Fact]
    public void A_single_line_declaration_does_not_fold()
    {
        Assert.Empty(Fold("/obj/item\n"));
    }

    /// <summary>
    /// The proc's body folds separately from the proc, so the signature stays visible when the
    /// body is collapsed.
    /// </summary>
    [Fact]
    public void A_proc_body_folds_separately_from_the_proc()
    {
        IReadOnlyList<FoldingRange> ranges = Fold(
            "/mob/proc/heal(amount)\n\thp += amount\n\treturn hp\n");

        Assert.True(ranges.Count(r => r.Kind == FoldKind.Region) >= 2,
            "the proc and its body are separate regions");
    }

    /// <summary>
    /// Built from the AST, not from indentation — DM's two block syntaxes nest freely, so folding
    /// by leading whitespace would miss everything written inside braces.
    /// </summary>
    [Fact]
    public void A_brace_block_folds()
    {
        IReadOnlyList<FoldingRange> ranges = Fold("/obj/one {\n\tvar\n\t\ta = 1\n\t\tb = 2\n}\n");

        Assert.Contains(ranges, r => r.Kind == FoldKind.Region);
    }

    [Fact]
    public void A_block_comment_folds_as_a_comment()
    {
        FoldingRange range = Assert.Single(
            Fold("/*\n multi\n line\n*/\n/obj/item\n"), r => r.Kind == FoldKind.Comment);

        Assert.Equal(0, range.StartLine);
        Assert.Equal(3, range.EndLine);
    }

    /// <summary>Touching line comments are one comment to a reader, so they fold as one.</summary>
    [Fact]
    public void A_run_of_line_comments_folds_as_one()
    {
        FoldingRange range = Assert.Single(
            Fold("// one\n// two\n// three\n/obj/item\n"), r => r.Kind == FoldKind.Comment);

        Assert.Equal(0, range.StartLine);
        Assert.Equal(2, range.EndLine);
    }

    /// <summary>A blank line between comments separates them, matching what a reader sees.</summary>
    [Fact]
    public void A_gap_splits_a_comment_run()
    {
        IReadOnlyList<FoldingRange> comments =
            Fold("// one\n// two\n\n\n// four\n// five\n").Where(r => r.Kind == FoldKind.Comment).ToList();

        Assert.Equal(2, comments.Count);
    }

    [Fact]
    public void An_empty_file_folds_nothing()
    {
        Assert.Empty(Fold(string.Empty));
    }
}
