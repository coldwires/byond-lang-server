using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Syntax;

public class DeclarationParserTests
{
    private static ParseResult Parse(string source)
        => DeclarationParser.Parse(Lexer.Lex(SourceText.From(source)));

    private static DeclarationSyntax Single(string source)
    {
        ParseResult result = Parse(source);
        Assert.Empty(result.Diagnostics);
        return Assert.Single(result.Root.Declarations);
    }

    // -- types -------------------------------------------------------------

    [Fact]
    public void Parses_a_type_path()
    {
        DeclarationSyntax declaration = Single("/obj/item/sword\n");

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(declaration);
        Assert.Equal(new[] { "obj", "item", "sword" }, type.Path.Segments.ToArray());
        Assert.Equal(PathAnchor.Absolute, type.Path.Anchor);
        Assert.Equal("sword", type.Name);
    }

    /// <summary>Mid-path, <c>.</c> and <c>/</c> are the same token (PLAN.md §4a).</summary>
    [Fact]
    public void Dots_and_slashes_are_equivalent_mid_path()
    {
        TypeDeclarationSyntax slashes = Assert.IsType<TypeDeclarationSyntax>(Single("/obj/item/sword\n"));
        TypeDeclarationSyntax dots = Assert.IsType<TypeDeclarationSyntax>(Single("/obj.item.sword\n"));

        Assert.Equal(slashes.Path.Segments.ToArray(), dots.Path.Segments.ToArray());
    }

    /// <summary>Leading position is where they differ, so the anchor is kept.</summary>
    [Theory]
    [InlineData("/obj\n", PathAnchor.Absolute)]
    [InlineData("obj\n", PathAnchor.Relative)]
    public void The_leading_separator_is_recorded(string source, PathAnchor expected)
    {
        Assert.Equal(expected, Single(source).Path.Anchor);
    }

    [Fact]
    public void Indented_members_belong_to_the_enclosing_type()
    {
        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Single("/mob\n\tvar/hp = 10\n"));

        DeclarationSyntax member = Assert.Single(type.Members);
        Assert.IsType<VarDeclarationSyntax>(member);
    }

    // -- vars --------------------------------------------------------------

    [Fact]
    public void Splits_a_typed_var_into_type_and_name()
    {
        // The split that makes `t.` completion possible at M6.
        VarDeclarationSyntax variable = Assert.IsType<VarDeclarationSyntax>(Single("var/mob/test/t\n"));

        Assert.Equal("t", variable.Name);
        Assert.Equal(new[] { "mob", "test" }, variable.DeclaredType!.Segments.ToArray());
    }

    [Fact]
    public void Recognises_modifiers_inside_the_path()
    {
        VarDeclarationSyntax variable = Assert.IsType<VarDeclarationSyntax>(Single("var/const/MAX = 10\n"));

        Assert.Equal("MAX", variable.Name);
        Assert.Equal(new[] { "const" }, variable.Modifiers.ToArray());
        Assert.Null(variable.DeclaredType);
        Assert.True(variable.HasInitializer);
    }

    [Fact]
    public void An_untyped_var_has_no_declared_type()
    {
        VarDeclarationSyntax variable = Assert.IsType<VarDeclarationSyntax>(Single("var/hp = 1\n"));

        Assert.Equal("hp", variable.Name);
        Assert.Null(variable.DeclaredType);
    }

    [Fact]
    public void One_var_can_declare_several_names_with_commas()
    {
        VarDeclarationSyntax variable = Assert.IsType<VarDeclarationSyntax>(Single("var/a=1,b=0,c=0\n"));

        Assert.Equal("a", variable.Name);
        Assert.Equal(new[] { "b", "c" }, variable.Siblings.Select(s => s.Name).ToArray());
    }

    /// <summary>stddef.dm writes <c>x = 0; y = 0; z = 0</c> on one line.</summary>
    [Fact]
    public void Semicolons_also_separate_names()
    {
        ParseResult result = Parse("/sound\n\tvar\n\t\tx = 0; y = 0; z = 0\n");
        TypeDeclarationSyntax sound = Assert.IsType<TypeDeclarationSyntax>(result.Root.Declarations[0]);
        TypeDeclarationSyntax varBlock = Assert.IsType<TypeDeclarationSyntax>(sound.Members[0]);
        VarDeclarationSyntax first = Assert.IsType<VarDeclarationSyntax>(varBlock.Members[0]);

        Assert.Equal("x", first.Name);
        Assert.Equal(new[] { "y", "z" }, first.Siblings.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// <c>var/const</c> heads a block; the trailing segment is a modifier, not a name. stddef.dm
    /// declares whole groups of constants this way.
    /// </summary>
    [Fact]
    public void A_modifier_can_head_a_var_block()
    {
        TypeDeclarationSyntax block = Assert.IsType<TypeDeclarationSyntax>(
            Single("var/const\n\tNORTH = 1\n\tSOUTH = 2\n"));

        Assert.Equal(2, block.Members.Count);
        Assert.Equal(new[] { "NORTH", "SOUTH" }, block.Members.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void A_bare_var_heads_a_block_of_variables()
    {
        TypeDeclarationSyntax block = Assert.IsType<TypeDeclarationSyntax>(Single("var\n\tfoo\n\tbar\n"));

        Assert.All(block.Members, m => Assert.IsType<VarDeclarationSyntax>(m));
    }

    [Theory]
    [InlineData("var/L[]\n")]
    [InlineData("var/M[10]\n")]
    [InlineData("var/grid[10][5]\n")]
    public void Bracket_list_declarations_parse(string source)
    {
        Assert.IsType<VarDeclarationSyntax>(Single(source));
    }

    // -- procs -------------------------------------------------------------

    [Fact]
    public void Parses_a_proc_with_parameters()
    {
        ProcDeclarationSyntax proc = Assert.IsType<ProcDeclarationSyntax>(
            Single("/mob/proc/Attack(mob/M, damage = 5)\n\treturn 1\n"));

        Assert.Equal("Attack", proc.Name);
        Assert.True(proc.IsNewDeclaration);
        Assert.Equal(2, proc.Parameters.Count);

        Assert.Equal("M", proc.Parameters[0].Name);
        Assert.Equal(new[] { "mob" }, proc.Parameters[0].DeclaredType!.Segments.ToArray());

        Assert.Equal("damage", proc.Parameters[1].Name);
        Assert.True(proc.Parameters[1].HasDefault);
    }

    /// <summary>
    /// A <c>proc</c> segment declares a new proc; without one the declaration overrides an
    /// inherited one. Getting it backwards is a duplicate-definition error in DM.
    /// </summary>
    [Fact]
    public void Distinguishes_a_new_proc_from_an_override()
    {
        Assert.True(Assert.IsType<ProcDeclarationSyntax>(Single("/mob/proc/F()\n\treturn\n")).IsNewDeclaration);
        Assert.False(Assert.IsType<ProcDeclarationSyntax>(Single("/mob/F()\n\treturn\n")).IsNewDeclaration);
    }

    [Fact]
    public void Recognises_verbs()
    {
        Assert.True(Assert.IsType<ProcDeclarationSyntax>(Single("/mob/verb/Say()\n\treturn\n")).IsVerb);
    }

    [Fact]
    public void Reads_an_as_clause_on_a_parameter()
    {
        ProcDeclarationSyntax proc = Assert.IsType<ProcDeclarationSyntax>(
            Single("/mob/verb/Tell(msg as text)\n\treturn\n"));

        Assert.Equal("msg", proc.Parameters[0].Name);
        Assert.Equal("text", proc.Parameters[0].InputType);
    }

    /// <summary>
    /// The lexer emits <c>operator:=</c> as three tokens, so the name is reassembled. Without it the
    /// declaration parses as a type and its body is read as declarations.
    /// </summary>
    [Theory]
    [InlineData("/sound/proc/operator:=(sound/S)\n\treturn\n", "operator:=")]
    [InlineData("/matrix/proc/operator+(m)\n\treturn\n", "operator+")]
    public void Reassembles_overloaded_operator_names(string source, string expected)
    {
        Assert.Equal(expected, Assert.IsType<ProcDeclarationSyntax>(Single(source)).Name);
    }

    [Fact]
    public void A_proc_body_is_not_parsed_as_declarations()
    {
        // Statements inside a body must not become members of anything.
        ParseResult result = Parse("/mob/proc/F()\n\tvar/x = 1\n\treturn x\n/mob/proc/G()\n\treturn\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Root.Declarations.Count);
        Assert.Equal(new[] { "F", "G" }, result.Root.Declarations.Select(d => d.Name).ToArray());
    }

    /// <summary>
    /// A comment-only line still emits its newline, so the Indent opening a block is not always the
    /// next token. Missing this left proc bodies unskipped and their statements parsed as members.
    /// </summary>
    [Fact]
    public void A_comment_between_a_signature_and_its_body_does_not_break_skipping()
    {
        ParseResult result = Parse("/mob/proc/F()\n\t// explanation\n\tvar\n\t\tinner = 1\n/mob/proc/G()\n\treturn\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Root.Declarations.Count);
    }

    /// <summary>
    /// A directive line emits no Indent, so one sitting between a header and its body hid the Indent
    /// the following code line does emit. The body was then read as declarations and everything after
    /// it was lost. dm.exe 516.1666 compiles this shape with 0 errors; found in mlaas/src/spies.dm.
    /// </summary>
    [Fact]
    public void A_directive_between_a_signature_and_its_body_does_not_break_skipping()
    {
        ParseResult result = Parse("/mob/proc/F()\n\t#ifdef P\n\tvar\n\t\tseen = 0\n\t#endif\n\treturn seen\n/mob/proc/G()\n\treturn\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "F", "G" }, result.Root.Declarations.Select(d => d.Name).ToArray());
    }

    /// <summary>Same hole on the parse path: a directive opening a type block hid its members.</summary>
    [Fact]
    public void A_directive_at_the_top_of_a_type_block_does_not_hide_its_members()
    {
        ParseResult result = Parse("/mob/test\n\t#ifdef P\n\tvar/a = 1\n\t#endif\n\tvar/b = 2\n");

        Assert.Empty(result.Diagnostics);
        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        Assert.Equal(new[] { "a", "b" }, type.Members.Select(m => m.Name).ToArray());
    }

    /// <summary>
    /// A line holding only <c>;</c> is an empty declaration. dm.exe 516.1666 accepts one at file
    /// scope, indented inside a type block, and doubled, all in the same file. Real code leaves them
    /// behind when the statement they terminated gets commented out — two files in madridspy do.
    /// </summary>
    [Theory]
    [InlineData("/mob/proc/F()\n\treturn\n;\n/mob/proc/G()\n\treturn\n")]
    [InlineData("/mob/proc/F()\n\treturn\n;;\n/mob/proc/G()\n\treturn\n")]
    [InlineData("/mob/proc/F()\n\treturn\r\n;\r\n/mob/proc/G()\n\treturn\n")]
    public void A_line_holding_only_a_semicolon_is_not_a_declaration(string source)
    {
        ParseResult result = Parse(source);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(new[] { "F", "G" }, result.Root.Declarations.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void A_semicolon_line_inside_a_type_block_does_not_hide_its_members()
    {
        ParseResult result = Parse("/mob/test\n\t;\n\tvar/v = 1\n");

        Assert.Empty(result.Diagnostics);
        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        Assert.Equal("v", Assert.Single(type.Members).Name);
    }

    /// <summary>A var initialiser is parsed as an expression rather than skipped.</summary>
    [Fact]
    public void A_var_initializer_is_parsed()
    {
        VarDeclarationSyntax declaration = Assert.IsType<VarDeclarationSyntax>(Single("/mob/var/hp = 1 + 2\n"));

        Assert.True(declaration.HasInitializer);
        Assert.Equal(TokenKind.Plus, Assert.IsType<BinaryExpressionSyntax>(declaration.Initializer).OperatorToken);
    }

    /// <summary>Each name in a comma-separated var list keeps its own initialiser.</summary>
    [Fact]
    public void Each_var_in_a_list_keeps_its_own_initializer()
    {
        VarDeclarationSyntax declaration = Assert.IsType<VarDeclarationSyntax>(Single("/mob/var/a = 1, b = \"two\"\n"));

        Assert.Equal(LiteralKind.Number, Assert.IsType<LiteralExpressionSyntax>(declaration.Initializer).Kind);

        VarDeclarationSyntax sibling = Assert.Single(declaration.Siblings);
        Assert.Equal(LiteralKind.String, Assert.IsType<LiteralExpressionSyntax>(sibling.Initializer).Kind);
    }

    /// <summary>
    /// A bare assignment at type level overrides an inherited var. stddef.dm relies on it for
    /// <c>_dm_interface = _DM_datum|_DM_sound</c>, and every <c>world/maxx = 3</c> is one.
    /// </summary>
    [Fact]
    public void A_bare_assignment_declares_a_var_not_a_type()
    {
        VarDeclarationSyntax declaration =
            Assert.IsType<VarDeclarationSyntax>(Assert.Single(Parse("maxx = 3\n").Root.Declarations));

        Assert.Equal("maxx", declaration.Name);
        Assert.True(declaration.HasInitializer);
    }

    // -- robustness --------------------------------------------------------

    [Fact]
    public void Preprocessor_directives_are_skipped()
    {
        ParseResult result = Parse("#define MAX 10\n#include \"a.dm\"\n/mob/test\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal("test", Assert.Single(result.Root.Declarations).Name);
    }

    [Fact]
    public void An_unparseable_line_does_not_stop_the_file()
    {
        ParseResult result = Parse("/mob/a\n$$$ ???\n/mob/b\n");

        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Root.Declarations, d => d.Name == "a");
        Assert.Contains(result.Root.Declarations, d => d.Name == "b");
    }

    [Fact]
    public void An_empty_file_parses_to_nothing()
    {
        ParseResult result = Parse(string.Empty);

        Assert.Empty(result.Root.Declarations);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Name_spans_point_at_the_declared_name()
    {
        ParseResult result = Parse("/obj/item/sword\n");
        DeclarationSyntax declaration = result.Root.Declarations[0];

        Assert.Equal("sword", result.Text.ToString(declaration.NameSpan));
    }
}
