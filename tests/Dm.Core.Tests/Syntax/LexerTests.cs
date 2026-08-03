using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Syntax;

public class LexerTests
{
    private static LexResult Lex(string source) => Lexer.Lex(SourceText.From(source));

    /// <summary>Token kinds excluding the trailing EndOfFile, which every case would repeat.</summary>
    private static TokenKind[] Kinds(string source)
    {
        LexResult result = Lex(source);
        List<TokenKind> kinds = new();

        foreach (Token token in result.Tokens)
        {
            if (token.Kind != TokenKind.EndOfFile)
                kinds.Add(token.Kind);
        }

        return kinds.ToArray();
    }

    private static string[] Texts(string source)
    {
        LexResult result = Lex(source);
        List<string> texts = new();

        foreach (Token token in result.Tokens)
        {
            if (token.Kind != TokenKind.EndOfFile)
                texts.Add(result.GetText(token));
        }

        return texts.ToArray();
    }

    // -- identifiers and keywords -----------------------------------------

    [Fact]
    public void Lexes_identifiers()
    {
        Assert.Equal(
            new[] { TokenKind.Identifier, TokenKind.Identifier },
            Kinds("alpha _b2"));
    }

    [Theory]
    [InlineData("var", TokenKind.KeywordVar)]
    [InlineData("if", TokenKind.KeywordIf)]
    [InlineData("src", TokenKind.KeywordSrc)]
    [InlineData("in", TokenKind.KeywordIn)]
    [InlineData("as", TokenKind.KeywordAs)]
    public void Recognises_keywords(string source, TokenKind expected)
    {
        Assert.Equal(new[] { expected }, Kinds(source));
    }

    /// <summary>
    /// PLAN.md §4a: these are ordinary segments in the type tree, not keywords. Treating them as
    /// keywords would break <c>mob.proc.attack()</c>, which is legal and identical to
    /// <c>mob/proc/attack()</c>.
    /// </summary>
    [Theory]
    [InlineData("proc")]
    [InlineData("verb")]
    public void Proc_and_verb_are_identifiers(string source)
    {
        Assert.Equal(new[] { TokenKind.Identifier }, Kinds(source));
    }

    // -- numbers -----------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("42")]
    [InlineData("3.5")]
    [InlineData("0x1F")]
    [InlineData("1e10")]
    [InlineData("1.5e-3")]
    [InlineData(".5")]
    public void Lexes_numbers(string source)
    {
        Assert.Equal(new[] { TokenKind.Number }, Kinds(source));
    }

    [Fact]
    public void A_dot_after_a_number_is_only_consumed_when_a_digit_follows()
    {
        // `1.Foo()` is a number then member access, not a malformed float.
        Assert.Equal(
            new[] { TokenKind.Number, TokenKind.Dot, TokenKind.Identifier },
            Kinds("1.Foo"));

        Assert.Equal(new[] { "1", ".", "Foo" }, Texts("1.Foo"));
    }

    [Fact]
    public void An_e_not_followed_by_an_exponent_is_not_part_of_the_number()
    {
        Assert.Equal(
            new[] { TokenKind.Number, TokenKind.Identifier },
            Kinds("1east"));
    }

    // -- comments ----------------------------------------------------------

    [Fact]
    public void Lexes_a_line_comment()
    {
        Assert.Equal(new[] { TokenKind.Comment }, Kinds("// anything at all"));
    }

    [Fact]
    public void Block_comments_nest()
    {
        // Differs from C. A non-nesting scanner would stop at the first */ and treat the rest as code.
        LexResult result = Lex("/* outer /* inner */ still comment */");

        Assert.Equal(new[] { TokenKind.Comment }, Kinds("/* outer /* inner */ still comment */"));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void An_unterminated_block_comment_is_reported_but_still_lexes()
    {
        LexResult result = Lex("/* never closed");

        Assert.Equal(TokenKind.Comment, result.Tokens[0].Kind);
        Assert.Contains(result.Diagnostics, d => d.Id == "DM0002");
    }

    /// <summary>
    /// PLAN.md §4a: <c>var x = /obj//item</c> evaluates to <c>/obj</c>, because the rest of the
    /// line is a comment. Comment detection wins over path separation.
    /// </summary>
    [Fact]
    public void A_double_slash_inside_a_path_starts_a_comment()
    {
        Assert.Equal(
            new[] { TokenKind.Slash, TokenKind.Identifier, TokenKind.Comment },
            Kinds("/obj//item"));

        Assert.Equal(new[] { "/", "obj", "//item" }, Texts("/obj//item"));
    }

    // -- strings -----------------------------------------------------------

    [Fact]
    public void Lexes_a_simple_string()
    {
        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds("\"hello\""));
    }

    [Fact]
    public void An_empty_string_has_no_text_token()
    {
        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringEnd },
            Kinds("\"\""));
    }

    [Fact]
    public void Interpolation_produces_a_flat_run()
    {
        Assert.Equal(
            new[]
            {
                TokenKind.StringStart,
                TokenKind.StringText,
                TokenKind.InterpolationStart,
                TokenKind.Identifier,
                TokenKind.Dot,
                TokenKind.Identifier,
                TokenKind.InterpolationEnd,
                TokenKind.StringText,
                TokenKind.StringEnd,
            },
            Kinds("\"hi [M.name]!\""));
    }

    [Fact]
    public void Indexing_inside_an_interpolation_hole_does_not_end_it_early()
    {
        // The inner ] closes the index, not the hole.
        Assert.Equal(
            new[]
            {
                TokenKind.StringStart,
                TokenKind.InterpolationStart,
                TokenKind.Identifier,
                TokenKind.OpenBracket,
                TokenKind.Identifier,
                TokenKind.CloseBracket,
                TokenKind.InterpolationEnd,
                TokenKind.StringEnd,
            },
            Kinds("\"[list[i]]\""));
    }

    [Fact]
    public void Strings_nest_inside_interpolation()
    {
        Assert.Equal(
            new[]
            {
                TokenKind.StringStart,
                TokenKind.InterpolationStart,
                TokenKind.StringStart,
                TokenKind.StringText,
                TokenKind.StringEnd,
                TokenKind.InterpolationEnd,
                TokenKind.StringEnd,
            },
            Kinds("\"[\"inner\"]\""));
    }

    [Fact]
    public void Multiline_strings_carry_their_newlines_as_content()
    {
        LexResult result = Lex("{\"line one\nline two\"}");

        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds("{\"line one\nline two\"}"));

        // No Newline token: the break belongs to the string, not to the layout.
        Assert.DoesNotContain(result.Tokens, t => t.Kind == TokenKind.Newline);
        Assert.Equal("line one\nline two", result.GetText(result.Tokens[1]));
    }

    [Fact]
    public void A_bare_quote_does_not_end_a_multiline_string()
    {
        LexResult result = Lex("{\"say \" ok\"}");

        Assert.Equal("say \" ok", result.GetText(result.Tokens[1]));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Escapes_do_not_terminate_a_string()
    {
        LexResult result = Lex("\"a\\\"b\"");

        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds("\"a\\\"b\""));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void An_unterminated_string_stops_at_the_line_break()
    {
        LexResult result = Lex("\"oops\nnext");

        Assert.Contains(result.Diagnostics, d => d.Id == "DM0001");
        // The rest of the file still lexes, which is what an editor buffer needs.
        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.Identifier);
    }

    [Fact]
    public void Lexes_a_resource_literal()
    {
        Assert.Equal(new[] { TokenKind.Resource }, Kinds("'icons/mob.dmi'"));
    }

    // -- operators ---------------------------------------------------------

    [Theory]
    [InlineData("<<=", TokenKind.LeftShiftAssign)]
    [InlineData("<<", TokenKind.LeftShift)]
    [InlineData("<=", TokenKind.LessEqual)]
    [InlineData("<", TokenKind.Less)]
    [InlineData("<>", TokenKind.NotEqual)]
    [InlineData("**=", TokenKind.StarStarAssign)]
    [InlineData("**", TokenKind.StarStar)]
    [InlineData("~=", TokenKind.EquivalentTo)]
    [InlineData("~!", TokenKind.NotEquivalentTo)]
    [InlineData("&&=", TokenKind.AndAndAssign)]
    [InlineData("||", TokenKind.OrOr)]
    [InlineData("::", TokenKind.ColonColon)]
    [InlineData("?.", TokenKind.QuestionDot)]
    [InlineData("?:", TokenKind.QuestionColon)]
    [InlineData("..", TokenKind.DotDot)]
    [InlineData("++", TokenKind.PlusPlus)]
    public void Operators_match_longest_first(string source, TokenKind expected)
    {
        Assert.Equal(new[] { expected }, Kinds(source));
    }

    [Fact]
    public void An_unrecognised_character_becomes_an_unknown_token_rather_than_throwing()
    {
        LexResult result = Lex("a $ b");

        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.Unknown);
        Assert.Contains(result.Diagnostics, d => d.Id == "DM0005");
        Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
    }

    // -- layout ------------------------------------------------------------

    [Fact]
    public void Emits_indent_and_dedent_for_nesting()
    {
        Assert.Equal(
            new[]
            {
                TokenKind.Slash, TokenKind.Identifier, TokenKind.Newline,
                TokenKind.Indent, TokenKind.KeywordVar, TokenKind.Slash, TokenKind.Identifier, TokenKind.Newline,
                TokenKind.Dedent,
            },
            Kinds("/mob\n\tvar/x\n"));
    }

    [Fact]
    public void Closes_every_open_block_at_end_of_file()
    {
        LexResult result = Lex("/a\n\tb\n\t\tc");

        int dedents = 0;
        foreach (Token token in result.Tokens)
        {
            if (token.Kind == TokenKind.Dedent)
                dedents++;
        }

        Assert.Equal(2, dedents);
    }

    [Fact]
    public void Blank_lines_do_not_change_the_indent_level()
    {
        LexResult result = Lex("/a\n\tb\n\n\tc\n");

        Assert.Equal(1, CountOf(result, TokenKind.Indent));
        Assert.Equal(1, CountOf(result, TokenKind.Dedent));
    }

    [Fact]
    public void Comment_only_lines_do_not_change_the_indent_level()
    {
        // A comment sitting at column 0 inside an indented block must not close it.
        LexResult result = Lex("/a\n\tb\n// note\n\tc\n");

        Assert.Equal(1, CountOf(result, TokenKind.Indent));
        Assert.Equal(1, CountOf(result, TokenKind.Dedent));
    }

    [Fact]
    public void Inconsistent_indentation_is_reported_but_lexing_continues()
    {
        // Tab then spaces: neither prefix of the other.
        LexResult result = Lex("/a\n\tb\n    c\n");

        Assert.Contains(result.Diagnostics, d => d.Id == "DM0003");
        Assert.Equal(TokenKind.EndOfFile, result.Tokens[^1].Kind);
    }

    /// <summary>
    /// Recovery must not corrupt the enclosing levels. If the root entry were overwritten with the
    /// offending indent, column 0 would stop closing blocks for the rest of the file.
    /// </summary>
    [Fact]
    public void Inconsistent_indentation_does_not_break_later_dedents()
    {
        LexResult result = Lex("/a\n\tb\n    c\n/d\n");

        Assert.Contains(result.Diagnostics, d => d.Id == "DM0003");

        // `/d` is back at column 0, so every block opened above it must be closed by the time it
        // is reached.
        int index = 0;
        int depth = 0;
        int depthAtSlashD = -1;

        foreach (Token token in result.Tokens)
        {
            if (token.Kind == TokenKind.Indent) depth++;
            if (token.Kind == TokenKind.Dedent) depth--;

            if (token.Kind == TokenKind.Slash && result.Text.GetLinePosition(token.Span.Start).Line == 3)
                depthAtSlashD = depth;

            index++;
        }

        Assert.Equal(0, depthAtSlashD);
    }

    [Fact]
    public void A_backslash_joins_the_next_line()
    {
        // No Newline, and the continued line's leading whitespace is not an Indent.
        LexResult result = Lex("a = b \\\n\tc\n");

        Assert.Equal(1, CountOf(result, TokenKind.Newline));
        Assert.Equal(0, CountOf(result, TokenKind.Indent));
    }

    [Fact]
    public void Layout_tokens_are_suppressed_inside_parentheses()
    {
        LexResult result = Lex("f(a,\n  b)\n");

        Assert.Equal(1, CountOf(result, TokenKind.Newline));
        Assert.Equal(0, CountOf(result, TokenKind.Indent));
    }

    [Theory]
    [InlineData("/a\n\tb\n")]
    [InlineData("/a\r\n\tb\r\n")]
    [InlineData("/a\r\tb\r")]
    public void All_three_line_ending_forms_produce_the_same_structure(string source)
    {
        Assert.Equal(
            new[]
            {
                TokenKind.Slash, TokenKind.Identifier, TokenKind.Newline,
                TokenKind.Indent, TokenKind.Identifier, TokenKind.Newline,
                TokenKind.Dedent,
            },
            Kinds(source));
    }

    [Fact]
    public void Carriage_returns_never_appear_inside_token_text()
    {
        LexResult result = Lex("/mob/test\r\n\tvar/x = 1\r\n");

        foreach (Token token in result.Tokens)
        {
            if (token.Kind == TokenKind.Newline)
                continue;

            Assert.DoesNotContain('\r', result.GetText(token));
        }
    }

    // -- preprocessor ------------------------------------------------------

    [Fact]
    public void A_hash_at_the_start_of_a_line_introduces_a_directive()
    {
        Assert.Equal(
            new[] { TokenKind.Hash, TokenKind.DirectiveName, TokenKind.Identifier, TokenKind.Number },
            Kinds("#define MAX 10"));
    }

    /// <summary>
    /// Stringification. Confirmed present in stddef.dm's ASSERT macro, where it appears inside a
    /// string interpolation.
    /// </summary>
    [Fact]
    public void A_hash_elsewhere_is_stringification_not_a_directive()
    {
        Assert.Equal(
            new[]
            {
                TokenKind.StringStart,
                TokenKind.InterpolationStart,
                TokenKind.Hash,
                TokenKind.Identifier,
                TokenKind.InterpolationEnd,
                TokenKind.StringEnd,
            },
            Kinds("\"[#c]\""));
    }

    // -- whole-file invariants --------------------------------------------

    [Fact]
    public void Token_spans_tile_the_source_without_gaps_or_overlaps()
    {
        const string source = "/mob/test\n\tvar/x = 1 // note\n\tproc/F()\n\t\treturn \"a[b]c\"\n";
        LexResult result = Lex(source);

        int previousEnd = 0;
        foreach (Token token in result.Tokens)
        {
            Assert.True(token.Span.Start >= previousEnd,
                $"{token.Kind} at {token.Span} overlaps the previous token ending at {previousEnd}");
            previousEnd = token.Span.End;
        }

        Assert.Equal(source.Length, result.Tokens[^1].Span.End);
    }

    [Fact]
    public void Empty_input_produces_only_end_of_file()
    {
        LexResult result = Lex(string.Empty);

        Assert.Single(result.Tokens);
        Assert.Equal(TokenKind.EndOfFile, result.Tokens[0].Kind);
    }

    private static int CountOf(LexResult result, TokenKind kind)
    {
        int count = 0;
        foreach (Token token in result.Tokens)
        {
            if (token.Kind == kind)
                count++;
        }

        return count;
    }
}
