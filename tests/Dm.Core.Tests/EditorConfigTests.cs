using Dm.Core.Services;

namespace Dm.Core.Tests;

/// <summary>
/// <c>.editorconfig</c> as the formatter's configuration, per <c>docs/dm-format.md</c>: where the
/// file disagrees with the spec it wins, and where it says nothing the spec's defaults stand.
/// </summary>
/// <remarks>
/// Driven through real files on disk rather than a parse entry point, because the resolution
/// ORDER is most of the format — nearest file wins, <c>root</c> stops the walk, a later section
/// wins over an earlier one — and none of that is exercised by parsing one file's text.
/// </remarks>
public sealed class EditorConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dm-editorconfig-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativeDirectory, string text)
    {
        string directory = Path.Combine(_root, relativeDirectory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ".editorconfig"), text);
        return directory;
    }

    /// <summary>A path under the temp root. The file need not exist; only its path is read.</summary>
    private string Target(string relativePath) => Path.Combine(_root, relativePath);

    [Fact]
    public void With_no_editorconfig_the_specs_defaults_stand()
    {
        // Nothing is written here on purpose, and `root` in the file below is what stops the walk
        // from climbing into whatever this machine has above the temp directory.
        Write(".", "root = true\n");

        Assert.True(FormatOptions.ForFile(Target("code.dm")).TrimTrailingWhitespace);
    }

    [Fact]
    public void A_project_can_turn_a_rule_off()
    {
        Write(".", "root = true\n\n[*.dm]\ntrim_trailing_whitespace = false\n");

        Assert.False(FormatOptions.ForFile(Target("code.dm")).TrimTrailingWhitespace);
    }

    /// <summary>A section that does not cover the file changes nothing about it.</summary>
    [Fact]
    public void A_section_for_another_extension_is_ignored()
    {
        Write(".", "root = true\n\n[*.md]\ntrim_trailing_whitespace = false\n");

        Assert.True(FormatOptions.ForFile(Target("code.dm")).TrimTrailingWhitespace);
    }

    /// <summary>
    /// tgstation's own shape: a broad <c>[*]</c> block, then a narrower one for the DM files. The
    /// later section wins for a file both cover.
    /// </summary>
    [Fact]
    public void A_later_section_wins_over_an_earlier_one()
    {
        Write(
            ".",
            "root = true\n\n[*]\ntrim_trailing_whitespace = true\n\n[*.{dm,json,md}]\ntrim_trailing_whitespace = false\n");

        Assert.False(FormatOptions.ForFile(Target("code.dm")).TrimTrailingWhitespace);
        Assert.True(FormatOptions.ForFile(Target("notes.txt")).TrimTrailingWhitespace);
    }

    [Fact]
    public void The_nearest_file_wins()
    {
        Write(".", "root = true\n\n[*]\ntrim_trailing_whitespace = true\n");
        Write("code", "[*]\ntrim_trailing_whitespace = false\n");

        Assert.False(FormatOptions.ForFile(Target(Path.Combine("code", "mob.dm"))).TrimTrailingWhitespace);
    }

    /// <summary>
    /// <c>root = true</c> stops the upward walk, so the outer file is never read — asserted by
    /// giving the outer one a value the inner one does not mention.
    /// </summary>
    [Fact]
    public void Root_stops_the_walk()
    {
        Write(".", "root = true\n\n[*]\ntrim_trailing_whitespace = false\n");
        Write("code", "root = true\n\n[*]\ncharset = utf-8\n");

        Assert.True(FormatOptions.ForFile(Target(Path.Combine("code", "mob.dm"))).TrimTrailingWhitespace);
    }

    /// <summary>
    /// A comment is not a value. Both spellings the format allows, and one on the same line as a
    /// property, since a reader that only handled whole-line comments would take
    /// <c>false ; note</c> as neither true nor false and quietly leave the default.
    /// </summary>
    [Fact]
    public void Comments_are_not_values()
    {
        Write(
            ".",
            "root = true\n# a hash comment\n; a semicolon comment\n\n[*.dm]\ntrim_trailing_whitespace = false ; DM keeps its own\n");

        Assert.False(FormatOptions.ForFile(Target("code.dm")).TrimTrailingWhitespace);
    }

    /// <summary>
    /// The three keys the spec's example block shows. Only <c>trim_trailing_whitespace</c> reaches
    /// a v1 rule; the other two govern the two things v1 never touches, so a file setting them
    /// changes nothing rather than being obeyed halfway.
    /// </summary>
    [Fact]
    public void Indentation_and_final_newline_keys_reach_no_v1_rule()
    {
        Write(
            ".",
            "root = true\n\n[*.dm]\nindent_style = space\nindent_size = 4\ninsert_final_newline = true\n");

        FormatOptions options = FormatOptions.ForFile(Target("code.dm"));

        Assert.True(options.TrimTrailingWhitespace);
        Assert.True(options.SpaceAroundAssignment);
    }

    [Theory]
    [InlineData("*", "code.dm", true)]
    [InlineData("*.dm", "code.dm", true)]
    [InlineData("*.dm", "code.dme", false)]
    [InlineData("*.{dm,dme}", "code.dme", true)]
    [InlineData("*.{dm,json,md}", "code.txt", false)]
    [InlineData("*.dm", "code/mob.dm", true)]
    [InlineData("code/*.dm", "code/mob.dm", true)]
    [InlineData("code/*.dm", "code/deep/mob.dm", false)]
    [InlineData("code/**.dm", "code/deep/mob.dm", true)]
    [InlineData("/code/*.dm", "code/mob.dm", true)]
    [InlineData("mob?.dm", "mob1.dm", true)]
    [InlineData("mob?.dm", "mob12.dm", false)]
    public void Globs_match_the_way_the_format_says(string pattern, string relativePath, bool expected)
        => Assert.Equal(
            expected,
            EditorConfig.SectionMatches(pattern, _root, Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
}
