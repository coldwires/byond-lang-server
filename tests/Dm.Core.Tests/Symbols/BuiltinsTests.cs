using System.IO;
using System.Linq;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Symbols;

public class BuiltinsTests
{
    [Fact]
    public void The_bundled_table_records_the_byond_version()
    {
        Builtins.CreateTree();

        Assert.NotEqual("unknown", Builtins.Version);
        Assert.StartsWith("516.", Builtins.Version);
    }

    /// <summary>
    /// The acceptance target for this milestone: <c>mob.</c> offers the builtins, none of which
    /// appear in any source file.
    /// </summary>
    [Theory]
    [InlineData("/mob", "Login")]
    [InlineData("/mob", "Logout")]
    [InlineData("/atom", "loc")]
    [InlineData("/atom/movable", "Move")]
    [InlineData("/client", "mob")]
    [InlineData("/world", "maxx")]
    public void The_standard_library_is_present(string path, string member)
    {
        ObjectTree tree = Builtins.CreateTree();
        TypeSymbol type = Assert.IsType<TypeSymbol>(tree.Find(path));

        Assert.True(
            type.FindVar(member) is not null || type.FindProc(member) is not null,
            $"expected {path} to declare {member}");
    }

    /// <summary>
    /// <c>/mob</c> is a child of the root by path but inherits from <c>/atom/movable</c>. Without
    /// that link, which no path encodes, <c>mob.</c> offers no <c>loc</c> and no <c>Move()</c>.
    /// </summary>
    [Fact]
    public void Mob_inherits_from_atom_movable()
    {
        ObjectTree tree = Builtins.CreateTree();
        TypeSymbol mob = tree.Find("/mob")!;

        Assert.Equal("/atom/movable", mob.ParentType!.Value.Text);
        Assert.NotNull(tree.ResolveVar(mob, "loc"));
        Assert.NotNull(tree.ResolveProc(mob, "Move"));

        Assert.Equal(
            new[] { "/mob", "/atom/movable", "/atom", "/datum" },
            tree.InheritanceChain(mob).Select(t => t.Path.Text).ToArray());
    }

    /// <summary>
    /// Read off the compiler with <c>initial(T:parent_type)</c>: these three have no parent rather
    /// than descending from <c>/datum</c>, which is not what a reader would guess.
    /// </summary>
    [Theory]
    [InlineData("/client")]
    [InlineData("/list")]
    [InlineData("/savefile")]
    public void Some_builtins_have_no_parent(string path)
    {
        Assert.Null(Builtins.CreateTree().Find(path)!.ParentType);
    }

    /// <summary>A project reopening a builtin type adds to it rather than replacing it.</summary>
    [Fact]
    public void A_project_can_extend_a_builtin_type()
    {
        ObjectTree tree = Builtins.CreateTree();
        TypeTreeBuilder.AddFile(
            tree,
            "game.dm",
            DeclarationParser.Parse(Lexer.Lex(SourceText.From("/mob\n\tvar/hp = 100\n\tproc/attack()\n\t\treturn\n"))));

        TypeSymbol mob = tree.Find("/mob")!;

        Assert.NotNull(mob.FindVar("hp"));
        Assert.NotNull(mob.FindProc("attack"));

        // The builtins survive, and so does the inheritance link.
        Assert.NotNull(mob.FindProc("Login"));
        Assert.NotNull(tree.ResolveVar(mob, "loc"));
        Assert.True(mob.IsBuiltin);

        // The project's own members are not marked as builtin.
        Assert.False(mob.FindVar("hp")!.IsBuiltin);
        Assert.True(mob.FindProc("Login")!.IsBuiltin);
    }

    [Fact]
    public void Builtin_procs_carry_their_signatures()
    {
        ProcSymbol move = Builtins.CreateTree().Find("/atom/movable")!.FindProc("Move")!;

        Assert.NotEmpty(move.Parameters);
        Assert.Equal("NewLoc", move.Parameters[0]);
    }

    /// <summary>Global procs land on the root, which is where an unqualified call resolves.</summary>
    [Theory]
    [InlineData("istype")]
    [InlineData("locate")]
    [InlineData("text2num")]
    public void Global_procs_land_on_the_root(string name)
    {
        Assert.NotNull(Builtins.CreateTree().Root.FindProc(name));
    }

    /// <summary>The reader takes a newer table than the bundled one, for a different BYOND install.</summary>
    [Fact]
    public void A_caller_may_supply_its_own_table()
    {
        ObjectTree tree = new();
        string declared = Builtins.Read(tree, new StringReader("# comment\nversion 999.1\nT /thing\nV /thing count\nP /thing Go a,b\nX /thing /datum\n"));

        // Returned, not parked in a static. A caller's table used to overwrite Builtins.Version for
        // the whole process, so this test failed whenever another one seeded the real builtins at
        // the same moment — which is what the intermittent failure was.
        Assert.Equal("999.1", declared);
        Assert.StartsWith("516.", Builtins.Version);

        TypeSymbol thing = tree.Find("/thing")!;
        Assert.NotNull(thing.FindVar("count"));
        Assert.Equal(new[] { "a", "b" }, thing.FindProc("Go")!.Parameters.ToArray());
        Assert.Equal("/datum", thing.ParentType!.Value.Text);
    }
}
