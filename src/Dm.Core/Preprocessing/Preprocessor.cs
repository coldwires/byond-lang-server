using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;

namespace Dm.Core.Preprocessing;

/// <summary>
/// The preprocessed form of a whole project.
/// </summary>
public sealed class PreprocessResult
{
    internal PreprocessResult(IncludeGraph graph, IReadOnlyList<ExpandedToken> tokens)
    {
        Graph = graph;
        Tokens = tokens;
    }

    /// <summary>The files reached, in compile order.</summary>
    public IncludeGraph Graph { get; }

    /// <summary>
    /// Every code token in the project, in compile order, with conditionals resolved and macros
    /// expanded. Directive lines are gone; each token still knows where it came from.
    /// </summary>
    public IReadOnlyList<ExpandedToken> Tokens { get; }

    public IReadOnlyList<Diagnostic> Diagnostics => Graph.Diagnostics;
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
public static class Preprocessor
{
    public static PreprocessResult Run(string dmePath, IncludeOptions? options = null)
    {
        (IncludeGraph graph, List<ExpandedToken> tokens) =
            IncludeGraph.BuildCore(dmePath, options, collectTokens: true);

        return new PreprocessResult(graph, tokens);
    }
}
