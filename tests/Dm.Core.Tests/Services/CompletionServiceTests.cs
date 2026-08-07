using System;
using System.IO;
using System.Linq;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

public class CompletionServiceTests
{
    /// <summary>
    /// Completes at the caret marked <c>|</c>, which is removed before parsing.
    /// </summary>
    private static CompletionResult Complete(string sourceWithCaret, bool withBuiltins = false)
    {
        int caret = sourceWithCaret.IndexOf('|');
        Assert.True(caret >= 0, "the source must mark the caret with |");

        string source = sourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = withBuiltins ? Builtins.CreateTree() : new ObjectTree();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        return CompletionService.CompleteAt(tree, document, position.Line, position.Character);
    }

    private static string[] Names(CompletionResult result) => result.Items.Select(i => i.Name).ToArray();

    // -- the acceptance target ---------------------------------------------

    /// <summary>
    /// PLAN §1's target: <c>mob.</c> lists <c>/mob</c>'s members including inherited and builtin
    /// ones. None of <c>loc</c> or <c>Move</c> appears in any source file.
    /// </summary>
    [Fact]
    public void Mob_dot_lists_inherited_and_builtin_members()
    {
        CompletionResult result = Complete("/mob\n\tvar/hp = 1\n/proc/f()\n\tmob.|\n", withBuiltins: true);

        Assert.Equal(CompletionContext.Member, result.Context);

        string[] names = Names(result);
        Assert.Contains("hp", names);        // the project's own
        Assert.Contains("Login", names);     // builtin on /mob
        Assert.Contains("loc", names);       // inherited from /atom
        Assert.Contains("Move", names);      // inherited from /atom/movable
    }

    /// <summary>The other half of the target: a typed local resolves without any inference.</summary>
    [Fact]
    public void A_typed_local_resolves_to_its_declared_type()
    {
        CompletionResult result = Complete(
            "/mob/test\n\tvar/special = 1\n/proc/f()\n\tvar/mob/test/t = new\n\tt.|\n");

        Assert.Contains("special", Names(result));
    }

    [Fact]
    public void A_typed_parameter_resolves_too()
    {
        CompletionResult result = Complete(
            "/mob/test\n\tvar/special = 1\n/proc/f(mob/test/target)\n\ttarget.|\n");

        Assert.Contains("special", Names(result));
    }

    // -- `.` versus `:` -----------------------------------------------------

    /// <summary>
    /// <c>.</c> checks the declared type, so a member that exists only on a subtype is not offered.
    /// <c>:</c> widens the check to the subtype tree and does offer it — but neither is unchecked,
    /// so an unrelated type's member appears in neither list.
    /// </summary>
    [Fact]
    public void Dot_offers_the_declared_type_only()
    {
        CompletionResult result = Complete(
            "/mob/test\n\tvar/base = 1\n/mob/test/special\n\tvar/only_here = 2\n"
            + "/datum/unrelated\n\tvar/elsewhere = 3\n/proc/f()\n\tvar/mob/test/t = new\n\tt.|\n");

        Assert.Equal(CompletionContext.Member, result.Context);

        string[] names = Names(result);
        Assert.Contains("base", names);
        Assert.DoesNotContain("only_here", names);
        Assert.DoesNotContain("elsewhere", names);
    }

    [Fact]
    public void Colon_widens_to_subtypes_but_not_to_everything()
    {
        CompletionResult result = Complete(
            "/mob/test\n\tvar/base = 1\n/mob/test/special\n\tvar/only_here = 2\n"
            + "/datum/unrelated\n\tvar/elsewhere = 3\n/proc/f()\n\tvar/mob/test/t = new\n\tt:|\n");

        Assert.Equal(CompletionContext.SubtypeMember, result.Context);

        string[] names = Names(result);
        Assert.Contains("base", names);
        Assert.Contains("only_here", names);

        // Still a check, just a wider one.
        Assert.DoesNotContain("elsewhere", names);
    }

    // -- scope --------------------------------------------------------------

    [Fact]
    public void A_bare_identifier_offers_locals_parameters_and_src_members()
    {
        CompletionResult result = Complete(
            "/mob\n\tvar/hp = 1\n\tproc/f(damage)\n\t\tvar/local = 2\n\t\t|\n");

        Assert.Equal(CompletionContext.Identifier, result.Context);

        string[] names = Names(result);
        Assert.Contains("local", names);     // local
        Assert.Contains("damage", names);    // parameter
        Assert.Contains("hp", names);        // src member
    }

    /// <summary>A local declared after the cursor is not in scope yet.</summary>
    [Fact]
    public void A_later_local_is_not_offered()
    {
        CompletionResult result = Complete(
            "/mob/proc/f()\n\tvar/early = 1\n\t|\n\tvar/late = 2\n");

        string[] names = Names(result);
        Assert.Contains("early", names);
        Assert.DoesNotContain("late", names);
    }

    [Fact]
    public void Src_resolves_to_the_enclosing_type()
    {
        CompletionResult result = Complete("/mob\n\tvar/hp = 1\n\tproc/f()\n\t\tsrc.|\n");

        Assert.Contains("hp", Names(result));
    }

    /// <summary>
    /// Globals are offered for a bare identifier but never after <c>.</c> — <c>istype(x)</c> is a
    /// call, while <c>mob.istype()</c> is not valid DM.
    /// </summary>
    [Fact]
    public void Globals_are_offered_bare_but_not_after_a_dot()
    {
        Assert.Contains("istype", Names(Complete("/proc/f()\n\t|\n", withBuiltins: true)));
        Assert.DoesNotContain("istype", Names(Complete("/mob\n/proc/f()\n\tmob.|\n", withBuiltins: true)));
    }

    // -- paths and partial words -------------------------------------------

    [Fact]
    public void A_written_path_resolves_to_its_type()
    {
        CompletionResult result = Complete("/obj/item\n\tvar/weight = 1\n/proc/f()\n\t/obj/item.|\n");

        Assert.Contains("weight", Names(result));
    }

    /// <summary>A partly typed word is not the trigger; what precedes it is.</summary>
    [Fact]
    public void A_partial_word_still_completes_against_the_receiver()
    {
        CompletionResult result = Complete("/mob\n\tvar/health = 1\n/proc/f()\n\tmob.he|\n");

        Assert.Equal(CompletionContext.Member, result.Context);
        Assert.Contains("health", Names(result));
    }

    [Fact]
    public void An_unresolvable_receiver_offers_nothing_rather_than_everything()
    {
        CompletionResult result = Complete("/proc/f()\n\tvar/untyped = 1\n\tuntyped.|\n");

        Assert.Equal(CompletionContext.Member, result.Context);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void Builtin_items_are_marked_as_such()
    {
        CompletionResult result = Complete("/mob\n\tvar/hp = 1\n/proc/f()\n\tmob.|\n", withBuiltins: true);

        Assert.True(result.Items.Single(i => i.Name == "Login").IsBuiltin);
        Assert.False(result.Items.Single(i => i.Name == "hp").IsBuiltin);
    }

    /// <summary>
    /// The same path the ABI takes: a real workspace, a tree built from the include graph, and a
    /// position given as line/character rather than an offset.
    /// </summary>
    [Fact]
    public void Completion_works_through_a_workspace()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dm_completion_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "p.dme"), "#include \"p.dm\"\n");

            // CRLF on purpose: what an editor on Windows writes, and what the C++ smoke test
            // produces through std::ofstream.
            File.WriteAllText(Path.Combine(dir, "p.dm"), "/mob/test\r\n\tvar/base_var = 1\r\n/proc/f()\r\n\tvar/mob/test/t = new\r\n\tt.\r\n");

            using Workspace workspace = Workspace.Open(Path.Combine(dir, "p.dme"));
            Document document = workspace.GetDocument("p.dm");

            CompletionResult result = CompletionService.CompleteAt(
                workspace.GetObjectTree(), document, 4, 3);

            Assert.Equal(CompletionContext.Member, result.Context);
            Assert.Contains("base_var", result.Items.Select(i => i.Name));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // -- inference ----------------------------------------------------------
    //
    // Everything below offers more than dm.exe accepts. The compiler has no local type inference:
    // `var/x = new /obj/item` then `x.hp` is "x.hp: undefined var", verified against 516.1666 and
    // recorded in PLAN.md §8. These are a deliberate editor affordance, not a compiler claim.

    [Fact]
    public void An_untyped_local_infers_from_new()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item\n\tx.|\n");

        Assert.Contains("hp", Names(result));
    }

    [Fact]
    public void Inference_through_new_survives_arguments_and_a_modified_type()
    {
        Assert.Contains("hp", Names(Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item(src)\n\tx.|\n")));

        Assert.Contains("hp", Names(Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item{hp = 2}\n\tx.|\n")));
    }

    /// <summary>A bare <c>new</c> names no type, so there is nothing to infer from it.</summary>
    [Fact]
    public void A_bare_new_infers_nothing()
    {
        CompletionResult result = Complete("/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new\n\tx.|\n");

        Assert.Empty(result.Items);
    }

    [Fact]
    public void An_untyped_local_infers_from_a_later_assignment()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x\n\tx = new /obj/item\n\tx.|\n");

        Assert.Contains("hp", Names(result));
    }

    /// <summary>
    /// The nearest assignment before the cursor wins, so a name pointed at a second type reports
    /// the second one rather than whatever it held first.
    /// </summary>
    [Fact]
    public void Reassignment_reports_the_most_recent_type()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/datum/other\n\tvar/elsewhere = 2\n"
            + "/proc/f()\n\tvar/x = new /obj/item\n\tx = new /datum/other\n\tx.|\n");

        string[] names = Names(result);
        Assert.Contains("elsewhere", names);
        Assert.DoesNotContain("hp", names);
    }

    /// <summary>An assignment below the cursor has not happened yet at the position being asked about.</summary>
    [Fact]
    public void An_assignment_after_the_cursor_is_ignored()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x\n\tx.|\n\tx = new /obj/item\n");

        Assert.Empty(result.Items);
    }

    [Fact]
    public void An_untyped_local_infers_from_another_typed_local()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/obj/item/a = new\n\tvar/x = a\n\tx.|\n");

        Assert.Contains("hp", Names(result));
    }

    [Fact]
    public void A_parameter_infers_from_its_as_clause()
    {
        CompletionResult result = Complete(
            "/proc/f(M as mob)\n\tM.|\n", withBuiltins: true);

        Assert.Contains("Login", Names(result));
    }

    /// <summary>
    /// <c>as text</c> describes a value rather than an object, so there is no type to resolve
    /// members against and the list stays empty rather than guessing.
    /// </summary>
    [Fact]
    public void A_value_shaped_as_clause_infers_nothing()
    {
        CompletionResult result = Complete("/proc/f(T as text)\n\tT.|\n", withBuiltins: true);

        Assert.Empty(result.Items);
    }

    /// <summary>A written type always wins; inference only fills a slot the author left empty.</summary>
    [Fact]
    public void A_declared_type_beats_a_conflicting_initialiser()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/datum/other\n\tvar/elsewhere = 2\n"
            + "/proc/f()\n\tvar/obj/item/x = new /datum/other\n\tx.|\n");

        string[] names = Names(result);
        Assert.Contains("hp", names);
        Assert.DoesNotContain("elsewhere", names);
    }

    /// <summary>A name initialised from itself must not send the resolver into a loop.</summary>
    [Fact]
    public void Self_reference_terminates()
    {
        CompletionResult result = Complete("/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = x\n\tx.|\n");

        Assert.Empty(result.Items);
    }

    // -- documentation --------------------------------------------------------

    /// <summary>
    /// A member carries the <c>///</c> comment above its declaration, so a popup can show it.
    /// </summary>
    /// <remarks>
    /// Only when the caller supplies a file reader: a member's documentation lives where it was
    /// declared, which is rarely the file being completed in.
    /// </remarks>
    [Fact]
    public void A_member_carries_its_doc_comment()
    {
        const string WithCaret =
            "/mob/guy\n\t/// Restores health.\n\t/// Safe on a dead mob.\n\tproc/heal(amount)\n"
            + "\t\treturn amount\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n";

        int caret = WithCaret.IndexOf('|');
        string source = WithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);

        CompletionResult result = CompletionService.CompleteAt(
            tree, document, position.Line, position.Character, null, _ => document.Text);

        CompletionItem heal = result.Items.First(i => i.Name == "heal");

        Assert.Equal("Restores health.\nSafe on a dead mob.", heal.Documentation);
    }

    /// <summary>Without a file reader the list still works, just undocumented.</summary>
    [Fact]
    public void Documentation_is_empty_when_no_reader_is_supplied()
    {
        CompletionResult result = Complete(
            "/mob/guy\n\t/// Restores health.\n\tproc/heal(amount)\n\t\treturn amount\n"
            + "/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n");

        CompletionItem heal = result.Items.First(i => i.Name == "heal");

        Assert.Empty(heal.Documentation);
        Assert.Equal("heal", heal.Name);
    }

    // -- macros --------------------------------------------------------------

    /// <summary>
    /// Macros are in scope for a bare identifier and nowhere else.
    /// </summary>
    /// <remarks>
    /// They cannot come from the object tree: the preprocessor has removed them long before the
    /// parser runs, so the workspace carries the names across separately. A macro is not a member of
    /// anything, which is why nothing after <c>.</c> or <c>:</c> may offer one.
    /// </remarks>
    [Fact]
    public void Macros_are_offered_for_a_bare_identifier()
    {
        string[] macros = { "MAX_HEALTH", "HEAL" };

        CompletionResult result = CompleteWithMacros(
            "/mob/guy\n\tvar/health = 1\n\tproc/f()\n\t\t|\n", macros);

        Assert.Equal(CompletionContext.Identifier, result.Context);

        string[] names = Names(result);
        Assert.Contains("MAX_HEALTH", names);
        Assert.Contains("HEAL", names);
        Assert.Contains("health", names);

        Assert.Equal(
            CompletionKind.Macro,
            result.Items.First(i => i.Name == "MAX_HEALTH").Kind);
    }

    [Fact]
    public void Macros_are_not_offered_after_a_member_operator()
    {
        string[] macros = { "MAX_HEALTH" };

        CompletionResult dot = CompleteWithMacros(
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n", macros);

        Assert.Equal(CompletionContext.Member, dot.Context);
        Assert.Contains("health", Names(dot));
        Assert.DoesNotContain("MAX_HEALTH", Names(dot));

        CompletionResult colon = CompleteWithMacros(
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg:|\n", macros);

        Assert.DoesNotContain("MAX_HEALTH", Names(colon));
    }

    private static CompletionResult CompleteWithMacros(string sourceWithCaret, string[] macros)
    {
        int caret = sourceWithCaret.IndexOf('|');
        string source = sourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        return CompletionService.CompleteAt(
            tree, document, position.Line, position.Character, macros);
    }

    /// <summary>
    /// A call result still resolves to nothing. That is the one place DM itself gives up, letting
    /// <c>.</c> behave like <c>:</c>, so there is no single type to offer.
    /// </summary>
    [Fact]
    public void A_call_result_still_infers_nothing()
    {
        CompletionResult result = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/mk()\n\treturn new /obj/item\n/proc/f()\n\tvar/x = mk()\n\tx.|\n");

        Assert.Empty(result.Items);
    }

    /// <summary>
    /// A bare leading <c>.</c> is the return-value variable, not member access. The distinct
    /// context lets a client show nothing without guessing from the trigger character.
    /// </summary>
    [Fact]
    public void A_bare_leading_dot_is_the_return_value_context()
    {
        CompletionResult atLineStart = Complete("/mob/guy\n\tvar/health = 1\n\tproc/f()\n\t\t.|\n");
        Assert.Equal(CompletionContext.ReturnValue, atLineStart.Context);
        Assert.Empty(atLineStart.Items);

        CompletionResult afterReturn = Complete("/mob/guy\n\tproc/f()\n\t\treturn .|\n");
        Assert.Equal(CompletionContext.ReturnValue, afterReturn.Context);

        // A dot after a value is member access, exactly as before.
        CompletionResult member = Complete(
            "/mob/guy\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n");
        Assert.Equal(CompletionContext.Member, member.Context);
    }

    /// <summary>
    /// Items offered through an inferred receiver say so — the flag replaces every client's guess
    /// about which items ride on inference <c>dm.exe</c> does not do.
    /// </summary>
    [Fact]
    public void An_inferred_receiver_marks_every_item_and_a_declared_one_marks_none()
    {
        CompletionResult inferred = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/x = new /obj/item\n\tx.|\n");
        Assert.NotEmpty(inferred.Items);
        Assert.All(inferred.Items, i => Assert.True(i.Inferred));

        CompletionResult declared = Complete(
            "/obj/item\n\tvar/hp = 1\n/proc/f()\n\tvar/obj/item/x = new\n\tx.|\n");
        Assert.NotEmpty(declared.Items);
        Assert.All(declared.Items, i => Assert.False(i.Inferred));
    }

    /// <summary>
    /// The lazy-resolve pair: the brief list carries no documentation, and resolving one item
    /// returns exactly that item's. The list is otherwise identical, so a client can switch to
    /// lazy resolve without losing anything else.
    /// </summary>
    [Fact]
    public void The_brief_list_carries_no_documentation_and_resolve_supplies_it()
    {
        const string SourceWithCaret =
            "/mob/guy\n\t/// How much damage it can take.\n\tvar/health = 1\n"
            + "\t/// Not this one.\n\tvar/other = 2\n"
            + "/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);
        SourceText? Reader(string _) => document.Text;

        // The full list documents everything it can.
        CompletionResult full = CompletionService.CompleteAt(
            tree, document, position.Line, position.Character, null, Reader);

        Assert.Equal(
            "How much damage it can take.",
            full.Items.Single(i => i.Name == "health").Documentation);

        // The brief list is the same items with the documentation left off.
        CompletionResult brief = CompletionService.CompleteBriefAt(
            tree, document, position.Line, position.Character);

        Assert.Equal(Names(full), Names(brief));
        Assert.All(brief.Items, i => Assert.Empty(i.Documentation));

        // Resolve answers for the one item asked about.
        Assert.Equal(
            "How much damage it can take.",
            CompletionService.ResolveDocumentation(
                tree, document, position.Line, position.Character, "health", null, Reader));

        Assert.Equal(
            "Not this one.",
            CompletionService.ResolveDocumentation(
                tree, document, position.Line, position.Character, "other", null, Reader));
    }

    /// <summary>A name the position does not offer resolves to nothing, not an error.</summary>
    [Fact]
    public void Resolving_an_unknown_item_is_empty()
    {
        const string SourceWithCaret =
            "/mob/guy\n\t/// Doc.\n\tvar/health = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);

        Assert.Empty(CompletionService.ResolveDocumentation(
            tree, document, position.Line, position.Character, "no_such_member",
            null, _ => document.Text));
    }

    /// <summary>
    /// Ranked by scope distance, nearest first — a local the user declared two lines up outranks a
    /// builtin nobody asked for. The query-driven ranking a picker uses cannot help here: a bare
    /// identifier position has no query string.
    /// </summary>
    [Fact]
    public void A_bare_identifier_is_ranked_by_scope_distance()
    {
        CompletionResult result = Complete(
            "/mob/guy\n\tvar/member_var = 1\n\tproc/f(param_var)\n\t\tvar/local_var = 1\n\t\t|\n",
            withBuiltins: true);

        string[] names = Names(result);

        int local = Array.IndexOf(names, "local_var");
        int param = Array.IndexOf(names, "param_var");
        int member = Array.IndexOf(names, "member_var");
        int builtin = Array.IndexOf(names, "loc");

        Assert.True(local >= 0 && param >= 0 && member >= 0 && builtin >= 0, "all four must be offered");

        Assert.True(local < param, "a local outranks a parameter");
        Assert.True(param < member, "a parameter outranks a member");
        Assert.True(member < builtin, "a declared member outranks a builtin");
    }

    /// <summary>
    /// A cap is off unless a caller asks for one, and it says so when it bites — a client that
    /// filters locally over a silently truncated list would miss what the user is typing toward.
    /// </summary>
    [Fact]
    public void A_limit_caps_the_list_and_reports_that_it_did()
    {
        const string SourceWithCaret =
            "/mob/guy\n\tvar/a = 1\n\tvar/b = 2\n\tvar/c = 3\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);

        CompletionResult uncapped = CompletionService.CompleteAt(
            tree, document, position.Line, position.Character, null, null);

        Assert.False(uncapped.Truncated);
        Assert.Equal(3, uncapped.Items.Count);

        CompletionResult capped = CompletionService.CompleteAt(
            tree, document, position.Line, position.Character, null, null,
            PositionEncoding.Utf16, default, limit: 2);

        Assert.True(capped.Truncated);
        Assert.Equal(2, capped.Items.Count);

        // The cap keeps the ranking's head, so what survives is the nearest, not an arbitrary slice.
        Assert.Equal(new[] { "a", "b" }, capped.Items.Select(i => i.Name).ToArray());
    }

    /// <summary>A limit the list does not reach is not a truncation.</summary>
    [Fact]
    public void A_limit_larger_than_the_list_reports_nothing_cut()
    {
        const string SourceWithCaret =
            "/mob/guy\n\tvar/a = 1\n/proc/f()\n\tvar/mob/guy/g = new\n\tg.|\n";

        int caret = SourceWithCaret.IndexOf('|');
        string source = SourceWithCaret.Remove(caret, 1);

        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        LinePosition position = document.Text.GetLinePosition(caret);

        CompletionResult result = CompletionService.CompleteAt(
            tree, document, position.Line, position.Character, null, null,
            PositionEncoding.Utf16, default, limit: 500);

        Assert.False(result.Truncated);
        Assert.Single(result.Items);
    }

    /// <summary>An <c>as</c> clause is an input filter, not a type — dm.exe checks nothing off it.</summary>
    [Fact]
    public void An_as_clause_receiver_is_inferred()
    {
        CompletionResult result = Complete(
            "/mob\n\tvar/hp = 1\n/proc/f(M as mob)\n\tM.|\n", withBuiltins: true);

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, i => Assert.True(i.Inferred));
    }

    // -- the item's own type and initialiser --------------------------------

    /// <summary>
    /// A member carries its OWN declared type and initialiser, so a client renders the list
    /// without re-parsing. The two vars are deliberately opposite — typed with no value, untyped
    /// with one — because a single var could pass with the pair swapped.
    /// </summary>
    [Fact]
    public void A_member_carries_its_declared_type_and_initial_value()
    {
        CompletionResult result = Complete(
            "/mob/test\n\tvar/fatigue = 6\n\tvar/mob/test/friend\n"
            + "/proc/f()\n\tvar/mob/test/t = new\n\tt.|\n");

        CompletionItem fatigue = Assert.Single(result.Items, i => i.Name == "fatigue");
        CompletionItem friend = Assert.Single(result.Items, i => i.Name == "friend");

        // `var/fatigue = 6` has NO declared type - DM has no `num` to name, and an initialiser
        // does not type a variable (PLAN 8). Empty is the honest answer, and the value is what a
        // reader actually wants there.
        Assert.Equal(string.Empty, fatigue.DeclaredType);
        Assert.Equal("6", fatigue.InitialValue);

        Assert.Equal("/mob/test", friend.DeclaredType);
        Assert.Equal(string.Empty, friend.InitialValue);
    }

    /// <summary>
    /// Locals and parameters carry the same two fields, from the statement rather than the object
    /// tree — and a parameter's <c>as</c> clause is NOT reported as a type, because dm.exe does not
    /// check members through it.
    /// </summary>
    [Fact]
    public void A_local_and_a_parameter_carry_their_type_and_value()
    {
        CompletionResult result = Complete(
            "/proc/f(mob/target, amount as num, silent = 0)\n\tvar/list/held = list()\n\t|\n");

        Assert.Equal("/mob", Assert.Single(result.Items, i => i.Name == "target").DeclaredType);
        Assert.Equal("/list", Assert.Single(result.Items, i => i.Name == "held").DeclaredType);
        Assert.Equal("list()", Assert.Single(result.Items, i => i.Name == "held").InitialValue);

        // `as num` is an input filter, not a type: reporting `num` here would claim something the
        // compiler does not hold. The default value is still reported.
        CompletionItem amount = Assert.Single(result.Items, i => i.Name == "amount");
        Assert.Equal(string.Empty, amount.DeclaredType);

        Assert.Equal("0", Assert.Single(result.Items, i => i.Name == "silent").InitialValue);
    }
}
