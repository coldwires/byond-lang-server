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
    public void Recognises_keywords(string source, object expected)
    {
        Assert.Equal(new[] { (TokenKind)expected }, Kinds(source));
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

    /// <summary>
    /// Verified against dm.exe 516.1666: <c>/*</c> then <c>// */</c> then code then <c>*/</c>
    /// compiles clean, so the delimiter on the <c>//</c> line was ignored.
    /// </summary>
    [Fact]
    public void A_line_comment_inside_a_block_comment_hides_a_closing_delimiter()
    {
        LexResult result = Lex("/*\n// */\nstill inside\n*/ after");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(TokenKind.Comment, result.Tokens[0].Kind);
        Assert.Equal("/*\n// */\nstill inside\n*/", result.GetText(result.Tokens[0]));
    }

    /// <summary>
    /// The real-world case: a `//*` line inside a block comment must not nest, or it swallows the
    /// rest of the file.
    /// </summary>
    [Fact]
    public void A_line_comment_inside_a_block_comment_hides_an_opening_delimiter()
    {
        LexResult result = Lex("/*\n//*see the article\n*/\n/mob/a\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("/*\n//*see the article\n*/", result.GetText(result.Tokens[0]));
        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.Identifier);
    }

    /// <summary>
    /// Quotes get no special treatment inside a block comment. dm.exe reports "unterminated text"
    /// for <c>"*/"</c> inside one, which means the delimiter closed the comment and left a stray
    /// quote behind.
    /// </summary>
    [Fact]
    public void A_quote_does_not_protect_a_closing_delimiter()
    {
        LexResult result = Lex("/* \"*/\" ");

        Assert.Equal("/* \"*/", result.GetText(result.Tokens[0]));
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

    /// <summary>
    /// A backslash before a line break continues the string onto the next line. Found in real
    /// code, used constantly for long description text.
    /// </summary>
    [Theory]
    [InlineData("\"one \\\ntwo\"")]      // LF
    [InlineData("\"one \\\r\ntwo\"")]    // CRLF
    [InlineData("\"one \\\rtwo\"")]      // lone CR
    public void A_backslash_before_a_line_break_continues_the_string(string source)
    {
        LexResult result = Lex(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds(source));
    }

    /// <summary>
    /// Regression: skipping a fixed two characters ate the CR of a CRLF pair and left the LF, which
    /// then read as the end of an unterminated string. Invisible on LF files, so only
    /// Windows-authored source exposed it.
    /// </summary>
    [Fact]
    public void A_crlf_continuation_does_not_leak_a_newline_into_the_code_stream()
    {
        LexResult result = Lex("var/s = \"a \\\r\nb\"\r\nvar/t = 1\r\n");

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.Tokens, t => t.Kind == TokenKind.Unknown);

        // Two statements, so exactly two newlines survive.
        Assert.Equal(2, CountOf(result, TokenKind.Newline));
    }

    // -- raw strings -------------------------------------------------------

    [Fact]
    public void A_raw_string_does_not_process_escapes()
    {
        LexResult result = Lex("@\"C:\\path\\x01\"");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds("@\"C:\\path\\x01\""));
        Assert.Equal("C:\\path\\x01", result.GetText(result.Tokens[1]));
    }

    /// <summary>
    /// The reason raw strings exist. A regex character class would otherwise open an interpolation
    /// hole and swallow the rest of the pattern.
    /// </summary>
    [Fact]
    public void A_raw_string_does_not_interpolate()
    {
        const string source = "@\"[^\\x01-\\xFF]\"";
        LexResult result = Lex(source);

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.Tokens, t => t.Kind == TokenKind.InterpolationStart);
        Assert.Equal("[^\\x01-\\xFF]", result.GetText(result.Tokens[1]));
    }

    [Fact]
    public void A_raw_string_ends_at_the_first_quote()
    {
        LexResult result = Lex("@\"ab\" + 1");

        Assert.Equal(TokenKind.StringEnd, result.Tokens[2].Kind);
        Assert.Equal(TokenKind.Plus, result.Tokens[3].Kind);
    }

    [Fact]
    public void A_raw_multiline_string_is_supported()
    {
        LexResult result = Lex("@{\"a\\b\nc\"}");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("a\\b\nc", result.GetText(result.Tokens[1]));
    }

    /// <summary>
    /// The reference documents that a raw string's delimiter is <b>any single character</b>, and
    /// all of these compile. The regex form is the one that matters: treating <c>@</c> as only ever
    /// introducing <c>@"</c> mis-lexes <c>@/(\d+)/</c> as a division — a silent wrong answer.
    /// </summary>
    [Theory]
    [InlineData("@\"body\"", "body")]
    [InlineData("@#body, \"quotes\" fine#", "body, \"quotes\" fine")]
    [InlineData("@/(\\d+)/", "(\\d+)")]
    [InlineData("@!body!", "body")]
    [InlineData("@|body|", "body")]
    public void A_raw_string_delimiter_may_be_any_single_character(string source, string expected)
    {
        LexResult result = Lex(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds(source));
        Assert.Equal(expected, result.GetText(result.Tokens[1]));
    }

    [Fact]
    public void A_raw_regex_string_does_not_lex_as_division()
    {
        LexResult result = Lex("var/r = @/(\\d+)/\nvar/n = 1\n");

        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(result.Tokens, t => t.Kind == TokenKind.Slash && result.GetText(t) == "/"
            && result.Text.GetLinePosition(t.Span.Start).Line == 0
            && result.Text.GetLinePosition(t.Span.Start).Character > 7);
    }

    /// <summary>
    /// <c>@(XYZ)…XYZ</c> — an arbitrary multi-character terminator, spanning lines.
    /// </summary>
    [Fact]
    public void A_raw_string_may_use_an_arbitrary_multi_character_terminator()
    {
        LexResult result = Lex("@(~~~)\nline one\nline two\n~~~");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new[] { TokenKind.StringStart, TokenKind.StringText, TokenKind.StringEnd },
            Kinds("@(~~~)\nline one\nline two\n~~~"));
        Assert.Equal("\nline one\nline two\n", result.GetText(result.Tokens[1]));
    }

    [Fact]
    public void A_single_line_raw_string_does_not_span_lines()
    {
        LexResult result = Lex("@#unclosed\nvar/x = 1\n");

        Assert.Contains(result.Diagnostics, d => d.Id == "DM0001");
        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.KeywordVar);
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
    // Found in the DM Reference's /operator index, not in 2.4M tokens of corpus. All four
    // verified to compile.
    [InlineData("%%", TokenKind.PercentPercent)]
    [InlineData("%%=", TokenKind.PercentPercentAssign)]
    [InlineData("<=>", TokenKind.Spaceship)]
    [InlineData(":=", TokenKind.ColonAssign)]
    public void Operators_match_longest_first(string source, object expected)
    {
        Assert.Equal(new[] { (TokenKind)expected }, Kinds(source));
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

    /// <summary>
    /// Directives sit at column 0 regardless of the surrounding indentation and neither open nor
    /// close a block. Found in real code: a depth-2 body wrapped in a column-0 <c>#ifdef</c>
    /// dedented the lexer to the root, and every later dedent to depth 1 then failed.
    /// </summary>
    [Fact]
    public void Preprocessor_directives_do_not_change_the_indent_level()
    {
        LexResult result = Lex(
            "/mob\n" +
            "\tproc/F()\n" +
            "\t\tvar/a = 1\n" +
            "#ifdef DEBUG\n" +
            "\t\tvar/b = 2\n" +
            "#endif\n" +
            "\tproc/G()\n");

        Assert.Empty(result.Diagnostics);

        // Two blocks opened (\t then \t\t) and both closed, with nothing spurious in between.
        Assert.Equal(2, CountOf(result, TokenKind.Indent));
        Assert.Equal(2, CountOf(result, TokenKind.Dedent));
    }

    [Fact]
    public void A_directive_at_arbitrary_depth_is_also_layout_neutral()
    {
        // Real code puts them indented too.
        LexResult result = Lex(
            "/mob\n" +
            "\tproc/F()\n" +
            "\t\tvar/a = 1\n" +
            "\t\t#pragma pop\n" +
            "\tproc/G()\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, CountOf(result, TokenKind.Indent));
    }

    /// <summary>
    /// Depth follows dm.exe, not a tidier rule. Against a sibling at one tab, the compiler accepts
    /// <c>" \t"</c>, <c>"\t "</c> and <c>" "</c> as the same level. Prefix comparison — which this
    /// used to do — rejected the first two, flagging code DM compiles.
    /// </summary>
    [Theory]
    [InlineData("\t")]      // control
    [InlineData(" \t")]     // space then tab
    [InlineData("\t ")]     // tab then space
    [InlineData(" ")]       // a single space, no tabs at all
    public void Whitespace_forms_the_compiler_accepts_are_the_same_depth(string indent)
    {
        LexResult result = Lex($"client\n\tNorth()\n{indent}South()\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(1, CountOf(result, TokenKind.Indent));
        Assert.Equal(1, CountOf(result, TokenKind.Dedent));
    }

    [Fact]
    public void Tabs_decide_depth_when_both_tabs_and_spaces_are_present()
    {
        // Two tabs is deeper than one, whatever the spaces around them do.
        LexResult result = Lex("a\n \tb\n \t\tc\n \td\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, CountOf(result, TokenKind.Indent));
    }

    /// <summary>
    /// Odd indentation must not corrupt the enclosing levels: a line back at column 0 has to close
    /// every block above it, whatever happened in between.
    /// </summary>
    [Fact]
    public void Column_zero_still_closes_every_block_after_odd_indentation()
    {
        LexResult result = Lex("/a\n\tb\n    c\n/d\n");

        int depth = 0;
        int depthAtSlashD = -1;

        foreach (Token token in result.Tokens)
        {
            if (token.Kind == TokenKind.Indent) depth++;
            if (token.Kind == TokenKind.Dedent) depth--;

            if (token.Kind == TokenKind.Slash && result.Text.GetLinePosition(token.Span.Start).Line == 3)
                depthAtSlashD = depth;
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
    /// Verified against dm.exe: <c>#warn this won't work and "unbalanced</c> compiles with 0
    /// errors and prints the line verbatim. Found in a BYOND library whose apostrophe in "won't"
    /// otherwise opened a resource literal and ran to end of line.
    /// </summary>
    [Theory]
    [InlineData("#warn HudLib won't work")]
    [InlineData("#error it doesn't build")]
    [InlineData("#warn an \"unbalanced quote")]
    [InlineData("#warn trailing // not a comment")]
    public void Warn_and_error_take_free_text_not_tokens(string source)
    {
        LexResult result = Lex(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            new[] { TokenKind.Hash, TokenKind.DirectiveName, TokenKind.DirectiveText },
            Kinds(source));
    }

    [Fact]
    public void Directive_free_text_stops_at_the_line_break()
    {
        LexResult result = Lex("#warn don't\n/mob/a\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("don't", result.GetText(result.Tokens[2]));
        Assert.Contains(result.Tokens, t => t.Kind == TokenKind.Identifier);
    }

    [Fact]
    public void Other_directives_still_tokenize_their_body()
    {
        // Only warn and error are free text. A #define body is a replacement list.
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
