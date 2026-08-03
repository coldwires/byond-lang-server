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
public static class PreprocessedSplitter
{
    /// <summary>One entry per file reached, in compile order, with that file's tokens gathered.</summary>
    public static List<(string File, TokenSource Source)> Split(
        PreprocessResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        Dictionary<SourceText, List<ExpandedToken>> byOrigin = new();
        List<SourceText> order = new();

        foreach (ExpandedToken token in result.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SourceText origin = token.ReportAt.Source;

            if (!byOrigin.TryGetValue(origin, out List<ExpandedToken>? run))
            {
                run = new List<ExpandedToken>();
                byOrigin.Add(origin, run);
                order.Add(origin);
            }

            run.Add(token);
        }

        List<(string, TokenSource)> files = new(order.Count);

        foreach (SourceText origin in order)
            files.Add((origin.Path ?? string.Empty, TokenSource.FromExpanded(origin, byOrigin[origin])));

        return files;
    }
}
