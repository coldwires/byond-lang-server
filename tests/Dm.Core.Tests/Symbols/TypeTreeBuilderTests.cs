using System.Collections.Generic;
using System.Linq;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Symbols;

public class TypeTreeBuilderTests
{
    private static ObjectTree Build(params string[] files)
    {
        List<(string, ParseResult)> parsed = new();

        for (int i = 0; i < files.Length; i++)
            parsed.Add(($"file{i}.dm", DeclarationParser.Parse(Lexer.Lex(SourceText.From(files[i])))));

        return TypeTreeBuilder.Build(parsed);
    }

    // -- paths --------------------------------------------------------------

    /// <summary>Declaring a leaf brings its ancestors into being, as DM does.</summary>
    [Fact]
    public void Ancestors_are_created_on_demand()
    {
        ObjectTree tree = Build("/obj/item/sword\n");

        Assert.NotNull(tree.Find("/obj"));
        Assert.NotNull(tree.Find("/obj/item"));
        Assert.NotNull(tree.Find("/obj/item/sword"));

        // Only the leaf was actually written.
        Assert.False(tree.Find("/obj/item")!.IsDeclared);
        Assert.True(tree.Find("/obj/item/sword")!.IsDeclared);
    }

    /// <summary>Mid-path, <c>.</c> and <c>/</c> are the same separator, so both key one node.</summary>
    [Fact]
    public void Dotted_and_slashed_paths_are_the_same_type()
    {
        ObjectTree tree = Build("/obj/item/sword\n\tvar/a = 1\n", "/obj.item.sword\n\tvar/b = 2\n");

        TypeSymbol sword = Assert.IsType<TypeSymbol>(tree.Find("/obj/item/sword"));
        Assert.Equal(new[] { "a", "b" }, sword.Vars.Select(v => v.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public void Indentation_nests_paths()
    {
        ObjectTree tree = Build("mob\n\tvar/hp = 1\n\tclient\n\t\tvar/x = 2\n");

        Assert.NotNull(tree.Find("/mob")!.FindVar("hp"));
        Assert.NotNull(tree.Find("/mob/client")!.FindVar("x"));
    }

    /// <summary>
    /// <c>/mob/client</c> is a subtype of <c>/mob</c> named <c>client</c>, unrelated to the builtin
    /// <c>/client</c>. Keying by name rather than path would merge them.
    /// </summary>
    [Fact]
    public void A_nested_type_is_not_the_builtin_of_the_same_name()
    {
        ObjectTree tree = Build("mob\n\tclient\n\t\tvar/x = 1\n", "client\n\tvar/y = 2\n");

        Assert.NotNull(tree.Find("/mob/client")!.FindVar("x"));
        Assert.Null(tree.Find("/mob/client")!.FindVar("y"));
        Assert.NotNull(tree.Find("/client")!.FindVar("y"));
    }

    /// <summary>A leading <c>/</c> is absolute and ignores the indentation it sits under.</summary>
    [Fact]
    public void An_absolute_path_ignores_its_enclosing_block()
    {
        ObjectTree tree = Build("mob\n\tvar/hp = 1\n/obj/crate\n\tvar/size = 2\n");

        Assert.NotNull(tree.Find("/obj/crate")!.FindVar("size"));
        Assert.Null(tree.Find("/mob/obj/crate"));
    }

    // -- members ------------------------------------------------------------

    /// <summary><c>var</c> and <c>proc</c> are ordinary segments; the owner is what precedes them.</summary>
    [Theory]
    [InlineData("/mob/var/hp = 1\n")]
    [InlineData("mob\n\tvar/hp = 1\n")]
    [InlineData("mob\n\tvar\n\t\thp = 1\n")]
    public void A_var_lands_on_the_type_that_precedes_the_var_segment(string source)
    {
        Assert.NotNull(Build(source).Find("/mob")!.FindVar("hp"));
    }

    [Theory]
    [InlineData("/mob/proc/attack()\n\treturn\n")]
    [InlineData("mob\n\tproc/attack()\n\t\treturn\n")]
    [InlineData("mob\n\tproc\n\t\tattack()\n\t\t\treturn\n")]
    public void A_proc_lands_on_the_type_that_precedes_the_proc_segment(string source)
    {
        Assert.NotNull(Build(source).Find("/mob")!.FindProc("attack"));
    }

    [Fact]
    public void A_verb_is_recorded_as_one()
    {
        Assert.True(Build("/mob/verb/shout()\n\treturn\n").Find("/mob")!.FindProc("shout")!.IsVerb);
    }

    [Fact]
    public void A_var_keeps_its_declared_type()
    {
        VarSymbol variable = Build("/mob/var/mob/test/target = null\n").Find("/mob")!.FindVar("target")!;

        Assert.Equal("/mob/test", variable.DeclaredType!.Value.Text);
    }

    /// <summary>
    /// A type is legitimately declared in several files, and every site is kept so
    /// go-to-definition can offer them all.
    /// </summary>
    [Fact]
    public void Declarations_merge_across_files()
    {
        ObjectTree tree = Build("/mob\n\tvar/hp = 1\n", "/mob\n\tvar/mana = 2\n", "/mob\n\tproc/f()\n\t\treturn\n");

        TypeSymbol mob = tree.Find("/mob")!;

        Assert.Equal(2, mob.Vars.Count);
        Assert.Single(mob.Procs);
        Assert.Equal(3, mob.Sites.Count);
    }

    /// <summary>
    /// Inside a <c>var</c> block the leading segments are the variable's declared type, not a type
    /// path. Reading them as a path invents <c>/mob/atom/movable</c> — found in mlaas, where
    /// <c>handling.dm</c> declares <c>atom/movable/locker</c> in a type-level var block.
    /// </summary>
    [Fact]
    public void A_typed_var_in_a_var_block_does_not_create_a_type()
    {
        ObjectTree tree = Build("mob\n\tvar\n\t\tatom/movable/locker\n\t\tlist/inventory\n");

        VarSymbol locker = tree.Find("/mob")!.FindVar("locker")!;
        Assert.Equal("/atom/movable", locker.DeclaredType!.Value.Text);
        Assert.Equal("/list", tree.Find("/mob")!.FindVar("inventory")!.DeclaredType!.Value.Text);

        // The declared type is not a subtype of the enclosing one.
        Assert.Null(tree.Find("/mob/atom"));
        Assert.Null(tree.Find("/mob/list"));
    }

    /// <summary>
    /// A bare assignment is the other reading: no <c>var</c>, so the leading segments name the type
    /// whose inherited variable is being overridden.
    /// </summary>
    [Fact]
    public void A_bare_assignment_overrides_on_the_type_it_names()
    {
        ObjectTree tree = Build("/obj/item\n/obj/item/hp = 3\n");

        Assert.NotNull(tree.Find("/obj/item")!.FindVar("hp"));
        Assert.Null(tree.Find("/obj/item/hp"));
    }

    // -- inheritance --------------------------------------------------------

    [Fact]
    public void Members_resolve_up_the_implicit_path_chain()
    {
        ObjectTree tree = Build("/obj\n\tvar/weight = 1\n/obj/item/sword\n\tvar/damage = 5\n");

        TypeSymbol sword = tree.Find("/obj/item/sword")!;

        Assert.NotNull(tree.ResolveVar(sword, "damage"));
        Assert.NotNull(tree.ResolveVar(sword, "weight"));
        Assert.Null(tree.ResolveVar(sword, "nothing"));
    }

    /// <summary><c>parent_type</c> replaces the implicit link rather than adding to it.</summary>
    [Fact]
    public void Parent_type_redirects_inheritance()
    {
        ObjectTree tree = Build(
            "/datum/base\n\tvar/from_base = 1\n/datum/child\n\tparent_type = /datum/base\n\tvar/own = 2\n");

        TypeSymbol child = tree.Find("/datum/child")!;

        Assert.Equal("/datum/base", child.ParentType!.Value.Text);
        Assert.NotNull(tree.ResolveVar(child, "from_base"));

        // It is a link, not a variable.
        Assert.Null(child.FindVar("parent_type"));
    }

    /// <summary>
    /// A <c>parent_type</c> may name a type declared later, so the link resolves on lookup rather
    /// than when it is read.
    /// </summary>
    [Fact]
    public void Parent_type_may_point_forward()
    {
        ObjectTree tree = Build("/datum/child\n\tparent_type = /datum/base\n", "/datum/base\n\tvar/late = 1\n");

        Assert.NotNull(tree.ResolveVar(tree.Find("/datum/child")!, "late"));
    }

    /// <summary>
    /// <c>parent_type</c> is an ordinary assignment, so a project can write a cycle. Walking it
    /// must terminate rather than hang the editor.
    /// </summary>
    [Fact]
    public void A_parent_type_cycle_terminates()
    {
        ObjectTree tree = Build("/datum/a\n\tparent_type = /datum/b\n/datum/b\n\tparent_type = /datum/a\n");

        Assert.Null(tree.ResolveVar(tree.Find("/datum/a")!, "missing"));
        Assert.Equal(2, tree.InheritanceChain(tree.Find("/datum/a")!).Count());
    }

    // -- overrides ----------------------------------------------------------

    /// <summary>
    /// <c>proc/</c> declares; omitting it overrides. Two declaring sites on one type is the
    /// duplicate-definition error DM reports, so the count is kept for an M11 diagnostic.
    /// </summary>
    [Fact]
    public void Override_chains_are_kept_in_include_order()
    {
        ObjectTree tree = Build("/mob/proc/attack()\n\treturn\n", "/mob/attack()\n\treturn\n");

        ProcSymbol attack = tree.Find("/mob")!.FindProc("attack")!;

        Assert.Equal(2, attack.Sites.Count);
        Assert.Equal(1, attack.DeclaringCount);
        Assert.Equal("file0.dm", attack.Sites[0].File);
        Assert.Equal("file1.dm", attack.Sites[1].File);
    }

    [Fact]
    public void Declaring_a_proc_twice_is_visible()
    {
        ObjectTree tree = Build("/mob/proc/attack()\n\treturn\n", "/mob/proc/attack()\n\treturn\n");

        Assert.Equal(2, tree.Find("/mob")!.FindProc("attack")!.DeclaringCount);
    }

    /// <summary>An override on a subtype is a separate symbol from the one it overrides.</summary>
    [Fact]
    public void An_override_on_a_subtype_is_its_own_symbol()
    {
        ObjectTree tree = Build("/mob/proc/attack()\n\treturn\n/mob/orc/attack()\n\treturn\n");

        Assert.NotNull(tree.Find("/mob")!.FindProc("attack"));
        Assert.NotNull(tree.Find("/mob/orc")!.FindProc("attack"));
        Assert.Equal(0, tree.Find("/mob/orc")!.FindProc("attack")!.DeclaringCount);
    }
}
