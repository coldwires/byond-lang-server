using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

public class DefinitionServiceTests
{
    /// <summary>
    /// Resolves at the caret marked <c>|</c>, which is removed before parsing.
    /// </summary>
    private static IReadOnlyList<DefinitionLocation> Definition(string sourceWithCaret)
    {
        int caret = sourceWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the source must mark the caret with |");

        string source = sourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        return DefinitionService.DefinitionAt(tree, document, position.Line, position.Character);
    }

    private static string TextAt(string source, DefinitionLocation location)
        => source.Substring(location.NameSpan.Start, location.NameSpan.Length);

    [Fact]
    public void A_type_path_resolves_to_its_declaration()
    {
        const string Source = "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = /obj/it|em\n";

        IReadOnlyList<DefinitionLocation> found = Definition(Source);

        DefinitionLocation location = Assert.Single(found);
        Assert.Equal("/obj/item", location.Detail);
        Assert.Equal("item", TextAt(Source.Replace("|", string.Empty), location));
    }

    /// <summary>
    /// A type reopened across declarations has several definitions, and all of them are real.
    /// </summary>
    /// <remarks>
    /// Collapsing to one would pick arbitrarily and hide the rest, which matters most in exactly
    /// the codebases that reopen types heavily.
    /// </remarks>
    [Fact]
    public void A_reopened_type_reports_every_declaration()
    {
        IReadOnlyList<DefinitionLocation> found = Definition(
            "/obj/item\n\tvar/hp = 1\n/obj/item\n\tvar/weight = 2\n/proc/f()\n\tvar/x = /obj/it|em\n");

        Assert.Equal(2, found.Count);
        Assert.All(found, l => Assert.Equal("/obj/item", l.Detail));
    }

    [Fact]
    public void A_member_resolves_through_the_receivers_type()
    {
        const string Source =
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.heal|th = 2\n";

        DefinitionLocation location = Assert.Single(Definition(Source));

        Assert.Equal("/mob/guy/health", location.Detail);
        Assert.Equal("health", TextAt(Source.Replace("|", string.Empty), location));
    }

    /// <summary>An inherited member resolves to where it is actually declared.</summary>
    [Fact]
    public void An_inherited_member_resolves_to_the_base()
    {
        DefinitionLocation location = Assert.Single(Definition(
            "/mob/base\n\tvar/health = 1\n/mob/base/child\n/proc/f()\n"
            + "\tvar/mob/base/child/c = new\n\tc.heal|th = 2\n"));

        Assert.Equal("/mob/base/health", location.Detail);
    }

    /// <summary>A proc's override chain is all of it, nearest first.</summary>
    [Fact]
    public void A_proc_reports_its_override_chain()
    {
        IReadOnlyList<DefinitionLocation> found = Definition(
            "/mob/base\n\tproc/attack()\n\t\treturn\n"
            + "/mob/base/child\n\tattack()\n\t\treturn\n"
            + "/proc/f()\n\tvar/mob/base/child/c = new\n\tc.atta|ck()\n");

        // The override on the child and the declaration on the base are both definitions.
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void A_bare_name_resolves_against_the_enclosing_type()
    {
        DefinitionLocation location = Assert.Single(Definition(
            "/mob/guy\n\tvar/health = 1\n\tproc/f()\n\t\thea|lth = 2\n"));

        Assert.Equal("/mob/guy/health", location.Detail);
    }

    /// <summary>A relative path resolves by the §4a search, same as everywhere else.</summary>
    [Fact]
    public void A_relative_path_resolves_by_searching_upward()
    {
        DefinitionLocation location = Assert.Single(Definition(
            "/x/sword\n/x/magic/sword\n/x/magic/thing\n\tvar/p = .swo|rd\n"));

        Assert.Equal("/x/magic/sword", location.Detail);
    }

    [Fact]
    public void An_unknown_name_resolves_to_nothing()
    {
        Assert.Empty(Definition("/proc/f()\n\tnothing_at_a|ll = 1\n"));
    }

    [Fact]
    public void A_position_on_no_token_resolves_to_nothing()
    {
        Assert.Empty(Definition("/obj/item\n|\n"));
    }
}
