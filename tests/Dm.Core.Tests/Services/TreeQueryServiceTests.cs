using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

public class TreeQueryServiceTests
{
    private const string Source = """
        /obj/item
        	var/hp = 1
        	proc/use()
        		return
        /obj/item/sword
        	var/damage = 5
        /obj/item/sword/magic
        /obj/crate
        """;

    private static ObjectTree Build(string source = Source)
    {
        ObjectTree tree = new();
        TypeTreeBuilder.AddFile(tree, "test.dm", DeclarationParser.Parse(Lexer.Lex(SourceText.From(source))));
        return tree;
    }

    [Fact]
    public void Browse_returns_one_level_of_children_by_default()
    {
        TreeNode node = Assert.IsType<TreeNode>(TreeQueryService.Browse(Build(), "/obj"));

        Assert.Equal("/obj", node.Path);
        Assert.Equal(new[] { "/obj/item", "/obj/crate" }, node.Children.Select(c => c.Path).ToArray());

        // One level only: the sword is a child of the item, not of /obj.
        Assert.Empty(node.Children[0].Children);
    }

    /// <summary>
    /// The count is what makes an expander arrow drawable without a second call, so it has to
    /// describe what exists rather than what came back.
    /// </summary>
    [Fact]
    public void A_depth_limited_node_still_reports_how_many_children_it_has()
    {
        TreeNode node = Assert.IsType<TreeNode>(TreeQueryService.Browse(Build(), "/obj", depth: 0));

        Assert.Empty(node.Children);
        Assert.Equal(2, node.ChildCount);
    }

    [Fact]
    public void Browse_counts_the_types_own_members()
    {
        TreeNode node = Assert.IsType<TreeNode>(TreeQueryService.Browse(Build(), "/obj/item", depth: 0));

        Assert.Equal(1, node.VarCount);
        Assert.Equal(1, node.ProcCount);
    }

    /// <summary>
    /// <c>/obj/item/sword</c> exists as a declared type; <c>/obj</c> exists only because things
    /// under it were declared. A tree panel wants to tell those apart.
    /// </summary>
    [Fact]
    public void An_implied_node_is_marked_undeclared()
    {
        ObjectTree tree = Build();

        Assert.False(Assert.IsType<TreeNode>(TreeQueryService.Browse(tree, "/obj", depth: 0)).Declared);
        Assert.True(Assert.IsType<TreeNode>(TreeQueryService.Browse(tree, "/obj/item", depth: 0)).Declared);
    }

    [Fact]
    public void An_unknown_path_is_null_rather_than_empty()
    {
        Assert.Null(TreeQueryService.Browse(Build(), "/obj/nothing"));
        Assert.Null(TreeQueryService.Subtypes(Build(), "/obj/nothing"));
        Assert.Null(TreeQueryService.Members(Build(), "/obj/nothing"));
    }

    [Fact]
    public void Subtypes_are_flat_and_exclude_the_type_asked_about()
    {
        SubtypeListing listing = Assert.IsType<SubtypeListing>(TreeQueryService.Subtypes(Build(), "/obj/item"));

        Assert.Equal(
            new[] { "/obj/item/sword", "/obj/item/sword/magic" },
            listing.Types.Select(t => t.Path).ToArray());

        Assert.False(listing.Truncated);
    }

    /// <summary>
    /// A list exactly as long as the limit is indistinguishable from one that was cut, so the flag
    /// is reported rather than inferred.
    /// </summary>
    [Fact]
    public void A_capped_subtype_listing_says_it_was_capped()
    {
        SubtypeListing listing =
            Assert.IsType<SubtypeListing>(TreeQueryService.Subtypes(Build(), "/obj/item", limit: 1));

        Assert.Single(listing.Types);
        Assert.True(listing.Truncated);
    }

    [Fact]
    public void Members_include_what_a_type_inherits_and_say_where_from()
    {
        TypeMembers members = Assert.IsType<TypeMembers>(
            TreeQueryService.Members(Build(), "/obj/item/sword", inherited: true));

        MemberEntry damage = Assert.Single(members.Vars, v => v.Name == "damage");
        Assert.False(damage.Inherited);
        Assert.Equal("/obj/item/sword", damage.Owner);

        MemberEntry hp = Assert.Single(members.Vars, v => v.Name == "hp");
        Assert.True(hp.Inherited);
        Assert.Equal("/obj/item", hp.Owner);
    }

    [Fact]
    public void Members_can_be_limited_to_the_type_itself()
    {
        TypeMembers members = Assert.IsType<TypeMembers>(
            TreeQueryService.Members(Build(), "/obj/item/sword", inherited: false));

        Assert.Equal(new[] { "damage" }, members.Vars.Select(v => v.Name).ToArray());
        Assert.Empty(members.Procs);
    }

    /// <summary>An override hides the ancestor's declaration, which is what the compiler does.</summary>
    [Fact]
    public void The_nearest_declaration_of_a_name_wins()
    {
        ObjectTree tree = Build("/obj/item\n\tvar/hp = 1\n/obj/item/sword\n\tvar/hp = 9\n");

        TypeMembers members = Assert.IsType<TypeMembers>(TreeQueryService.Members(tree, "/obj/item/sword"));

        MemberEntry hp = Assert.Single(members.Vars, v => v.Name == "hp");
        Assert.Equal("/obj/item/sword", hp.Owner);
        Assert.False(hp.Inherited);
    }

    /// <summary>
    /// Builtins are most of the tree once they are seeded, so a panel showing only the project's own
    /// declarations needs to be able to say so.
    /// </summary>
    [Fact]
    public void Builtins_can_be_excluded()
    {
        ObjectTree tree = Build();
        Builtins.Seed(tree);

        TreeNode all = Assert.IsType<TreeNode>(TreeQueryService.Browse(tree, "/", includeBuiltins: true));
        TreeNode mine = Assert.IsType<TreeNode>(TreeQueryService.Browse(tree, "/", includeBuiltins: false));

        Assert.True(all.ChildCount > mine.ChildCount);
        Assert.DoesNotContain(mine.Children, c => c.Builtin);
    }

    /// <summary>
    /// <c>/mob</c> is a child of the root by path and inherits from <c>/atom/movable</c>. A browser
    /// that showed the path parent would be describing a different program.
    /// </summary>
    [Fact]
    public void A_node_reports_where_it_inherits_from_rather_than_its_path_parent()
    {
        ObjectTree tree = new();
        Builtins.Seed(tree);

        TreeNode mob = Assert.IsType<TreeNode>(TreeQueryService.Browse(tree, "/mob", depth: 0));

        Assert.Equal("/atom/movable", mob.ParentType);
    }
}
