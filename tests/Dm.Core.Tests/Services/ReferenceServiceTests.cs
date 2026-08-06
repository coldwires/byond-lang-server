using System.Collections.Generic;
using System.Linq;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

/// <summary>
/// The reference index: uses found by the binder's own walk, so a hit exists exactly where
/// diagnostics resolution succeeds. Kinds, canonical targets, and the enclosing symbol are the
/// contract dm-patch's UPSTREAM-REQUESTS §1 asked for.
/// </summary>
public class ReferenceServiceTests
{
    private const string Types =
        "/mob/guy\n\tvar/hp = 1\n\tproc/heal(amount)\n\t\thp += amount\n";

    private const string Uses =
        "/mob/guy/child\n\theal(amount)\n\t\treturn ..()\n"
        + "/proc/f()\n\tvar/mob/guy/g = new\n\tg.hp = 5\n\treturn g.hp + g.heal(1)\n"
        + "/proc/t()\n\tvar/mob/guy/child/c = new\n\treturn istype(c, /mob/guy) ? c.hp : 0\n";

    private static (ObjectTree Tree, List<(string, ParseResult)> Files) Build(params string[] sources)
    {
        List<(string, ParseResult)> files = new();
        ObjectTree tree = new();
        Builtins.Seed(tree);

        for (int i = 0; i < sources.Length; i++)
        {
            ParseResult parse = DeclarationParser.Parse(Lexer.Lex(SourceText.From(sources[i])));
            files.Add(($"file{i}.dm", parse));
            TypeTreeBuilder.AddFile(tree, $"file{i}.dm", parse);
        }

        return (tree, files);
    }

    [Fact]
    public void A_var_is_found_where_it_is_written_and_read_with_the_enclosing_symbol()
    {
        (ObjectTree tree, var files) = Build(Types, Uses);

        ReferenceListing found = ReferenceService.Find(tree, files, "/mob/guy/hp");

        // `hp += amount` inside heal — a bare name resolved against the enclosing chain.
        Assert.Contains(found.References, r =>
            r.File == "file0.dm" && r.Kind == ReferenceKind.Write && r.Inside == "/mob/guy/heal()");

        // `g.hp = 5` and `g.hp + ...` through a typed receiver.
        Assert.Contains(found.References, r =>
            r.File == "file1.dm" && r.Kind == ReferenceKind.Write && r.Inside == "/f()");
        Assert.Contains(found.References, r =>
            r.File == "file1.dm" && r.Kind == ReferenceKind.Read && r.Inside == "/f()");

        Assert.False(found.Truncated);
    }

    /// <summary>
    /// A subtype receiver's hit lands on the SAME canonical target as the base type's — the
    /// farthest declaring type — or every query would fragment by receiver spelling.
    /// </summary>
    [Fact]
    public void A_subtype_receiver_canonicalises_to_the_declaring_type()
    {
        (ObjectTree tree, var files) = Build(Types, Uses);

        ReferenceListing found = ReferenceService.Find(tree, files, "/mob/guy/hp");

        // `c.hp` where c is /mob/guy/child.
        Assert.Contains(found.References, r => r.Inside == "/t()" && r.Kind == ReferenceKind.Read);
    }

    [Fact]
    public void A_proc_is_found_at_calls_and_at_its_overrides()
    {
        (ObjectTree tree, var files) = Build(Types, Uses);

        ReferenceListing found = ReferenceService.Find(tree, files, "/mob/guy/heal()");

        Assert.Contains(found.References, r => r.Kind == ReferenceKind.Call && r.Inside == "/f()");

        // /mob/guy/child's marker-less declaration is an override of the origin.
        Assert.Contains(found.References, r => r.Kind == ReferenceKind.Override && r.File == "file1.dm");
    }

    [Fact]
    public void A_type_is_found_at_its_path_literals()
    {
        (ObjectTree tree, var files) = Build(Types, Uses);

        ReferenceListing found = ReferenceService.Find(tree, files, "/mob/guy");

        // The istype(c, /mob/guy) literal.
        Assert.Contains(found.References, r => r.Kind == ReferenceKind.Read && r.Inside == "/t()");
    }

    /// <summary>Would fail by over-reporting: a local shadows the member whatever its type.</summary>
    [Fact]
    public void A_shadowing_local_is_not_a_reference()
    {
        (ObjectTree tree, var files) = Build(
            Types,
            "/proc/shadowed()\n\tvar/hp = 3\n\thp = 4\n\treturn hp\n");

        ReferenceListing found = ReferenceService.Find(tree, files, "/mob/guy/hp");

        Assert.DoesNotContain(found.References, r => r.File == "file1.dm");
    }

    [Fact]
    public void The_limit_caps_and_reports_truncation()
    {
        (ObjectTree tree, var files) = Build(Types, Uses);

        ReferenceListing found = ReferenceService.Find(tree, files, "/mob/guy/hp", limit: 1);

        Assert.Single(found.References);
        Assert.True(found.Truncated);
    }
}
