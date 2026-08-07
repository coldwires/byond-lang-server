using System;
using System.IO;
using Dm.Core;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Xunit;

namespace Dm.Core.Tests;

/// <summary>
/// A file the <c>.dme</c> never includes: it resolves as its own single-file project rather than
/// looking half-broken.
/// </summary>
/// <remarks>
/// The shape that made this worth fixing: from inside such a file, symbols declared in PROJECT
/// files resolve normally while the file's own procs resolve nowhere. A user reads that as a bug
/// in the editor, and a client cannot tell it apart from a failed buffer push.
/// </remarks>
[Collection("handle table")]
public class StandaloneFileTests : IDisposable
{
    private readonly string _dir;

    public StandaloneFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dm_standalone_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);

        File.WriteAllText(Path.Combine(_dir, "game.dme"), "#include \"included.dm\"\n");
        File.WriteAllText(Path.Combine(_dir, "included.dm"), "/mob/project\n\tvar/hp = 1\n");

        // Never referenced by the .dme - a scratch file, or one not yet included.
        File.WriteAllText(
            Path.Combine(_dir, "outside.dm"),
            "/mob/orphan\n\tvar/stamina = 5\n\tproc/rest()\n\t\treturn stamina\n");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private Workspace Open() => Workspace.Open(Path.Combine(_dir, "game.dme"));

    [Fact]
    public void The_walk_reaches_an_included_file_and_not_an_outside_one()
    {
        using Workspace ws = Open();

        Assert.True(ws.IsFileInProject("included.dm"));
        Assert.False(ws.IsFileInProject("outside.dm"));

        // The .dme is the root of the walk rather than an entry in it, and still counts.
        Assert.True(ws.IsFileInProject("game.dme"));
    }

    /// <summary>The point of the feature: its own declarations resolve.</summary>
    [Fact]
    public void An_outside_file_resolves_its_own_declarations()
    {
        using Workspace ws = Open();

        ObjectTree tree = ws.GetTreeFor("outside.dm");

        Assert.NotNull(tree.Find(TypePath.Parse("/mob/orphan")));

        TypeSymbol orphan = tree.Find(TypePath.Parse("/mob/orphan"))!;
        Assert.NotNull(orphan.FindVar("stamina"));
        Assert.NotNull(orphan.FindProc("rest"));
    }

    /// <summary>
    /// The builtins come with it, so an outside file is a real compilation unit rather than a bare
    /// parse — <c>loc</c> and <c>Move()</c> resolve exactly as they do in the project.
    /// </summary>
    [Fact]
    public void An_outside_file_still_gets_the_builtins()
    {
        using Workspace ws = Open();

        ObjectTree tree = ws.GetTreeFor("outside.dm");
        TypeSymbol mob = Assert.IsType<TypeSymbol>(tree.Find(TypePath.Parse("/mob")));

        Assert.NotNull(tree.ResolveVar(mob, "loc"));
    }

    /// <summary>
    /// And it deliberately cannot see the project. The compiler would not either — the file is not
    /// in the program — and reaching across would be inventing a resolution dm.exe does not make.
    /// </summary>
    [Fact]
    public void An_outside_file_does_not_see_project_declarations()
    {
        using Workspace ws = Open();

        Assert.Null(ws.GetTreeFor("outside.dm").Find(TypePath.Parse("/mob/project")));

        // The project's own tree is unaffected and still has it.
        Assert.NotNull(ws.GetTreeFor("included.dm").Find(TypePath.Parse("/mob/project")));
    }

    /// <summary>An included file keeps getting the project tree, not a single-file one.</summary>
    [Fact]
    public void An_included_file_gets_the_project_tree()
    {
        using Workspace ws = Open();

        Assert.Same(ws.GetObjectTree(), ws.GetTreeFor("included.dm"));
        Assert.NotSame(ws.GetObjectTree(), ws.GetTreeFor("outside.dm"));
    }

    /// <summary>Cached on the parse, so an unchanged buffer reuses it and an edit rebuilds.</summary>
    [Fact]
    public void A_standalone_tree_is_cached_until_the_file_changes()
    {
        using Workspace ws = Open();

        Assert.Same(ws.GetTreeFor("outside.dm"), ws.GetTreeFor("outside.dm"));

        ws.SetBuffer("outside.dm", "/mob/orphan\n\tvar/stamina = 5\n\tvar/added = 1\n");

        ObjectTree after = ws.GetTreeFor("outside.dm");
        TypeSymbol orphan = Assert.IsType<TypeSymbol>(after.Find(TypePath.Parse("/mob/orphan")));

        Assert.NotNull(orphan.FindVar("added"));
    }
}
