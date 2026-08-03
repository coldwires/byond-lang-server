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
}
