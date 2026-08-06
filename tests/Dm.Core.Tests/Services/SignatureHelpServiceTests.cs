using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

/// <summary>
/// The popup's contract: which call, whose proc, which parameter. The frame scan runs on tokens,
/// so a comma inside a string or a nested call must never bleed into the count — the exact traps
/// dm-patch's upstream-requests doc names as unanswerable from text.
/// </summary>
public class SignatureHelpServiceTests
{
    private static SignatureHelpResult? At(string sourceWithCaret)
    {
        int caret = sourceWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the source must mark the caret with |");

        string source = sourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        return SignatureHelpService.SignatureAt(tree, document, position.Line, position.Character);
    }

    private const string Mob =
        "/mob\n\tproc/heal(mob/target, amount as num, silent = 0)\n\t\treturn amount\n";

    [Fact]
    public void The_first_parameter_is_active_right_after_the_paren()
    {
        SignatureHelpResult? help = At(Mob + "/mob/proc/f()\n\theal(|\n");

        Assert.NotNull(help);
        // The parameter renders with the same leading slash completion's detail uses.
        Assert.Equal("heal(/mob/target, amount as num, silent = 0)", help!.Label);
        Assert.Equal(0, help.ActiveParameter);
        Assert.Equal("/mob/heal", help.Detail);
    }

    [Fact]
    public void A_comma_advances_the_active_parameter()
    {
        SignatureHelpResult? help = At(Mob + "/mob/proc/f()\n\theal(src, 5|\n");

        Assert.Equal(1, help!.ActiveParameter);
    }

    /// <summary>A comma inside a string argument is text, not a separator.</summary>
    [Fact]
    public void A_comma_inside_a_string_does_not_count()
    {
        SignatureHelpResult? help = At(Mob + "/mob/proc/f()\n\theal(\"a, b, c\", 5|\n");

        Assert.Equal(1, help!.ActiveParameter);
    }

    /// <summary>A nested call keeps its commas to itself, and the popup follows the caret.</summary>
    [Fact]
    public void A_nested_call_owns_its_own_commas()
    {
        // Caret inside the inner heal: its own first parameter.
        SignatureHelpResult? inner = At(Mob + "/mob/proc/f()\n\theal(heal(|, 1), 2)\n");
        Assert.Equal(0, inner!.ActiveParameter);

        // Caret back in the outer call, after the closed inner one: parameter one.
        SignatureHelpResult? outer = At(Mob + "/mob/proc/f()\n\theal(heal(1, 2), |\n");
        Assert.Equal(1, outer!.ActiveParameter);
    }

    /// <summary>Commas fenced by an index do not advance the call's parameter.</summary>
    [Fact]
    public void An_index_fences_its_commas()
    {
        SignatureHelpResult? help = At(Mob + "/mob/proc/f(list/L)\n\theal(L[1, 2], |\n");

        Assert.Equal(1, help!.ActiveParameter);
    }

    [Fact]
    public void A_member_call_resolves_through_its_receiver()
    {
        SignatureHelpResult? help = At(
            Mob + "/proc/f()\n\tvar/mob/m = new\n\tm.heal(src, |\n");

        Assert.NotNull(help);
        Assert.Equal("/mob/heal", help!.Detail);
        Assert.Equal(1, help.ActiveParameter);
    }

    [Fact]
    public void No_enclosing_call_means_no_popup()
    {
        Assert.Null(At(Mob + "/mob/proc/f()\n\tvar/x = 1|\n"));
        Assert.Null(At(Mob + "/mob/proc/f()\n\theal(1, 2) |\n"));
    }

    [Fact]
    public void An_unknown_callee_means_no_popup()
    {
        Assert.Null(At(Mob + "/mob/proc/f()\n\tnowhere(|\n"));
    }
}
