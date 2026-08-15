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

    /// <summary>
    /// <c>usr</c> is always a <c>/mob</c> — compiler-verified, and <c>world.mob</c> does not
    /// retype it — so dm.exe checks members through it and, since 0.28, so do we. The first
    /// assert failing is the missed check returning; the second failing is inventing on every
    /// game that touches <c>usr</c>.
    /// </summary>
    [Fact]
    public void A_member_through_usr_is_checked()
    {
        // /mob is declared in the source because this harness's tree carries no builtins.
        IReadOnlyList<Diagnostic> found = Bind(
            "/mob\n\tvar/hp = 1\n/proc/f()\n\treturn usr.hp + usr.nonexistent_xyz\n");

        Assert.Contains(found, d => d.Id == "DM0400" && d.Message.Contains("nonexistent_xyz"));
        Assert.DoesNotContain(found, d => d.Message.Contains("hp"));
    }

    /// <summary>
    /// A bare name resolving to a TYPED member of the enclosing type carries that written type —
    /// mlaas's <c>clone.health</c> through <c>var/mob/pc/clone</c>, which sat unchecked,
    /// unindexed and uncertain-to-rename until 0.28.
    /// </summary>
    [Fact]
    public void A_member_receiver_reached_by_bare_name_is_checked()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/mob\n\tvar/hp = 1\n"
            + "/obj/pill\n\tvar/mob/owner\n\tproc/show()\n\t\treturn owner.hp + owner.nonexistent_xyz\n");

        Assert.Contains(found, d => d.Id == "DM0400" && d.Message.Contains("nonexistent_xyz"));
        Assert.DoesNotContain(found, d => d.Message.Contains("hp"));
    }

    /// <summary>
    /// An untyped LOCAL shadows a typed member of the same name, so the resolution must stop at
    /// the local — falling through would check against a type the receiver never had. The local
    /// being untyped, the access now reports dm.exe's own rejection rather than the member's
    /// answer: the shadow decides WHICH error, not whether.
    /// </summary>
    [Fact]
    public void An_untyped_local_shadowing_a_member_stops_the_check()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/mob\n\tvar/hp = 1\n"
            + "/obj/pill\n\tvar/mob/owner\n\tproc/show(x)\n\t\tvar/owner = x\n\t\treturn owner.anything_at_all\n");

        Assert.Contains(found, d => d.Message == "owner.anything_at_all: undefined var");
        Assert.DoesNotContain(found, d => d.Message.Contains("undefined var on"));
    }

    /// <summary>A typed root GLOBAL as a receiver is checked through its written type.</summary>
    [Fact]
    public void A_global_receiver_is_checked()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "var/mob/keeper\n/mob\n\tvar/hp = 1\n/proc/f()\n\treturn keeper.hp + keeper.nonexistent_xyz\n");

        Assert.Contains(found, d => d.Id == "DM0400" && d.Message.Contains("nonexistent_xyz"));
    }

    /// <summary>
    /// Binds the last file against a tree built from all of them, as a real build does — builtins
    /// seeded, because a bare tree resolves `list()` and `nameof()` to nothing and the
    /// undefined-proc check then measures the harness rather than the code.
    /// </summary>
    private static IReadOnlyList<Diagnostic> Bind(params string[] files)
    {
        List<(string, ParseResult)> parsed = new();

        for (int i = 0; i < files.Length; i++)
            parsed.Add(($"file{i}.dm", DeclarationParser.Parse(Lexer.Lex(SourceText.From(files[i])))));

        ObjectTree tree = new();
        Builtins.Seed(tree);

        foreach ((string file, ParseResult parse) in parsed)
            TypeTreeBuilder.AddFile(tree, file, parse);

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
    /// Cross-file: each file reports its own half of the pair. The file holding the LATER
    /// declaration draws the duplicate; binding the ancestor's file draws the "previous
    /// definition" — the check's one documented miss until 2026-08-13, closed by the tree's
    /// redeclaration index instead of a descendant scan per bind.
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

    [Fact]
    public void A_cross_file_redeclaration_reports_the_previous_half_in_the_ancestors_file()
    {
        // Bind() binds the LAST file, so the ancestor's declaration is the one being bound and
        // the descendant's duplicate sits in the other file.
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing/sub\n\tproc/f()\n\t\treturn 2\n",
            "/datum/thing\n\tproc/f()\n\t\treturn 1\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Contains("previous definition", reported.Message);
    }

    /// <summary>dm.exe reports one previous line however many descendants duplicate (probed).</summary>
    [Fact]
    public void Two_duplicating_descendants_draw_one_previous_line()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing/one\n\tproc/f()\n\t\treturn 2\n/datum/thing/two\n\tproc/f()\n\t\treturn 3\n",
            "/datum/thing\n\tproc/f()\n\t\treturn 1\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Contains("previous definition", reported.Message);
    }

    /// <summary>The var half pairs across files the same way (probe p2, 2026-08-13).</summary>
    [Fact]
    public void A_var_redeclared_on_a_subtype_reports_the_previous_half_in_the_ancestors_file()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/thing/sub\n\tvar/x\n",
            "/datum/thing\n\tvar/x\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Contains("previous definition", reported.Message);
    }

    // -- bare identifiers (DM0400's undefined-var half) -----------------------

    [Fact]
    public void A_bare_name_resolving_nowhere_is_an_undefined_var()
    {
        IReadOnlyList<Diagnostic> found = Bind("/proc/f()\n\treturn missing_thing\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Equal("DM0400", reported.Id);
        Assert.Equal("missing_thing: undefined var", reported.Message);
    }

    /// <summary>Value position is VARS-ONLY: &amp;f and initial(p) both error in dm.exe.</summary>
    [Fact]
    public void A_proc_name_does_not_satisfy_value_position()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/datum/d\n\tproc/p()\n\t\treturn 1\n\tproc/q()\n\t\treturn p\n");

        Assert.Contains(found, d => d.Id == "DM0400" && d.Message == "p: undefined var");
    }

    [Fact]
    public void Members_globals_and_proc_scope_names_all_resolve()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "var/g = 1\n\n/mob/test\n\tvar/m = 2\n\tproc/f(a)\n\t\treturn g + m + a + args.len + usr\n");

        Assert.DoesNotContain(found, d => d.Id == "DM0400");
    }

    /// <summary>`list(k = 1)` stores the STRING key "k"; the variable is never read (probed).</summary>
    [Fact]
    public void An_assoc_key_identifier_is_string_sugar_not_a_read()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/proc/f()\n\tvar/list/L = list(missing_key = 1)\n\treturn L\n");

        Assert.DoesNotContain(found, d => d.Id == "DM0400");
    }

    /// <summary>A modified-type entry's target names a member of the constructed type.</summary>
    [Fact]
    public void A_modified_type_entry_target_is_not_a_scope_read()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/thing\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/thing/T = new /obj/thing{hp = 2}\n\treturn T\n");

        Assert.DoesNotContain(found, d => d.Id == "DM0400");
    }

    /// <summary>A lone identifier line is a LABEL — dm.exe's colon is optional (probed).</summary>
    [Fact]
    public void A_bare_label_line_is_not_a_read()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/proc/f()\n\tgoto fin\n\tfin\n\treturn 1\n");

        Assert.Empty(found);
    }

    /// <summary>`var/a = 1, b = 2, c = 3` — the comma tail stays flat, so c is declared too.</summary>
    [Fact]
    public void Every_comma_sibling_is_declared()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/proc/f()\n\tvar/a = 1, b = 2, c = 3\n\tvar x, y, z\n\tx = 1\n\ty = 2\n\tz = 3\n"
            + "\treturn a + b + c + x + y + z\n");

        Assert.Empty(found);
    }

    /// <summary>A typed local var BLOCK declares its children as that type (mlaas's shape).</summary>
    [Fact]
    public void A_typed_local_var_block_declares_its_children()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/cl\n\tvar/worn = 0\n\n/proc/f()\n\tvar/obj/cl\n\t\tfirst\n\t\tsecond\n"
            + "\tfirst = new\n\tsecond = new\n\treturn first.worn + second.worn\n");

        Assert.Empty(found);
    }

    /// <summary>The `set` block form's children are settings, not statements (madridspy).</summary>
    [Fact]
    public void A_set_block_holds_settings_not_reads()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/mob/verb/v()\n\tset\n\t\thidden = 1\n\t\tinstant = 1\n\treturn 1\n");

        Assert.Empty(found);
    }

    /// <summary>`var{a = 1; b = 2}` declares locals, not assignment statements (warklan).</summary>
    [Fact]
    public void A_var_brace_group_declares_its_entries()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/proc/f()\n\tvar{bg1 = 3; bg2 = 4}\n\treturn bg1 + bg2\n");

        Assert.Empty(found);
    }

    // -- set names, usr in initializers, dotted receivers, undefined procs ----
    // The 2026-08-13 evening batch, each rule probed before the code.

    [Fact]
    public void An_unknown_set_name_is_an_undefined_var()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/mob/verb/v()\n\tset bogus_setting = 1\n\treturn 1\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Equal("bogus_setting: undefined var", reported.Message);
    }

    /// <summary>All ten names compile in verbs AND procs (probed); none may report.</summary>
    [Fact]
    public void The_set_vocabulary_stays_silent()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/proc/t()\n\tset name = \"n\"\n\tset desc = \"d\"\n\tset category = \"c\"\n"
            + "\tset hidden = 1\n\tset instant = 1\n\tset invisibility = 1\n\tset popup_menu = 0\n"
            + "\tset background = 1\n\tset waitfor = 0\n\treturn 1\n");

        Assert.Empty(found);
    }

    /// <summary>`usr` errors in a type-level initializer (probed in three spellings) and not in a body.</summary>
    [Fact]
    public void Usr_is_rejected_in_a_type_level_initializer()
    {
        Assert.Contains(
            Bind("/datum/d\n\tvar/v = usr\n"),
            d => d.Message == "usr: undefined var");

        Assert.Empty(Bind("/proc/t()\n\treturn usr\n"));
    }

    /// <summary>
    /// A bare receiver that resolves as NO var is dm.exe's error with the dotted text as the
    /// symbol (errors/bare_type_receiver); an untyped LOCAL receiver stays the deliberate miss.
    /// </summary>
    [Fact]
    public void A_bare_receiver_resolving_nowhere_reports_the_dotted_form()
    {
        Assert.Contains(
            Bind("/mob\n\tvar/hp = 1\n\n/proc/t()\n\treturn mob.hp\n"),
            d => d.Message == "mob.hp: undefined var");

        // The untyped-local control moved: dm.exe rejects that too, with the same dotted form
        // — see An_untyped_receiver_rejects_every_member.
        Assert.Contains(
            Bind("/obj/item\n\tvar/hp = 1\n\n/proc/t()\n\tvar/x = new /obj/item\n\treturn x.hp\n"),
            d => d.Message == "x.hp: undefined var");
    }

    [Fact]
    public void A_bare_call_no_proc_satisfies_is_an_undefined_proc()
    {
        IReadOnlyList<Diagnostic> found = Bind("/proc/t()\n\treturn no_such_global_xyz()\n");

        Diagnostic reported = Assert.Single(found);
        Assert.Equal("DM0401", reported.Id);
        Assert.Equal("no_such_global_xyz: undefined proc", reported.Message);
    }

    /// <summary>Enclosing-chain, global, and builtin procs all satisfy a call.</summary>
    [Fact]
    public void Resolving_calls_stay_silent()
    {
        Assert.Empty(Bind(
            "/proc/helper()\n\treturn 1\n\n/mob/test\n\tproc/own()\n\t\treturn 1\n"
            + "\tproc/t(x)\n\t\treturn own() + helper() + length(x)\n"));
    }

    /// <summary>
    /// A var does not satisfy a call — and dm.exe still counts the local unused (probed: both
    /// diagnostics on one probe). But the NAME can resolve as a proc past the shadowing local:
    /// mlaas calls the builtin length() with a parameter of that name in scope.
    /// </summary>
    [Fact]
    public void A_called_local_is_an_undefined_proc_and_stays_unused()
    {
        IReadOnlyList<Diagnostic> found = Bind("/proc/t()\n\tvar/x = 5\n\tx()\n\treturn 1\n");

        Assert.Contains(found, d => d.Id == "DM0401" && d.Message == "x: undefined proc");
        Assert.Contains(found, d => d.Id == "unused_var");

        Assert.Empty(Bind("/proc/limit(message, length)\n\treturn length(message)\n"));
    }

    /// <summary>`new the_type(usr)` reads the var and passes constructor args — not a call.</summary>
    [Fact]
    public void New_through_a_var_is_a_read_not_a_call()
    {
        Assert.Empty(Bind(
            "/obj/thing\n/proc/t()\n\tvar/the_type = /obj/thing\n\treturn new the_type (null)\n"));
    }

    // -- what it must catch -------------------------------------------------

    [Fact]
    public void A_member_no_type_declares_is_an_undefined_var()
    {
        // The unused_var rides along on purpose: dm.exe counts a member access as a use of its
        // receiver only when the access COMPILES, so the failing read leaves I unused — the
        // errors/semantic goldens pair every such error with the warning.
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.nowhere_at_all\n");

        Assert.Equal(new[] { "DM0400", "unused_var" }, Ids(found).OrderBy(i => i));
    }

    [Fact]
    public void A_called_member_no_type_declares_is_an_undefined_proc()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/obj/item/I = new\n\treturn I.nowhere_at_all()\n");

        Assert.Equal(new[] { "DM0401", "unused_var" }, Ids(found).OrderBy(i => i));
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

        Assert.Equal(new[] { "DM0400", "unused_var" }, Ids(found).OrderBy(i => i));
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
    /// dm.exe rejects every member of an untyped local, including the right one, because it does
    /// no local inference — and it STILL counts the local unused, since the erroring access is
    /// not a read (both probed 2026-08-14). This was the deliberate miss until the declaration
    /// forms were certain; the certainty guard that remains is builtins with no recorded type.
    /// </summary>
    [Fact]
    public void An_untyped_receiver_rejects_every_member()
    {
        IReadOnlyList<Diagnostic> found = Bind(
            "/obj/item\n\tvar/hp = 1\n\n/proc/f()\n\tvar/x = new /obj/item\n\treturn x.hp\n");

        Assert.Contains(found, d => d.Id == "DM0400" && d.Message == "x.hp: undefined var");
        Assert.Contains(found, d => d.Id == "unused_var");
    }

    /// <summary>The invoked twin, and untyped members and globals reject the same way (probed).</summary>
    [Fact]
    public void The_untyped_rejection_covers_calls_members_and_globals()
    {
        Assert.Contains(
            Bind("/proc/f(a)\n\treturn a.g()\n"),
            d => d.Id == "DM0401" && d.Message == "a.g: undefined proc");

        Assert.Contains(
            Bind("var/gv\n/proc/f()\n\treturn gv.hp\n"),
            d => d.Id == "DM0400" && d.Message == "gv.hp: undefined var");

        Assert.Contains(
            Bind("/mob/test\n\tvar/thing\n\tproc/p()\n\t\treturn thing.hp\n"),
            d => d.Id == "DM0400" && d.Message == "thing.hp: undefined var");
    }

    /// <summary>
    /// A builtin var with no recorded type is OUR table's gap — five are deliberately untyped
    /// because no probe discriminates them — so it must stay silent, not report.
    /// </summary>
    [Fact]
    public void An_untyped_builtin_var_stays_silent()
        => Assert.DoesNotContain(
            Bind("/mob/test\n\tproc/p()\n\t\treturn appearance.icon\n"),
            d => d.Id is "DM0400" or "DM0401");

    /// <summary>
    /// An untyped OVERRIDE on a subtype must not hide the typed declaration above it — the
    /// declared type is the chain's first non-null, not the first symbol's. tgstation's bots
    /// override `ai_controller` per type while /atom declares it `/datum/ai_controller`, and the
    /// override-shadow read invented 319 there on the check's first run.
    /// </summary>
    [Fact]
    public void An_untyped_override_does_not_hide_the_declared_type()
        => Assert.Empty(Bind(
            "/datum/brain\n\tvar/mode = 1\n"
            + "/mob/base\n\tvar/datum/brain/mind_thing\n"
            + "/mob/base/bot\n\tmind_thing = null\n\tproc/p()\n\t\treturn mind_thing.mode\n"));

    /// <summary>
    /// Brackets and a `var/list` block header TYPE vars — mlaas's `players[0].Add()`, madridspy's
    /// market block, warklan's ban lists all compile through them — so the member check runs
    /// against /list rather than rejecting everything.
    /// </summary>
    [Fact]
    public void Bracket_and_header_vars_are_lists()
    {
        Assert.Empty(Bind(
            "/mob/test\n\tvar/bl[0]\n\tvar/list\n\t\thl\n\tproc/p()\n\t\tvar/lb[0]\n"
            + "\t\tlb.Add(1)\n\t\treturn bl.len + hl.len\n"));

        Assert.Contains(
            Bind("/proc/t()\n\tvar/lb[0]\n\tlb.Add(1)\n\treturn lb.bogus_xyz\n"),
            d => d.Id == "DM0400" && d.Message.Contains("bogus_xyz"));
    }

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

        Assert.Equal(new[] { "DM0400", "unused_var" }, Ids(found).OrderBy(i => i));
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
