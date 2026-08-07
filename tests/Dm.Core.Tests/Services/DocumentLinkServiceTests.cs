using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Services;
using Dm.Core.Text;
using Xunit;

namespace Dm.Core.Tests.Services;

public class DocumentLinkServiceTests
{
    /// <summary>Writes a file and returns a Document reading it, so resolution has real disk to hit.</summary>
    private static Document Open(string directory, string name, string source)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, source);
        return new Document(path, SourceText.From(source, path), fromBuffer: false);
    }

    private static string TempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dm_links_" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void An_include_that_exists_becomes_a_link_on_the_path_text()
    {
        string dir = TempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(dir, "mob.dm"), "/mob\n");
            Document document = Open(dir, "game.dme", "#include \"mob.dm\"\n");

            DocumentLink link = Assert.Single(DocumentLinkService.LinksFor(document));

            Assert.Equal(Path.Combine(dir, "mob.dm"), link.Target);

            // The clickable span is the path alone, not the whole directive line.
            Assert.Equal("mob.dm", document.Text.ToString(link.Span));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// Windows separators are the norm in real <c>.dme</c> files and both forms work, so a link
    /// that only handled <c>/</c> would resolve nothing on a real project.
    /// </summary>
    [Fact]
    public void A_backslash_separator_resolves()
    {
        string dir = TempDirectory();

        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "src", "mob.dm"), "/mob\n");

            Document document = Open(dir, "game.dme", "#include \"src\\mob.dm\"\n");

            DocumentLink link = Assert.Single(DocumentLinkService.LinksFor(document));
            Assert.Equal(Path.Combine(dir, "src", "mob.dm"), link.Target);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// A missing file yields no link. Navigation that dead-ends is worse than none, and a broken
    /// include is exactly where a reader wants to notice rather than be reassured.
    /// </summary>
    [Fact]
    public void An_include_that_does_not_exist_yields_no_link()
    {
        string dir = TempDirectory();

        try
        {
            Document document = Open(dir, "game.dme", "#include \"nope.dm\"\n");
            Assert.Empty(DocumentLinkService.LinksFor(document));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Driven off tokens, so a directive inside a comment is correctly not one.</summary>
    [Fact]
    public void An_include_inside_a_comment_is_not_a_link()
    {
        string dir = TempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(dir, "mob.dm"), "/mob\n");
            Document document = Open(dir, "game.dme", "/*\n#include \"mob.dm\"\n*/\n");

            Assert.Empty(DocumentLinkService.LinksFor(document));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Several_includes_all_resolve()
    {
        string dir = TempDirectory();

        try
        {
            File.WriteAllText(Path.Combine(dir, "a.dm"), "/mob\n");
            File.WriteAllText(Path.Combine(dir, "b.dm"), "/obj\n");

            Document document = Open(dir, "game.dme", "#include \"a.dm\"\n#include \"b.dm\"\n");

            IReadOnlyList<DocumentLink> links = DocumentLinkService.LinksFor(document);

            Assert.Equal(2, links.Count);
            Assert.Equal("a.dm", document.Text.ToString(links[0].Span));
            Assert.Equal("b.dm", document.Text.ToString(links[1].Span));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A library include needs a root; without one it resolves nowhere rather than guessing.</summary>
    [Fact]
    public void A_library_include_needs_a_root()
    {
        string dir = TempDirectory();

        try
        {
            Document document = Open(dir, "game.dme", "#include <vendor/thing>\n");
            Assert.Empty(DocumentLinkService.LinksFor(document, libraryRoot: null));

            string lib = Path.Combine(dir, "lib");
            Directory.CreateDirectory(Path.Combine(lib, "vendor", "thing"));
            File.WriteAllText(Path.Combine(lib, "vendor", "thing", "thing.dm"), "/mob\n");

            DocumentLink link = Assert.Single(DocumentLinkService.LinksFor(document, libraryRoot: lib));
            Assert.Equal(Path.Combine(lib, "vendor", "thing", "thing.dm"), link.Target);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
