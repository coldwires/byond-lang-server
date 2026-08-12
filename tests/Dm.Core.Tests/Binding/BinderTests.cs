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
    /// <summary>
    /// dm.exe's <c>new_name</c>, worth 9 of the 16 diagnostics the corpus said we were missing.
    /// A name check: no tree walk, no evaluator.
    /// </summary>
    [Fact]
    public void Calling_lentext_reports_the_deprecation()
    {
        Assert.Contains(
            Bind("/proc/f(t)\n\treturn lentext(t)\n"),
            d => d.Id == "new_name" && d.Message.Contains("phased out"));
    }

    /// <summary>A project declaring its own shadows the builtin, so there is nothing to deprecate.</summary>
    [Fact]
    public void A_project_lentext_is_not_the_deprecated_one()
    {
        Assert.DoesNotContain(
            Bind("/proc/lentext(t)\n\treturn 1\n/proc/f(t)\n\treturn lentext(t)\n"),
            d => d.Id == "new_name");
    }

    /// <summary>
    /// <c>no_parent</c>: a <c>proc/</c> new declaration has nothing above it. Compiler-verified
    /// one case per proc — the global and the fresh declaration warn, every override does not.
    /// </summary>
    [Theory]
    [InlineData("/proc/global_orphan()\n\treturn ..()\n")]
    [InlineData("/datum/a\n\tproc/fresh()\n\t\treturn ..()\n")]
    [InlineData("/datum/b\n\tproc/Login()\n\t\treturn ..()\n")]
    public void A_new_declaration_has_no_parent_to_call(string source)
    {
        Assert.Contains(Bind(source), d => d.Id == "no_parent");
    }

    /// <summary>
    /// An override always reaches something: a project proc on a subtype, a builtin, or the same
    /// type's own earlier declaration. Warning here would be inventing on working code.
    /// </summary>
    [Theory]
    [InlineData("/datum/a\n\tproc/fresh()\n\t\treturn 1\n/datum/a/sub\n\tfresh()\n\t\treturn ..()\n")]
    [InlineData("/mob/Login()\n\treturn ..()\n")]
    [InlineData("/datum/c\n\tproc/twice()\n\t\treturn 1\n/datum/c/twice()\n\treturn ..()\n")]
    public void An_override_does_not_report_no_parent(string source)
    {
        Assert.DoesNotContain(Bind(source), d => d.Id == "no_parent");
    }

    /// <summary>Binds the last file against a tree built from all of them, as a real build does.</summary>
    private static IReadOnlyList<Diagnostic> Bind(params string[] files)
    {
        List<(string, ParseResult)> parsed = new();

        for (int i = 0; i < files.Length; i++)
            parsed.Add(($"file{i}.dm", DeclarationParser.Parse(Lexer.Lex(SourceText.From(files[i])))));

        ObjectTree tree = TypeTreeBuilder.Build(parsed);

        return Binder.Bind(tree, parsed[^1].Item2.Root, parsed[^1].Item1);
    }

    private static IReadOnlyList<string> Ids(IReadOnlyList<Diagnostic> diagnostics)
        => diagnostics.Select(d => d.Id).ToList();

    // -- duplicate definitions (DM0403) --------------------------------------
    // Compiler-verified shapes, probes dup1-dup9: proc/ twice on one type, on an ancestor at any
    // depth, on the root, and against a builtin are all errors; an override and a var sharing a
    // proc's name are not.

    [Fact]
    public void A_proc_declared_twice_on_one_type_reports_the_pair()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing\n\tproc/f()\n\t\treturn 1\n\tproc/f()\n\t\treturn 2\n");

        Assert.Equal(new[] { "DM0403", "DM0403" }, Ids(found));
        Assert.Contains(found, d => d.Message.Contains("previous definition"));
        Assert.Contains(found, d => d.Message.Contains("duplicate definition"));
    }

    [Fact]
    public void A_proc_redeclared_on_a_subtype_reports_the_pair_in_one_file()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing\n\tproc/f()\n\t\treturn 1\n\n/datum/thing/mid\n\n"
            + "/datum/thing/mid/deep\n\tproc/f()\n\t\treturn 2\n");

        Assert.Equal(new[] { "DM0403", "DM0403" }, Ids(found));
    }

    [Fact]
    public void A_global_proc_declared_twice_reports_the_pair()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/proc/g()\n\treturn 1\n\n/proc/g()\n\treturn 2\n");

        Assert.Equal(new[] { "DM0403", "DM0403" }, Ids(found));
    }

    /// <summary>Needs the seeded tree: `Move` exists only as a builtin on /atom/movable.</summary>
    [Fact]
    public void Redeclaring_a_builtin_proc_conflicts()
    {
        ObjectTree tree = new();
        Builtins.Seed(tree);

        ParseResult parse = DeclarationParser.Parse(
            Lexer.Lex(SourceText.From("/mob/proc/Move()\n\treturn 1\n")));
        TypeTreeBuilder.AddFile(tree, "a.dm", parse);

        IReadOnlyList<Diagnostic> found = Binder.Bind(tree, parse.Root, "a.dm");

        Diagnostic reported = Assert.Single(found);
        Assert.Equal("DM0403", reported.Id);
        Assert.Contains("built-in proc", reported.Message);
    }

    /// <summary>Would fail by inventing: an override carries no marker and is the ordinary case.</summary>
    [Fact]
    public void An_override_is_not_a_duplicate()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing\n\tproc/f()\n\t\treturn 1\n\n/datum/thing/sub\n\tf()\n\t\treturn 2\n");

        Assert.Empty(found);
    }

    /// <summary>Would fail by inventing: dm.exe accepts a var and a proc sharing a name (dup7).</summary>
    [Fact]
    public void A_var_and_a_proc_may_share_a_name()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing\n\tvar/f = 1\n\tproc/f()\n\t\treturn 2\n");

        Assert.Empty(found);
    }

    /// <summary>
    /// Cross-file: the file holding the LATER declaration reports the duplicate; the earlier
    /// file's "previous definition" line is the documented miss.
    /// </summary>
    [Fact]
    public void A_cross_file_redeclaration_reports_the_duplicate_half()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing\n\tproc/f()\n\t\treturn 1\n",
            "/datum/thing/sub\n\tproc/f()\n\t\treturn 2\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Contains("duplicate definition", reported.Message);
    }

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
    /// An expression-position path literal that names no type is dm.exe's eager
    /// "undefined type path". Would fail by reporting nothing.
    /// </summary>
    [Fact]
    public void An_expression_path_to_no_type_is_reported()
    {
        IReadOnlyList<Diagnostic> found = Bind("/obj/item\n\n/proc/f()\n\treturn /obj/nothing\n");

        Assert.Equal(new[] { "DM0402" }, Ids(found));
    }

    /// <summary>
    /// A DECLARED type is resolved at the use site, not the declaration — `var/clothing/slot`
    /// with no /clothing compiles clean until touched (§8). Would fail by inventing.
    /// </summary>
    [Fact]
    public void A_declared_type_is_not_checked_by_the_path_rule()
    {
        IReadOnlyList<Diagnostic> found = Bind("/mob\n\tvar\n\t\tclothing/slot\n");

        Assert.Empty(found);
    }

    /// <summary>
    /// `/obj/trap/get` names the verb `get` on /obj/trap with no `verb` marker segment — mlaas
    /// writes `verbs += /obj/small/trap/get`. Would fail by inventing.
    /// </summary>
    [Fact]
    public void A_path_naming_a_proc_through_its_type_is_silent()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/trap\n\tverb/get()\n\t\treturn\n\n/proc/f()\n\treturn /obj/trap/get\n");

        Assert.Empty(found);
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

    // -- DM Reference links -------------------------------------------------

    /// <summary>
    /// A builtin the reference documents carries a link; one it does not carries none. The second
    /// half is the point: 190 builtins come from stddef.dm and the verified-members table with no
    /// section to link to, and 25 more were scraped from an anchor whose shape a reader cannot
    /// reconstruct, so a link for them would open the index instead of the symbol.
    /// </summary>
    [Fact]
    public void A_documented_builtin_carries_a_reference_link()
    {
        ObjectTree tree = new();
        Builtins.Seed(tree);

        VarSymbol loc = tree.ResolveVar(tree.Find("/atom")!, "loc")!;
        ProcSymbol move = tree.ResolveProc(tree.Find("/atom/movable")!, "Move")!;

        Assert.True(loc.HasReference);
        Assert.True(move.HasReference);

        // From stddef.dm - real, and absent from the reference.
        Assert.False(tree.ResolveVar(tree.Find("/")!, "world")!.HasReference);
    }

    // -- DM0302: a member reached through `new` -----------------------------

    /// <summary>
    /// <c>new /path(...)</c> constructs exactly that type, so a missing member is a certain
    /// runtime error — verified: "undefined variable /mob/test/var/elsewhere". dm.exe accepts it
    /// because it holds no type for the expression, so this is a WARNING on code that compiles,
    /// and a deliberate divergence rather than an invented diagnostic.
    /// </summary>
    [Fact]
    public void A_missing_member_on_a_new_expression_warns()
    {
        IReadOnlyList<Diagnostic> diagnostics = Bind(
            "/mob/test\n\tvar/hp = 7\n/datum/other\n\tvar/elsewhere = 99\n"
            + "/proc/f()\n\treturn new /mob/test(1).elsewhere\n");

        Diagnostic found = Assert.Single(diagnostics, d => d.Id == "DM0302");

        Assert.Equal(DiagnosticSeverity.Warning, found.Severity);
        Assert.True(DeliberateDivergences.Contains("DM0302"));
    }

    /// <summary>The control: a member that IS on the constructed type says nothing.</summary>
    [Fact]
    public void A_real_member_on_a_new_expression_is_silent()
    {
        IReadOnlyList<Diagnostic> diagnostics = Bind(
            "/mob/test\n\tvar/hp = 7\n/datum/other\n\tvar/elsewhere = 99\n"
            + "/proc/f()\n\treturn new /mob/test(1).hp\n");

        Assert.DoesNotContain(diagnostics, d => d.Id == "DM0302");
    }

    /// <summary>
    /// A call result is genuinely unknowable, so it must NOT warn — that is the boundary between
    /// this check and the degrade-to-<c>:</c> case dm.exe is right to leave alone.
    /// </summary>
    [Fact]
    public void A_call_result_receiver_does_not_warn()
    {
        IReadOnlyList<Diagnostic> diagnostics = Bind(
            "/mob/test\n\tvar/hp = 7\n/datum/other\n\tvar/elsewhere = 99\n"
            + "/proc/mk()\n\treturn new /mob/test\n/proc/f()\n\treturn mk().elsewhere\n");

        Assert.DoesNotContain(diagnostics, d => d.Id == "DM0302");
    }
}
