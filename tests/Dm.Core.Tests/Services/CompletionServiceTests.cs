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
}
