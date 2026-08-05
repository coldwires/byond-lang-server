using System.Collections.Generic;
using System.Linq;
using Dm.Core.Binding;
using Dm.Core.Diagnostics;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Binding;

/// <summary>
/// The binder's contract is asymmetric: a missed diagnostic is work outstanding, an invented one is
/// a bug. So most of these assert <b>silence</b>, and each says which way it would fail.
/// </summary>
public class BinderTests
{
    /// <summary>Binds the last file against a tree built from all of them, as a real build does.</summary>
    private static IReadOnlyList<Diagnostic> Bind(params string[] files)
    {
        List<(string, ParseResult)> parsed = new();

        for (int i = 0; i < files.Length; i++)
            parsed.Add(($"file{i}.dm", DeclarationParser.Parse(Lexer.Lex(SourceText.From(files[i])))));

        ObjectTree tree = TypeTreeBuilder.Build(parsed);

        return Binder.Bind(tree, parsed[^1].Item2.Root);
    }

    private static IReadOnlyList<string> Ids(IReadOnlyList<Diagnostic> diagnostics)
        => diagnostics.Select(d => d.Id).ToList();

    // -- what it must catch -------------------------------------------------

    [Fact]
    public void A_member_no_type_declares_is_an_undefined_var()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.nowhere_at_all\n");

        Assert.Equal(new[] { "DM0400" }, Ids(found));
    }

    [Fact]
    public void A_called_member_no_type_declares_is_an_undefined_proc()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.nowhere_at_all()\n");

        Assert.Equal(new[] { "DM0401" }, Ids(found));
    }

    /// <summary>
    /// `.` checks the declared type and nothing beneath it, so reaching a subtype's member through
    /// it is an error even though the member plainly exists. PLAN.md §4a.
    /// </summary>
    [Fact]
    public void A_member_declared_only_on_a_subtype_is_rejected_through_a_dot()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n/obj/item/sword\n\tvar/sharpness = 5\n"
            + "\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.sharpness\n");

        Assert.Equal(new[] { "DM0400" }, Ids(found));
    }

    // -- what it must not ---------------------------------------------------

    [Fact]
    public void A_declared_member_is_silent()
        => Assert.Empty(Bind("/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.hp\n"));

    [Fact]
    public void An_inherited_member_is_silent()
        => Assert.Empty(Bind(
            "/obj/item\n\tvar/hp = 1\n/obj/item/sword\n\n/proc/f()\n\tvar/obj/item/sword/S = new\n\treturn S.hp\n"));

    /// <summary>
    /// `:` widens the check to the subtype tree and, on an untyped receiver, asks only whether the
    /// name exists anywhere at all. Both are real checks, and neither is implemented — so it stays
    /// silent rather than reporting the `.` answer, which would be wrong in the invented direction.
    /// </summary>
    [Fact]
    public void A_colon_access_is_not_checked_yet()
        => Assert.Empty(Bind("/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I:nowhere\n"));

    /// <summary>
    /// dm.exe rejects every member of an untyped local, including the right one, because it does no
    /// local inference. That is a real diagnostic we do not yet raise — reporting it needs certainty
    /// that we saw the declaration, and a missed declaration form would invent errors on live code.
    /// </summary>
    [Fact]
    public void An_untyped_receiver_is_not_checked_yet()
        => Assert.Empty(Bind("/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/x = new /obj/item\n\treturn x.hp\n"));

    /// <summary>
    /// A call result has no knowable type, so dm.exe silently degrades `.` to `:` and stops
    /// checking. Reporting here would contradict the compiler on code it accepts.
    /// </summary>
    [Fact]
    public void A_call_result_receiver_is_not_checked()
        => Assert.Empty(Bind("/obj/item\n\tvar/hp = 1\n\n/proc/mk()\n\treturn new /obj/item\n\n/proc/f()\n\treturn mk().anything\n"));

    // -- regressions found on real projects, not by writing tests -----------

    /// <summary>
    /// `mob/pc/verb` heads a block of verbs on /mob/pc: only the trailing keyword is the marker.
    /// Treating the whole header as the group put every proc's `src` on the root, which reported
    /// four undefined vars on a game that compiles clean.
    /// </summary>
    [Fact]
    public void A_group_header_carrying_a_path_owns_its_children()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/mob/pc\n\tvar/shout = 1\n\nmob/pc/verb\n\tshout_at(msg)\n\t\tsrc.shout -= 1\n");

        Assert.Empty(found);
    }

    /// <summary>
    /// One proc routinely reuses a name across loops of different types. Gathering locals before the
    /// walk let the later declaration decide how the earlier loop was checked, inventing three
    /// diagnostics on a shipped game.
    /// </summary>
    [Fact]
    public void A_loop_variables_type_does_not_leak_into_another_loop()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/trainer\n\tvar/short_name = \"\"\n/obj/disc_train\n\tvar/other = 1\n"
            + "\n/proc/f()\n\tvar/list/L = list()\n\tfor(var/obj/trainer/T in L)\n\t\tT.short_name = \"x\"\n"
            + "\tfor(var/obj/disc_train/T in L)\n\t\tT.other = 2\n");

        Assert.Empty(found);
    }

    /// <summary>
    /// This used to be suppressed by a guard, because two holes in our own tree made misses like it
    /// unreliable: `builtins.txt` had no appearance vars on `/image`, and a root-level type
    /// implicitly derives from `/datum`, which the tree did not model. Both are fixed, so the guard
    /// is gone and a typo that happens to name a member of an unrelated type is reported — which is
    /// what dm.exe does, since `.` checks the declared type and nothing else.
    /// </summary>
    [Fact]
    public void A_name_that_exists_only_on_an_unrelated_type_is_still_undefined_here()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n/datum/elsewhere\n\tvar/borrowed = 0\n"
            + "\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.borrowed\n");

        Assert.Equal(new[] { "DM0400" }, Ids(found));
    }

    /// <summary>
    /// A doubled separator collapses: `/obj/.item` is `/obj/item` (PLAN.md §4a). The path parser
    /// consumed one trailing separator and stopped, so the rest of the path became member access —
    /// /tg/station's `TYPE_PROC_REF(/datum/beam/, Start)` expands to `/datum/beam/.proc/Start` and
    /// produced 71 reports of a member named `proc`.
    /// </summary>
    [Fact]
    public void A_doubled_path_separator_does_not_end_the_path()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/beam\n\tproc/Start()\n\t\treturn 1\n"
            + "\n/proc/f()\n\treturn nameof(/datum/beam/.proc/Start)\n");

        Assert.Empty(found);
    }

    /// <summary>
    /// Compiler-verified on 516.1666: `src.type`, `src.tag` and `src.vars` all resolve inside a bare
    /// `/market_values`, while a name no type declares still errors. Nothing in the path says so, and
    /// without the link a root-level datum inherited nothing at all.
    /// </summary>
    [Fact]
    public void A_root_level_project_type_inherits_datum()
    {
        ObjectTree tree = new();
        Builtins.Seed(tree);
        TypeTreeBuilder.AddFile(
            tree,
            "a.dm",
            DeclarationParser.Parse(Lexer.Lex(SourceText.From("market_values\n\tvar/value = 0\n"))));

        TypeSymbol type = tree.Find("/market_values")!;

        Assert.NotNull(tree.ResolveVar(type, "tag"));
        Assert.NotNull(tree.ResolveVar(type, "type"));
        Assert.Null(tree.ResolveVar(type, "definitely_not_a_member"));
    }

    /// <summary>
    /// The reference documents no vars on `/image` beyond `loc`, so the scrape found one and the
    /// binder reported `I.pixel_y` on a project that compiles. The rest are in `builtins.txt` now,
    /// each confirmed by compiling.
    /// </summary>
    [Fact]
    public void Image_carries_its_appearance_vars()
    {
        ObjectTree tree = new();
        Builtins.Seed(tree);

        TypeSymbol image = tree.Find("/image")!;

        Assert.NotNull(tree.ResolveVar(image, "pixel_y"));
        Assert.NotNull(tree.ResolveVar(image, "icon_state"));
        Assert.NotNull(tree.ResolveVar(image, "transform"));

        // vis_flags is on /atom/movable, not /image - it was the accidental control in the probe.
        Assert.Null(tree.ResolveVar(image, "vis_flags"));
    }
}
