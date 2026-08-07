using System.Linq;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Services;

public class WorkspaceSymbolServiceTests
{
    private static ObjectTree Build(string source, bool withBuiltins = false)
    {
        Document document = new("test.dm", SourceText.From(source), fromBuffer: true);

        ObjectTree tree = withBuiltins ? Builtins.CreateTree() : new ObjectTree();
        TypeTreeBuilder.AddFile(tree, "test.dm", document.Parse);

        return tree;
    }

    private static string[] Names(IReadOnlyList<WorkspaceSymbol> hits)
        => hits.Select(h => h.Detail).ToArray();

    /// <summary>
    /// A leading kind filter narrows the search, spelled the way DM already spells the same
    /// distinction — <c>var/</c>, <c>proc/</c>, <c>verb/</c>, and <c>#</c> for a type, which is the
    /// one kind with no segment word of its own.
    /// </summary>
    [Fact]
    public void A_prefix_filter_narrows_to_one_kind()
    {
        // One name shared by a type, a var and a proc, so only the filter can tell them apart.
        ObjectTree tree = Build(
            "/obj/blade\n\tvar/blade = 1\n\tproc/blade()\n\t\treturn\n\tverb/blade_verb()\n\t\treturn\n");

        Assert.Equal(new[] { "/obj/blade" }, Names(WorkspaceSymbolService.Search(tree, "#blade")));
        Assert.Equal(new[] { "/obj/blade/blade" }, Names(WorkspaceSymbolService.Search(tree, "var/blade")));
        Assert.Equal(new[] { "/obj/blade/blade()" }, Names(WorkspaceSymbolService.Search(tree, "proc/blade")));
        Assert.Equal(new[] { "/obj/blade/blade_verb()" }, Names(WorkspaceSymbolService.Search(tree, "verb/blade")));
    }

    /// <summary>A bare filter lists that whole kind — what a user mid-type has actually asked for.</summary>
    [Fact]
    public void A_bare_filter_lists_the_kind()
    {
        ObjectTree tree = Build("/obj/sword\n\tvar/sharpness = 1\n\tvar/weight = 2\n\tproc/swing()\n\t\treturn\n");

        string[] vars = Names(WorkspaceSymbolService.Search(tree, "var/"));

        Assert.Contains("/obj/sword/sharpness", vars);
        Assert.Contains("/obj/sword/weight", vars);
        Assert.DoesNotContain("/obj/sword/swing()", vars);
        Assert.DoesNotContain("/obj/sword", vars);
    }

    /// <summary>An unfiltered query is untouched by the feature.</summary>
    [Fact]
    public void An_unfiltered_query_still_searches_every_kind()
    {
        ObjectTree tree = Build("/obj/blade\n\tvar/blade = 1\n\tproc/blade()\n\t\treturn\n");

        string[] all = Names(WorkspaceSymbolService.Search(tree, "blade"));

        Assert.Contains("/obj/blade", all);
        Assert.Contains("/obj/blade/blade", all);
        Assert.Contains("/obj/blade/blade()", all);
    }

    [Fact]
    public void Finds_types_vars_and_procs()
    {
        ObjectTree tree = Build("/obj/sword\n\tvar/sharpness = 1\n\tproc/swing()\n\t\treturn\n");

        Assert.Contains("/obj/sword", Names(WorkspaceSymbolService.Search(tree, "sword")));
        Assert.Contains("/obj/sword/sharpness", Names(WorkspaceSymbolService.Search(tree, "sharp")));
        Assert.Contains("/obj/sword/swing()", Names(WorkspaceSymbolService.Search(tree, "swing")));
    }

    /// <summary>
    /// Ranking is the whole feature. An exact match beats a prefix, which beats a substring.
    /// </summary>
    /// <remarks>
    /// Without it, a short query in a large codebase returns a wall in arbitrary order and the
    /// picker is unusable.
    /// </remarks>
    [Fact]
    public void Ranks_exact_before_prefix_before_substring()
    {
        ObjectTree tree = Build(
            "/obj/unhit\n/obj/hitbox\n/obj/hit\n");

        string[] found = Names(WorkspaceSymbolService.Search(tree, "hit"));

        Assert.Equal("/obj/hit", found[0]);      // exact
        Assert.Equal("/obj/hitbox", found[1]);   // prefix
        Assert.Equal("/obj/unhit", found[2]);    // substring
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        ObjectTree tree = Build("/obj/Sword\n");

        Assert.Single(WorkspaceSymbolService.Search(tree, "sword"));
    }

    /// <summary>Two procs with the same name are told apart by their owner.</summary>
    [Fact]
    public void The_detail_disambiguates_same_named_members()
    {
        ObjectTree tree = Build(
            "/mob/a\n\tproc/tick()\n\t\treturn\n/mob/b\n\tproc/tick()\n\t\treturn\n");

        string[] found = Names(WorkspaceSymbolService.Search(tree, "tick"));

        Assert.Contains("/mob/a/tick()", found);
        Assert.Contains("/mob/b/tick()", found);
    }

    /// <summary>
    /// Builtins are excluded: nothing declares them, so a hit could not be opened.
    /// </summary>
    [Fact]
    public void Builtins_are_not_offered()
    {
        ObjectTree tree = Build("/mob/guy\n\tvar/health = 1\n", withBuiltins: true);

        // `loc` is a builtin on /atom and exists in the tree, but has no declaration site.
        Assert.DoesNotContain(
            WorkspaceSymbolService.Search(tree, "loc"),
            h => h.Kind == SymbolKind.Variable && h.Name == "loc");
    }

    [Fact]
    public void The_limit_is_honoured()
    {
        ObjectTree tree = Build("/obj/hit1\n/obj/hit2\n/obj/hit3\n/obj/hit4\n");

        Assert.Equal(2, WorkspaceSymbolService.Search(tree, "hit", limit: 2).Count);
    }

    [Fact]
    public void An_empty_query_returns_nothing()
    {
        ObjectTree tree = Build("/obj/sword\n");

        Assert.Empty(WorkspaceSymbolService.Search(tree, "   "));
    }

    /// <summary>A hit carries the name range, so a picker can put the caret on the name.</summary>
    [Fact]
    public void A_hit_carries_the_name_range()
    {
        const string Source = "/obj/sword\n\tvar/sharpness = 1\n";

        ObjectTree tree = Build(Source);
        WorkspaceSymbol hit = WorkspaceSymbolService.Search(tree, "sharpness").First();

        Assert.Equal("sharpness", Source.Substring(hit.NameSpan.Start, hit.NameSpan.Length));
    }
}
