using Dm.Core.Diagnostics;
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
    public void The_leading_separator_is_recorded(string source, object expected)
    {
        Assert.Equal((PathAnchor)expected, Single(source).Path.Anchor);
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

    /// <summary>
    /// The modifier word heads a block only when the line ends there. With anything after it,
    /// it is a variable NAMED final/const/tmp — /tg/station writes <c>var/final = ""</c> and
    /// dm.exe accepts every modifier word as a name, with uses, at type level too.
    /// </summary>
    [Fact]
    public void A_modifier_word_with_an_initializer_is_a_var_named_that()
    {
        VarDeclarationSyntax var = Assert.IsType<VarDeclarationSyntax>(Single("var/final = 3\n"));

        Assert.Equal("final", var.Name);
        Assert.Empty(var.Modifiers);
        Assert.True(var.HasInitializer);
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

    // -- `;` at declaration level -------------------------------------------

    /// <summary>
    /// A <c>;</c> can end a var declaration outright, leaving a fresh declaration on the same line,
    /// and the indented block below belongs to that one rather than to the var.
    /// </summary>
    /// <remarks>
    /// Verified against dm.exe 516.1666: <c>var/a = 1; /datum/x</c> declares both. Macro-heavy code
    /// reaches this constantly — tgstation's <c>SUBSYSTEM_DEF</c> expands to exactly this shape, and
    /// treating the remainder as part of the var swallowed the type and every member under it,
    /// silently. Recovering it moved proc recall on that codebase from 96.04% to 97.90%.
    /// </remarks>
    [Fact]
    public void A_semicolon_can_end_a_var_and_start_a_new_declaration()
    {
        ParseResult result = Parse("var/glair;/datum/sub/air\n\tvar/thing = 1\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Root.Declarations.Count);

        VarDeclarationSyntax global = Assert.IsType<VarDeclarationSyntax>(result.Root.Declarations[0]);
        Assert.Equal("glair", result.Text.ToString(global.NameSpan));

        // The block under the line is the type's, not the var's.
        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(result.Root.Declarations[1]);
        Assert.Equal("/datum/sub/air", type.Path.Text);
        Assert.Single(type.Members);
    }

    /// <summary>Names after a <c>;</c> still share the <c>var/</c>, which is the older behaviour.</summary>
    [Fact]
    public void A_semicolon_still_separates_names_sharing_one_var()
    {
        DeclarationSyntax declaration = Single("var/a; b\n");

        VarDeclarationSyntax variable = Assert.IsType<VarDeclarationSyntax>(declaration);
        Assert.Single(variable.Siblings);
    }

    /// <summary>A trailing <c>;</c> with nothing after it still ends the line normally.</summary>
    [Fact]
    public void A_trailing_semicolon_does_not_split_the_declaration()
    {
        ParseResult result = Parse("var/a;\n/datum/x\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Root.Declarations.Count);
    }

    // -- parameter defaults -------------------------------------------------

    private static ParameterSyntax OnlyParameter(string source)
    {
        ParseResult result = Parse(source);
        Assert.Empty(result.Diagnostics);

        ProcDeclarationSyntax proc = Assert.IsType<ProcDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        return Assert.Single(proc.Parameters);
    }

    /// <remarks>
    /// The default used to be a bool with nothing behind it, so `f(x = 5)` and `f(x = get_max())`
    /// were indistinguishable to everything downstream.
    /// </remarks>
    [Fact]
    public void A_parameter_default_is_parsed_as_an_expression()
    {
        ParameterSyntax parameter = OnlyParameter("/proc/f(x = 5)\n\treturn\n");

        Assert.True(parameter.HasDefault);
        LiteralExpressionSyntax literal = Assert.IsType<LiteralExpressionSyntax>(parameter.DefaultValue);
        Assert.Equal("5", literal.Text);
    }

    [Fact]
    public void A_parameter_without_a_default_has_neither_flag_nor_tree()
    {
        ParameterSyntax parameter = OnlyParameter("/proc/f(x)\n\treturn\n");

        Assert.False(parameter.HasDefault);
        Assert.Null(parameter.DefaultValue);
    }

    /// <summary>
    /// The parameter list is split on commas at paren depth 1, so a call in a default keeps its own
    /// commas. Reading past the parameter would take the next one with it.
    /// </summary>
    [Fact]
    public void A_default_may_contain_commas_of_its_own()
    {
        ParseResult result = Parse("/proc/f(x = list(1, 2), y = 3)\n\treturn\n");

        Assert.Empty(result.Diagnostics);

        ProcDeclarationSyntax proc = Assert.IsType<ProcDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        Assert.Equal(2, proc.Parameters.Count);

        Assert.IsType<InvocationExpressionSyntax>(proc.Parameters[0].DefaultValue);
        Assert.Equal("y", proc.Parameters[1].Name);
        Assert.IsType<LiteralExpressionSyntax>(proc.Parameters[1].DefaultValue);
    }

    /// <summary>A default sits after the type and before the <c>as</c> clause, and none of them collide.</summary>
    [Fact]
    public void A_default_coexists_with_a_declared_type()
    {
        ParameterSyntax parameter = OnlyParameter("/proc/f(obj/item/I = /obj/item/sword)\n\treturn\n");

        Assert.Equal("I", parameter.Name);
        Assert.Equal("/obj/item", parameter.DeclaredType?.Text);
        Assert.IsType<PathExpressionSyntax>(parameter.DefaultValue);
    }

    // -- brace blocks -------------------------------------------------------

    /// <summary>
    /// The shape macro-generated code produces, since a <c>\</c>-continued macro body has no lines
    /// to indent. tgstation's ADMIN_VERB family is exactly this, all on one logical line.
    /// </summary>
    [Fact]
    public void A_brace_block_holds_declarations_separated_by_semicolons()
    {
        ParseResult result = Parse("/datum/av/x { name = \"n\"; desc = \"d\" }\n");

        Assert.Empty(result.Diagnostics);

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));

        // Both overrides are here: a `;` between names carries them as siblings of one
        // declaration, the same shape `var/a; b` produces.
        VarDeclarationSyntax first = Assert.IsType<VarDeclarationSyntax>(Assert.Single(type.Members));
        Assert.Equal("name", first.Name);
        Assert.Equal("desc", Assert.Single(first.Siblings).Name);
    }

    /// <remarks>
    /// dm.exe puts `/datum/av/x` and `/datum/av/y` side by side under `/datum/av`. The object tree
    /// agreed already, because it attributes by full path and a wrongly nested absolute path still
    /// lands in the right place — so only the outline showed it, with `y` drawn inside `x`.
    /// </remarks>
    [Fact]
    public void A_declaration_after_a_brace_block_is_a_sibling_of_it()
    {
        ParseResult result = Parse("/datum/av/x { name = \"n\" }\n\n/datum/av/y\n\tname = \"n\"\n");

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Root.Declarations.Count);
    }

    /// <remarks>
    /// Compiler-verified (PLAN.md §8): braces and indentation nest freely, and the braced form
    /// produces a tree identical to the all-indented one in <c>dm.exe -o</c>. We used to ignore the
    /// Indent inside the braces, which lost the members and reported an error per line.
    /// </remarks>
    [Fact]
    public void A_brace_block_can_hold_an_indented_var_block()
    {
        ParseResult result = Parse("/obj/one {\n\tvar\n\t\ta = 1\n\t\tb = 2\n}\n");

        Assert.Empty(result.Diagnostics);

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        TypeDeclarationSyntax varBlock = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(type.Members));

        Assert.Equal(2, varBlock.Members.Count);
        Assert.Equal(new[] { "a", "b" }, varBlock.Members.Select(m => m.Name).ToArray());
    }

    [Fact]
    public void A_brace_block_can_hold_a_proc_with_an_indented_body()
    {
        ParseResult result = Parse("/obj/two {\n\tproc/f()\n\t\treturn 1\n}\n");

        Assert.Empty(result.Diagnostics);

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        Assert.IsType<ProcDeclarationSyntax>(Assert.Single(type.Members));
    }

    [Fact]
    public void A_brace_block_can_hold_a_subtype_declared_by_indentation()
    {
        ParseResult result = Parse("/obj/three {\n\tsub\n\t\tvar/c = 1\n}\n");

        Assert.Empty(result.Diagnostics);

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        TypeDeclarationSyntax sub = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(type.Members));

        Assert.Equal("sub", sub.Name);
        Assert.IsType<VarDeclarationSyntax>(Assert.Single(sub.Members));
    }

    /// <summary>
    /// The control the compiler run used: the braced and indented forms have to agree, since
    /// <c>dm.exe -o</c> prints the same tree for both.
    /// </summary>
    [Fact]
    public void The_braced_and_indented_forms_declare_the_same_thing()
    {
        ParseResult braced = Parse("/obj/x {\n\tvar\n\t\ta = 1\n}\n");
        ParseResult indented = Parse("/obj/x\n\tvar\n\t\ta = 1\n");

        static string Shape(DeclarationSyntax declaration)
        {
            string children = string.Join(
                ",",
                declaration is TypeDeclarationSyntax type
                    ? type.Members.Select(Shape)
                    : Enumerable.Empty<string>());

            return $"{declaration.GetType().Name}:{declaration.Name}({children})";
        }

        Assert.Equal(
            Shape(Assert.Single(indented.Root.Declarations)),
            Shape(Assert.Single(braced.Root.Declarations)));
    }

    // -- declarations the compiler discards ---------------------------------

    /// <remarks>
    /// dm.exe compiles this with 0 errors and 0 warnings and declares nothing: `vanished` is not a
    /// proc, not a var, and absent from `vars`. See PLAN.md §8 and §18 of the language notes, which
    /// runs it. The sibling var beside it is unaffected, which is why nothing looks wrong.
    /// </remarks>
    [Fact]
    public void A_proc_block_inside_a_var_block_declares_nothing()
    {
        ParseResult result = Parse("/datum/swallowed\n\tvar\n\t\tkept = 1\n\t\tproc\n\t\t\tvanished()\n");

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        TypeDeclarationSyntax varBlock = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(type.Members));

        DeclarationSyntax kept = Assert.Single(varBlock.Members);
        Assert.IsType<VarDeclarationSyntax>(kept);
        Assert.Equal("kept", kept.Name);
    }

    /// <summary>
    /// Dropping them silently would match the compiler and help nobody: nothing else in a DM
    /// toolchain reports this, and the author's proc does not exist at runtime.
    /// </summary>
    [Fact]
    public void A_discarded_proc_block_is_reported_as_a_warning()
    {
        ParseResult result = Parse("/datum/swallowed\n\tvar\n\t\tkept = 1\n\t\tproc\n\t\t\tvanished()\n");

        Diagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DM0300", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);

        // Anchored on the misplaced header, which is the line to dedent.
        Assert.Equal("proc", result.Text.ToString(diagnostic.Span));
    }

    /// <summary><c>verb</c> is the same shape and the same outcome.</summary>
    [Fact]
    public void A_verb_block_inside_a_var_block_is_discarded_too()
    {
        ParseResult result = Parse("/mob\n\tvar\n\t\tverb\n\t\t\tsay_hi()\n");

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        TypeDeclarationSyntax varBlock = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(type.Members));

        Assert.Empty(varBlock.Members);
        Assert.Equal("DM0300", Assert.Single(result.Diagnostics).Id);
    }

    /// <summary>
    /// The negative control: one level out, the same block is an ordinary proc group and declares
    /// the proc. Without this the test above would pass against a parser that dropped every proc.
    /// </summary>
    [Fact]
    public void A_proc_block_beside_the_var_block_still_declares()
    {
        ParseResult result = Parse("/datum/kept\n\tvar\n\t\tkept = 1\n\tproc\n\t\tsurvives()\n");

        Assert.Empty(result.Diagnostics);

        TypeDeclarationSyntax type = Assert.IsType<TypeDeclarationSyntax>(Assert.Single(result.Root.Declarations));
        Assert.Equal(2, type.Members.Count);

        TypeDeclarationSyntax procBlock = Assert.IsType<TypeDeclarationSyntax>(type.Members[1]);
        Assert.IsType<ProcDeclarationSyntax>(Assert.Single(procBlock.Members));
    }
}
