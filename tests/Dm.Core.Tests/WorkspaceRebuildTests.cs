using Dm.Core;
using Dm.Core.Symbols;

namespace Dm.Core.Tests;

/// <summary>
/// What the caches between rebuilds must never do: hand back an answer describing text the user no
/// longer has. Each of these edits a buffer and asks the workspace what it believes afterwards.
/// </summary>
public class WorkspaceRebuildTests
{
    private static TempDirectory Project()
    {
        TempDirectory temp = new();
        temp.Write("game.dme", "#include \"a.dm\"\n#include \"b.dm\"\n");
        temp.Write("a.dm", "/obj/item\n\tvar/hp = 1\n");
        temp.Write("b.dm", "/mob/player\n\tvar/name_tag = \"x\"\n");
        return temp;
    }

    [Fact]
    public void An_edit_is_visible_in_the_next_tree()
    {
        using TempDirectory temp = Project();
        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));

        Assert.Null(workspace.GetObjectTree().Find("/obj/crate"));

        workspace.SetBuffer(Path.Combine(temp.Path, "a.dm"), "/obj/item\n\tvar/hp = 1\n/obj/crate\n");

        Assert.NotNull(workspace.GetObjectTree().Find("/obj/crate"));
    }

    /// <summary>
    /// The file that was not touched has to survive the rebuild. A cache that dropped it would look
    /// exactly like a project that never declared it.
    /// </summary>
    [Fact]
    public void An_untouched_file_survives_a_rebuild()
    {
        using TempDirectory temp = Project();
        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));

        workspace.GetObjectTree();
        workspace.SetBuffer(Path.Combine(temp.Path, "a.dm"), "/obj/item\n\tvar/hp = 2\n");

        TypeSymbol? player = workspace.GetObjectTree().Find("/mob/player");

        Assert.NotNull(player);
        Assert.NotNull(player!.FindVar("name_tag"));
    }

    /// <summary>Undoing an edit has to undo what it declared, or the cache is keeping a ghost.</summary>
    [Fact]
    public void Reverting_an_edit_reverts_the_tree()
    {
        using TempDirectory temp = Project();
        string file = Path.Combine(temp.Path, "a.dm");

        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));
        workspace.GetObjectTree();

        workspace.SetBuffer(file, "/obj/item\n\tvar/hp = 1\n/obj/crate\n");
        Assert.NotNull(workspace.GetObjectTree().Find("/obj/crate"));

        workspace.SetBuffer(file, "/obj/item\n\tvar/hp = 1\n");
        Assert.Null(workspace.GetObjectTree().Find("/obj/crate"));
    }

    /// <summary>
    /// The case a per-file cache is most likely to get wrong. Editing a `#define` changes what every
    /// file after it means, while those files' own text is untouched — so anything keyed on the file
    /// alone would serve the previous expansion.
    /// </summary>
    [Fact]
    public void A_changed_define_reaches_the_files_that_use_it()
    {
        using TempDirectory temp = new();
        temp.Write("game.dme", "#include \"defs.dm\"\n#include \"use.dm\"\n");
        temp.Write("defs.dm", "#define THING /obj/first\n");
        temp.Write("use.dm", "THING\n\tvar/hp = 1\n");

        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));

        Assert.NotNull(workspace.GetObjectTree().Find("/obj/first"));

        workspace.SetBuffer(Path.Combine(temp.Path, "defs.dm"), "#define THING /obj/second\n");

        ObjectTree tree = workspace.GetObjectTree();

        Assert.NotNull(tree.Find("/obj/second"));
        Assert.Null(tree.Find("/obj/first"));
    }

    /// <summary>
    /// Closing a buffer goes back to disk, which is a second way the text under a cached answer can
    /// change without that file being edited again.
    /// </summary>
    [Fact]
    public void Closing_a_buffer_returns_to_the_file_on_disk()
    {
        using TempDirectory temp = Project();
        string file = Path.Combine(temp.Path, "a.dm");

        using Workspace workspace = Workspace.Open(Path.Combine(temp.Path, "game.dme"));

        workspace.SetBuffer(file, "/obj/item\n/obj/only_in_the_buffer\n");
        Assert.NotNull(workspace.GetObjectTree().Find("/obj/only_in_the_buffer"));

        workspace.CloseBuffer(file);
        Assert.Null(workspace.GetObjectTree().Find("/obj/only_in_the_buffer"));
    }
}
