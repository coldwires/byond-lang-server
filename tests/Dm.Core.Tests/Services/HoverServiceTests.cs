using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

public class HoverServiceTests
{
    private static HoverResult? Hover(string sourceWithCaret)
    {
        int caret = sourceWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the source must mark the caret with |");

        string source = sourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        return HoverService.HoverAt(tree, document, position.Line, position.Character);
    }

    [Fact]
    public void Renders_the_declaration_of_a_member()
    {
        HoverResult? hover = Hover(
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n");

        Assert.NotNull(hover);
        Assert.Equal("/mob/guy/health", hover!.Detail);
        Assert.Equal("var/health = 1", hover.Signature);
    }

    /// <summary>A <c>///</c> run above the declaration is the documentation.</summary>
    [Fact]
    public void Collects_the_doc_comment_above_a_declaration()
    {
        HoverResult? hover = Hover(
            "/mob/guy\n\t/// How much damage it can take.\n\t/// Zero means dead.\n"
            + "\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n");

        Assert.NotNull(hover);
        Assert.Equal("How much damage it can take.\nZero means dead.", hover!.Documentation);
    }

    /// <summary>
    /// A blank line ends the run, because a reader treats a separated comment as unrelated.
    /// </summary>
    [Fact]
    public void A_blank_line_ends_the_doc_comment()
    {
        HoverResult? hover = Hover(
            "/mob/guy\n\t/// Not about health.\n\n\tvar/health = 1\n"
            + "/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n");

        Assert.NotNull(hover);
        Assert.Empty(hover!.Documentation);
    }

    /// <summary>An ordinary comment is not documentation.</summary>
    [Fact]
    public void A_plain_comment_is_not_documentation()
    {
        HoverResult? hover = Hover(
            "/mob/guy\n\t// just a note\n\tvar/health = 1\n"
            + "/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n");

        Assert.NotNull(hover);
        Assert.Empty(hover!.Documentation);
    }

    [Fact]
    public void Renders_a_type_path()
    {
        HoverResult? hover = Hover("/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = /obj/it|em\n");

        Assert.NotNull(hover);
        Assert.Equal("/obj/item", hover!.Detail);
        Assert.Equal("/obj/item", hover.Signature);
    }

    /// <summary>Hover shows the nearest declaration; the whole chain is go-to-definition's job.</summary>
    [Fact]
    public void An_override_chain_renders_the_nearest()
    {
        HoverResult? hover = Hover(
            "/mob/base\n\tproc/attack()\n\t\treturn\n"
            + "/mob/base/child\n\tattack()\n\t\treturn\n"
            + "/proc/f()\n\tvar/mob/base/child/c = new\n\tc.atta|ck()\n");

        Assert.NotNull(hover);
        Assert.Equal("/mob/base/child/attack()", hover!.Detail);
    }

    /// <summary>The span covers the hovered token, so a client highlights what it asked about.</summary>
    [Fact]
    public void Reports_the_hovered_token_span()
    {
        const string Source = "/mob/guy\n\tvar/health = 1\n\tproc/f()\n\t\thea|lth = 2\n";

        HoverResult? hover = Hover(Source);

        Assert.NotNull(hover);

        string text = Source.Replace("|", string.Empty);
        Assert.Equal("health", text.Substring(hover!.Span.Start, hover.Span.Length));
    }

    [Fact]
    public void An_unresolved_symbol_has_no_hover()
    {
        Assert.Null(Hover("/proc/f()\n\tnothing_at_a|ll = 1\n"));
    }

    /// <summary>
    /// A builtin member hovers even though nothing declares it: the signature is rendered from the
    /// symbol table, since there is no source line to read. Go-to-definition still returns nothing
    /// for it — there is no file to open — and that split is deliberate.
    /// </summary>
    [Fact]
    public void A_builtin_member_hovers_from_the_symbol_table()
    {
        const string SourceWithCaret = "/proc/f()\n\tvar/mob/m = new\n\tm.lo|c = null\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = Builtins.CreateTree();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        HoverResult? hover = HoverService.HoverAt(tree, document, position.Line, position.Character);

        Assert.NotNull(hover);
        Assert.Equal("/atom/loc", hover!.Detail);
        Assert.Equal("var/loc", hover.Signature);

        Assert.Empty(DefinitionService.DefinitionAt(
            tree, document, position.Line, position.Character));
    }

    /// <summary>A builtin proc renders the parameter list the reference documented.</summary>
    [Fact]
    public void A_builtin_proc_hovers_with_its_parameters()
    {
        const string SourceWithCaret = "/proc/f()\n\tvar/mob/m = new\n\tm.Mo|ve(null)\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = Builtins.CreateTree();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        HoverResult? hover = HoverService.HoverAt(tree, document, position.Line, position.Character);

        Assert.NotNull(hover);
        Assert.Equal("/atom/movable/Move()", hover!.Detail);
        Assert.Equal("Move(NewLoc, Dir=0, step_x=0, step_y=0)", hover.Signature);
    }

    /// <summary>
    /// The builtin global <c>world</c> — one of the most-typed names in DM — hovers with its type,
    /// which the builtins table carries as the var's declared type.
    /// </summary>
    [Fact]
    public void The_builtin_global_world_hovers_with_its_type()
    {
        const string SourceWithCaret = "/proc/f()\n\twor|ld.log << \"hi\"\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = Builtins.CreateTree();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        HoverResult? hover = HoverService.HoverAt(tree, document, position.Line, position.Character);

        Assert.NotNull(hover);
        Assert.Equal("/world", hover!.Detail);
        Assert.Equal("var/world/world", hover.Signature);
    }

    /// <summary>
    /// A macro hovers as its <c>#define</c> line, with the doc comment above it — the same render
    /// path every other declaration uses, since definition hands hover the site.
    /// </summary>
    [Fact]
    public void A_macro_renders_its_define_line_and_doc_comment()
    {
        const string SourceWithCaret =
            "/// One clip.\n#define AMMO_MAX 30\n/proc/f()\n\tvar/x = AMMO|_MAX\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        HoverResult? hover = HoverService.HoverAt(
            tree, document, position.Line, position.Character,
            macros: DefinitionServiceTests.Macros(source, "test.dm"));

        Assert.NotNull(hover);
        Assert.Equal("#define AMMO_MAX", hover!.Detail);
        Assert.Equal("#define AMMO_MAX 30", hover.Signature);
        Assert.Equal("One clip.", hover.Documentation);
    }
}
