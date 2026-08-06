using System.Collections.Generic;
using System.Linq;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

public class InlayHintServiceTests
{
    private static IReadOnlyList<InlayHint> Hints(string source, int startLine = 0, int endLine = int.MaxValue)
    {
        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        return InlayHintService.HintsFor(tree, document, startLine, endLine);
    }

    /// <summary>
    /// The DM-specific case the feature exists for: an untyped local whose type only inference
    /// knows. The hint shows what completion already rides on, so the two cannot disagree.
    /// </summary>
    [Fact]
    public void An_untyped_local_is_hinted_with_its_inferred_type()
    {
        InlayHint hint = Assert.Single(Hints(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item\n\treturn x\n"));

        Assert.Equal(": /obj/item", hint.Label);
        Assert.Equal(InlayHintKind.Type, hint.Kind);

        // The hint sits immediately after the name: line 3, past `\tvar/x`.
        Assert.Equal(3, hint.Position.Line);
        Assert.Equal("\tvar/x".Length, hint.Position.Character);
    }

    /// <summary>A written type needs no hint — the reader already has it.</summary>
    [Fact]
    public void A_declared_local_gets_no_hint()
    {
        Assert.Empty(Hints("/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/obj/item/x = new\n\treturn x\n"));
    }

    /// <summary>Nothing to infer means nothing to show; a plain value has no tree type.</summary>
    [Fact]
    public void A_local_with_no_inferable_type_gets_no_hint()
    {
        Assert.Empty(Hints("/proc/f()\n\tvar/x = 50\n\treturn x\n"));
    }

    /// <summary>An inference naming a type the tree does not hold is a typo, not knowledge.</summary>
    [Fact]
    public void An_inference_outside_the_tree_gets_no_hint()
    {
        Assert.Empty(Hints("/proc/f()\n\tvar/x = new /no/such/type\n\treturn x\n"));
    }

    [Fact]
    public void The_line_range_filters_hints()
    {
        const string Source =
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item\n\treturn x\n"
            + "/proc/g()\n\tvar/y = new /obj/item\n\treturn y\n";

        Assert.Equal(2, Hints(Source).Count);
        Assert.Single(Hints(Source, startLine: 0, endLine: 4));
        Assert.Single(Hints(Source, startLine: 5, endLine: 8));
    }

    /// <summary>Locals declared inside proc-level members of a type body are reached too.</summary>
    [Fact]
    public void A_local_inside_a_type_member_proc_is_hinted()
    {
        InlayHint hint = Assert.Single(Hints(
            "/obj/item\n\tvar/hp = 1\n/mob/guy\n\tproc/f()\n\t\tvar/x = new /obj/item\n\t\treturn x\n"));

        Assert.Equal(": /obj/item", hint.Label);
    }

    /// <summary>Siblings sharing one <c>var/</c> are hinted independently.</summary>
    [Fact]
    public void Siblings_are_hinted_independently()
    {
        IReadOnlyList<InlayHint> hints = Hints(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/a = new /obj/item, b = 5\n\treturn a\n");

        InlayHint hint = Assert.Single(hints);
        Assert.Equal(": /obj/item", hint.Label);
    }
}
