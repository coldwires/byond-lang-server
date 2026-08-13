using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;

namespace Dm.Core.Preprocessing;

/// <summary>
/// The preprocessed form of a whole project.
/// </summary>
internal sealed class PreprocessResult
{
    private readonly RunCollector _runs;
    private IReadOnlyList<ExpandedToken>? _flattened;

    internal PreprocessResult(IncludeGraph graph, RunCollector runs)
    {
        Graph = graph;
        _runs = runs;
    }

    /// <summary>The files reached, in compile order.</summary>
    public IncludeGraph Graph { get; }

    /// <summary>
    /// One run per file, in compile order, with that file's tokens gathered.
    /// </summary>
    /// <remarks>
    /// What the parser reads. Gathered during the walk, which already knows which file it is in —
    /// regrouping the whole stream afterwards cost as much as parsing it.
    /// </remarks>
    public IReadOnlyList<PreprocessedFile> Runs => _runs.Files;

    /// <summary>
    /// Every code token in the project, in compile order, with conditionals resolved and macros
    /// expanded. Directive lines are gone; each token still knows where it came from.
    /// </summary>
    /// <remarks>
    /// The stream as the compiler sees it, interleaved across files — which <see cref="Runs"/> is
    /// not, since a file's tokens are interrupted by its includes. Rebuilt on demand from the order
    /// the runs were emitted in, so holding this view costs nothing until something asks for it.
    /// </remarks>
    public IReadOnlyList<ExpandedToken> Tokens => _flattened ??= _runs.Flatten();

    public IReadOnlyList<Diagnostic> Diagnostics => Graph.Diagnostics;

    /// <summary>Macro state at the end of the run.</summary>
    public MacroTable Macros => Graph.Macros;
}

/// <summary>
/// Runs a project through the preprocessor.
/// </summary>
/// <remarks>
/// This is one pass, not three. Includes cannot be collected without evaluating conditionals, and
/// conditionals cannot be evaluated without tracking macros — so the include walk, the macro table
/// and expansion all advance together in compile order. Splitting them would mean walking the
/// project repeatedly and getting the ordering wrong in between.
///
/// The output is what M4's parser consumes.
/// </remarks>
internal static class Preprocessor
{
    public static PreprocessResult Run(string dmePath, IncludeOptions? options = null)
    {
        (IncludeGraph graph, RunCollector runs) =
            IncludeGraph.BuildCore(dmePath, options, collectTokens: true);

        return new PreprocessResult(graph, runs);
    }
}
