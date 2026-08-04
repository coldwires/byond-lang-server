using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Tests.Text;

/// <summary>
/// The cache trades a filesystem probe per file for a read and a lex. What has to hold is that the
/// probe actually catches a change — a stale entry here means the whole analysis describes a file
/// the user no longer has.
/// </summary>
public class SourceCacheTests
{
    [Fact]
    public void An_unchanged_file_is_served_from_cache()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/obj/item\n");

        SourceCache cache = new();

        SourceText first = cache.Read(path);
        SourceText second = cache.Read(path);

        Assert.Same(first, second);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Hits);
    }

    /// <summary>A rewrite of a different length is caught by the length alone.</summary>
    [Fact]
    public void A_rewritten_file_is_read_again()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/obj/item\n");

        SourceCache cache = new();
        SourceText first = cache.Read(path);

        temp.Write("a.dm", "/obj/item\n/obj/crate\n");
        SourceText second = cache.Read(path);

        Assert.NotSame(first, second);
        Assert.Contains("crate", second.ToString());
    }

    /// <summary>
    /// The case the length cannot catch. A `git checkout` between two revisions of the same size is
    /// exactly this shape, so the timestamp has to carry it.
    /// </summary>
    [Fact]
    public void A_same_length_rewrite_is_caught_by_the_timestamp()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/obj/aaaa\n");

        SourceCache cache = new();
        SourceText first = cache.Read(path);

        // Same byte count, and stamped forward so the write is visible whatever the clock did.
        temp.Write("a.dm", "/obj/bbbb\n");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));

        SourceText second = cache.Read(path);

        Assert.NotSame(first, second);
        Assert.Contains("bbbb", second.ToString());
    }

    [Fact]
    public void A_lex_is_reused_for_the_text_it_was_taken_from()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/obj/item\n\tvar/hp = 1\n");

        SourceCache cache = new();
        SourceText text = cache.Read(path);

        LexResult first = cache.Lex(path, text);
        LexResult second = cache.Lex(path, text);

        Assert.Same(first, second);
    }

    /// <summary>
    /// A pushed buffer supplies its own text for a path the cache also knows. Handing back the
    /// cached lex there would analyse the file as last saved while the user is typing in it.
    /// </summary>
    [Fact]
    public void Foreign_text_for_a_cached_path_gets_its_own_lex()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/obj/item\n");

        SourceCache cache = new();
        SourceText onDisk = cache.Read(path);
        LexResult fromDisk = cache.Lex(path, onDisk);

        SourceText edited = SourceText.From("/obj/item\n/obj/crate\n", path);
        LexResult fromBuffer = cache.Lex(path, edited);

        Assert.NotSame(fromDisk, fromBuffer);
        Assert.Same(onDisk, cache.Read(path));
    }

    [Fact]
    public void Clearing_drops_everything()
    {
        using TempDirectory temp = new();
        string path = temp.Write("a.dm", "/obj/item\n");

        SourceCache cache = new();
        SourceText first = cache.Read(path);

        cache.Clear();

        Assert.NotSame(first, cache.Read(path));
        Assert.Equal(0, cache.Hits);
    }
}
