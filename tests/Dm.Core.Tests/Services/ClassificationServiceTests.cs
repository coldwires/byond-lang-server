using Dm.Core.Services;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

public class ClassificationServiceTests
{
    private static IReadOnlyList<ClassifiedSpan> Classify(string source)
        => ClassificationService.Classify(Lexer.Lex(SourceText.From(source)));

    private static (ClassificationKind Kind, string Text)[] Pairs(string source)
    {
        SourceText text = SourceText.From(source);
        LexResult lex = Lexer.Lex(text);

        List<(ClassificationKind, string)> pairs = new();
        foreach (ClassifiedSpan span in ClassificationService.Classify(lex))
            pairs.Add((span.Kind, text.ToString(span.Span)));

        return pairs.ToArray();
    }

    [Fact]
    public void Classifies_the_basic_categories()
    {
        Assert.Equal(
            new[]
            {
                (ClassificationKind.Keyword, "var"),
                (ClassificationKind.Operator, "/"),
                (ClassificationKind.Identifier, "x"),
                (ClassificationKind.Operator, "="),
                (ClassificationKind.Number, "42"),
                (ClassificationKind.Comment, "// note"),
            },
            Pairs("var/x = 42 // note"));
    }

    /// <summary>
    /// A string is three tokens. Emitting three spans the client colours identically is waste on a
    /// path that runs per keystroke.
    /// </summary>
    [Fact]
    public void Touching_runs_of_the_same_kind_are_coalesced()
    {
        Assert.Equal(
            new[] { (ClassificationKind.String, "\"hello\"") },
            Pairs("\"hello\""));
    }

    [Fact]
    public void Interpolation_delimiters_separate_text_from_code()
    {
        // The expression inside must read as code, not as more string.
        Assert.Equal(
            new[]
            {
                (ClassificationKind.String, "\"hi "),
                (ClassificationKind.InterpolationDelimiter, "["),
                (ClassificationKind.Identifier, "name"),
                (ClassificationKind.InterpolationDelimiter, "]"),
                (ClassificationKind.String, "!\""),
            },
            Pairs("\"hi [name]!\""));
    }

    [Fact]
    public void Separate_strings_are_not_merged_across_a_gap()
    {
        (ClassificationKind Kind, string Text)[] pairs = Pairs("\"a\" \"b\"");

        Assert.Equal(2, pairs.Length);
        Assert.All(pairs, p => Assert.Equal(ClassificationKind.String, p.Kind));
    }

    [Fact]
    public void A_multiline_string_is_one_span()
    {
        (ClassificationKind Kind, string Text)[] pairs = Pairs("{\"one\ntwo\"}");

        Assert.Single(pairs);
        Assert.Equal(ClassificationKind.String, pairs[0].Kind);
        Assert.Equal("{\"one\ntwo\"}", pairs[0].Text);
    }

    [Fact]
    public void Classifies_preprocessor_directives()
    {
        Assert.Equal(
            new[]
            {
                (ClassificationKind.PreprocessorDirective, "#define"),
                (ClassificationKind.Identifier, "MAX"),
                (ClassificationKind.Number, "10"),
            },
            Pairs("#define MAX 10"));
    }

    [Fact]
    public void Classifies_resource_literals()
    {
        Assert.Equal(
            new[] { (ClassificationKind.Resource, "'mob.dmi'") },
            Pairs("'mob.dmi'"));
    }

    [Fact]
    public void Unrecognised_input_is_classified_as_an_error()
    {
        Assert.Contains(Classify("a $ b"), s => s.Kind == ClassificationKind.Error);
    }

    [Fact]
    public void Layout_produces_no_spans()
    {
        // Indent, Dedent and Newline carry no colour; a client renders the gaps.
        IReadOnlyList<ClassifiedSpan> spans = Classify("/mob\n\tvar/x\n");

        Assert.DoesNotContain(spans, s => s.Kind == ClassificationKind.None);
        Assert.All(spans, s => Assert.False(s.Span.IsEmpty));
    }

    // -- range filtering ---------------------------------------------------

    [Fact]
    public void Classify_lines_returns_only_the_requested_range()
    {
        const string source = "aaa\nbbb\nccc\nddd\n";
        SourceText text = SourceText.From(source);
        LexResult lex = Lexer.Lex(text);

        IReadOnlyList<ClassifiedSpan> spans = ClassificationService.ClassifyLines(lex, 1, 2);

        Assert.Equal(2, spans.Count);
        Assert.Equal("bbb", text.ToString(spans[0].Span));
        Assert.Equal("ccc", text.ToString(spans[1].Span));
    }

    [Fact]
    public void Classify_lines_clamps_an_out_of_range_request()
    {
        LexResult lex = Lexer.Lex(SourceText.From("aaa\nbbb\n"));

        Assert.NotEmpty(ClassificationService.ClassifyLines(lex, -5, 999));
        Assert.Empty(ClassificationService.ClassifyLines(lex, 999, 1000));
    }

    /// <summary>
    /// The reason classification lexes the whole file rather than the visible range. Line 3 is
    /// inside a string that opened on line 0; a range-local lex would colour it as code.
    /// </summary>
    [Fact]
    public void A_range_inside_a_multiline_string_is_still_classified_as_string()
    {
        const string source = "var/s = {\"one\ntwo\nthree\nfour\"}\n";
        LexResult lex = Lexer.Lex(SourceText.From(source));

        IReadOnlyList<ClassifiedSpan> spans = ClassificationService.ClassifyLines(lex, 2, 2);

        Assert.Single(spans);
        Assert.Equal(ClassificationKind.String, spans[0].Kind);
    }

    [Fact]
    public void A_range_inside_a_block_comment_is_still_classified_as_comment()
    {
        const string source = "/* one\ntwo\nthree */\n";
        LexResult lex = Lexer.Lex(SourceText.From(source));

        IReadOnlyList<ClassifiedSpan> spans = ClassificationService.ClassifyLines(lex, 1, 1);

        Assert.Single(spans);
        Assert.Equal(ClassificationKind.Comment, spans[0].Kind);
    }

    // -- invariants --------------------------------------------------------

    [Fact]
    public void Spans_are_ordered_and_never_overlap()
    {
        const string source = "/mob/test\n\tvar/x = 1 // c\n\tproc/F()\n\t\treturn \"a[b]c\"\n";
        IReadOnlyList<ClassifiedSpan> spans = Classify(source);

        int previousEnd = 0;
        foreach (ClassifiedSpan span in spans)
        {
            Assert.True(span.Span.Start >= previousEnd, $"{span} overlaps a previous span");
            previousEnd = span.Span.End;
        }
    }
}
