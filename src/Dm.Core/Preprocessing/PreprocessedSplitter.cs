using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Cuts the project-wide preprocessed stream back into per-file runs the parser can read.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Preprocessor.Run"/> produces one stream for the whole project in compile order. The
/// parser works a file at a time, and the object tree wants a file name to attribute each
/// declaration to, so the stream is split on the file each token was <b>written</b> in — the
/// invocation site for anything that came out of a macro, not the file the macro was defined in.
/// </para>
/// <para>
/// A file's tokens are <b>gathered</b>, not merely cut at each change of origin. An <c>#include</c>
/// interrupts the including file's stream with the included file's tokens, so cutting on every
/// change hands the parser fragments — and a fragment that begins inside an indented block has no
/// opening <c>Indent</c>, so the block structure is lost and the declarations under it go with it.
/// The file's own text is contiguous in the file itself, so putting its runs back together is what
/// restores the structure the lexer emitted.
/// </para>
/// <para>
/// Order is first appearance, which is the order the compiler reaches each file in, and which is
/// what decides override resolution.
/// </para>
/// </remarks>
internal static class PreprocessedSplitter
{
    /// <summary>One entry per file reached, in compile order, with that file's tokens gathered.</summary>
    public static List<(string File, TokenSource Source)> Split(
        PreprocessResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        List<(string, TokenSource)> files = new(result.Runs.Count);

        foreach (PreprocessedFile run in result.Runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            files.Add((run.Origin.Path ?? string.Empty, TokenSource.FromExpanded(run.Origin, run.Tokens)));
        }

        return files;
    }

    /// <summary>
    /// The same split, with each file's token source and parse taken from <paramref name="cache"/>
    /// when the preprocessor produced the same run for it as last time.
    /// </summary>
    /// <remarks>
    /// The parse is returned alongside because it is what the cache is for: a rebuild re-parses
    /// every file, and after an edit almost none of them have changed. Building the token source is
    /// avoided with it, since that allocates three arrays per file.
    /// </remarks>
    public static List<(string File, TokenSource Source, ParseResult Parse)> SplitAndParse(
        PreprocessResult result, ExpandedRunCache cache, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(cache);

        List<(string, TokenSource, ParseResult)> files = new(result.Runs.Count);

        foreach (PreprocessedFile run in result.Runs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string file = run.Origin.Path ?? string.Empty;
            (TokenSource source, ParseResult parse) = cache.GetOrAdd(file, run.Origin, run.Tokens);
            files.Add((file, source, parse));
        }

        return files;
    }
}
