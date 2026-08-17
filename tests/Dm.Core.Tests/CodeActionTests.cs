using Dm.Core.Services;
using Dm.Core.Symbols;

namespace Dm.Core.Tests;

/// <summary>
/// The "declare the type" fix on a member reached through an untyped local.
/// </summary>
/// <remarks>
/// This is the surface that resolves the project's one deliberate divergence from
/// <c>dm.exe</c> (PLAN.md §6): completion offers members through an inferred type the compiler
/// will not check, and until now that was only ever flagged. The fix writes the type down.
/// </remarks>
public class CodeActionTests
{
    private static IReadOnlyList<CodeAction> ActionsFor(string source, out Document document)
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", source);

        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));
        ObjectTree tree = workspace.GetObjectTree();

        document = workspace.GetDocument(Path.Combine(temp.Path, "code.dm"));
        return CodeActionService.ActionsIn(tree, document, 0, 999);
    }

    private const string Item = "/obj/item\n\tvar/hp = 1\n\tproc/use()\n\t\treturn 1\n";

    [Fact]
    public void An_untyped_local_offers_its_inferred_type()
    {
        var actions = ActionsFor(Item + "/proc/f()\n\tvar/x = new /obj/item\n\treturn x.hp\n", out _);

        CodeAction action = Assert.Single(actions);

        Assert.Equal("Declare x as /obj/item", action.Title);
        Assert.Equal("DM0400", action.DiagnosticId);
    }

    /// <summary>
    /// The edit is a zero-length insert before the name, which is what leaves everything the
    /// author wrote exactly where they wrote it.
    /// </summary>
    [Fact]
    public void The_edit_inserts_the_type_before_the_name()
    {
        var actions = ActionsFor(
            Item + "/proc/f()\n\tvar/x = new /obj/item\n\treturn x.hp\n", out Document document);

        CodeActionEdit edit = Assert.Single(Assert.Single(actions).Edits);

        Assert.Equal(0, edit.Span.Length);
        Assert.Equal("obj/item/", edit.NewText);

        // Applied, it produces the declaration dm.exe checks — asserted on the resulting TEXT
        // rather than on the offset, since an off-by-one here writes `var/obj/item/x` one
        // character out and still looks right in a span comparison.
        string text = document.Text.ToString();
        string patched = text[..edit.Span.Start] + edit.NewText + text[edit.Span.Start..];

        Assert.Contains("var/obj/item/x = new /obj/item", patched);
    }

    /// <summary>
    /// Modifiers stay where the author put them. Both orders compile and both apply the modifier
    /// (probed on 516.1687, runtime-verified), so this is a choice rather than a constraint — and
    /// the conservative choice is not to move the author's words.
    /// </summary>
    [Fact]
    public void A_modifier_is_left_where_it_was()
    {
        var actions = ActionsFor(
            Item + "/proc/f()\n\tvar/static/x = new /obj/item\n\treturn x.hp\n", out Document document);

        CodeActionEdit edit = Assert.Single(Assert.Single(actions).Edits);

        string text = document.Text.ToString();
        string patched = text[..edit.Span.Start] + edit.NewText + text[edit.Span.Start..];

        Assert.Contains("var/static/obj/item/x", patched);
    }

    [Fact]
    public void An_invoked_member_answers_the_proc_diagnostic()
    {
        var actions = ActionsFor(Item + "/proc/f()\n\tvar/x = new /obj/item\n\treturn x.use()\n", out _);

        Assert.Equal("DM0401", Assert.Single(actions).DiagnosticId);
    }

    /// <summary>
    /// A proc referenced WITHOUT parentheses offers nothing, because declaring the type would not
    /// fix it.
    /// </summary>
    /// <remarks>
    /// This test was written expecting a DM0400 fix and the service was right instead. Probed on
    /// 516.1687: <c>var/obj/item/x = new /obj/item</c> then <c>return x.use</c> — the type WRITTEN
    /// DOWN — is still <c>x.use: undefined var</c>. §8's kind-sensitivity rule, recorded there for
    /// <c>:</c>, holds for <c>.</c> as well: a proc name does not satisfy a var access, and no type
    /// declaration changes that. So the "the fix has to fix it" guard is load-bearing rather than
    /// belt-and-braces, and this is the case that proves it.
    /// </remarks>
    [Fact]
    public void A_proc_reference_without_parens_offers_nothing()
    {
        var actions = ActionsFor(Item + "/proc/f()\n\tvar/x = new /obj/item\n\treturn x.use\n", out _);

        Assert.Empty(actions);
    }

    [Fact]
    public void A_written_type_offers_nothing()
    {
        var actions = ActionsFor(
            Item + "/proc/f()\n\tvar/obj/item/x = new /obj/item\n\treturn x.hp\n", out _);

        Assert.Empty(actions);
    }

    /// <summary>
    /// The fix has to fix it. A member that is on no type would trade one error for another.
    /// </summary>
    [Fact]
    public void A_member_that_is_not_on_the_inferred_type_offers_nothing()
    {
        var actions = ActionsFor(
            Item + "/proc/f()\n\tvar/x = new /obj/item\n\treturn x.nowhere_xyz\n", out _);

        Assert.Empty(actions);
    }

    /// <summary>
    /// Nothing types the local, so there is no type to write down and no fix to offer.
    /// </summary>
    [Fact]
    public void An_uninferable_local_offers_nothing()
    {
        var actions = ActionsFor(Item + "/proc/f(v)\n\tvar/x = v\n\treturn x.hp\n", out _);

        Assert.Empty(actions);
    }

    /// <summary>
    /// A name declared twice in one proc is fixed against the declaration the USE sees, not the
    /// last one in the file — reusing a name across two loops with different types is real DM.
    /// </summary>
    [Fact]
    public void The_nearest_declaration_before_the_use_is_the_one_edited()
    {
        var actions = ActionsFor(
            Item
            + "/obj/other\n\tvar/hp = 2\n"
            + "/proc/f()\n\tvar/x = new /obj/other\n\treturn x.hp\n"
            + "/proc/g()\n\tvar/x = new /obj/item\n\treturn x.hp\n",
            out Document document);

        Assert.Equal(2, actions.Count);
        Assert.Equal("Declare x as /obj/other", actions[0].Title);
        Assert.Equal("Declare x as /obj/item", actions[1].Title);

        // Each edit lands in its own proc, which the offsets have to show rather than the titles.
        string text = document.Text.ToString();
        Assert.True(actions[0].Edits[0].Span.Start < actions[1].Edits[0].Span.Start);
        Assert.True(actions[1].Edits[0].Span.Start < text.Length);
    }

    /// <summary>
    /// A line range is a filter, so a client asking about the visible window does not pay for
    /// the whole file and does not get fixes it cannot show.
    /// </summary>
    [Fact]
    public void The_line_range_filters()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"code.dm\"\n");
        temp.Write("code.dm", Item + "/proc/f()\n\tvar/x = new /obj/item\n\treturn x.hp\n");

        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));
        ObjectTree tree = workspace.GetObjectTree();
        Document document = workspace.GetDocument(Path.Combine(temp.Path, "code.dm"));

        Assert.NotEmpty(CodeActionService.ActionsIn(tree, document, 0, 999));
        Assert.Empty(CodeActionService.ActionsIn(tree, document, 0, 2));
    }
}
