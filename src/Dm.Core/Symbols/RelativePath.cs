using System;
using System.Collections.Generic;

namespace Dm.Core.Symbols;

/// <summary>
/// Resolves a leading-<c>.</c> path, which is a search rather than a traversal.
/// </summary>
/// <remarks>
/// <para>
/// The rule, compiler-verified against 516.1666 in every particular (PLAN.md §4a): walk the
/// enclosing type's path ancestors nearest-first, including root, and take the first one under
/// which the <b>entire</b> relative path resolves.
/// </para>
/// <para>
/// Three parts of that are easy to get wrong, and each was measured rather than assumed:
/// </para>
/// <list type="bullet">
/// <item><description>
/// The anchor is the <b>path</b> ancestry, not the inheritance chain. A type with
/// <c>parent_type = /a/inh</c> does not see <c>/a/inh</c>'s children through a leading <c>.</c>.
/// Using <see cref="ObjectTree.InheritanceChain"/> here would be wrong.
/// </description></item>
/// <item><description>
/// The whole path must resolve, and the search <b>backtracks</b>. Given <c>/x/sword/deep</c> and a
/// nearer <c>/x/magic/sword</c> with no <c>deep</c>, <c>.sword/deep</c> resolves to the far one:
/// matching the first segment is not enough to claim the candidate.
/// </description></item>
/// <item><description>
/// The walk includes root, so a root-level type is reachable from any depth — and a global proc,
/// whose anchor is root, therefore reaches only root's own children.
/// </description></item>
/// </list>
/// </remarks>
internal static class RelativePath
{
    /// <summary>
    /// Resolves <paramref name="segments"/> as a leading-<c>.</c> path from <paramref name="anchor"/>,
    /// or returns null when no ancestor resolves all of it.
    /// </summary>
    public static TypePath? Resolve(ObjectTree tree, TypePath anchor, IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(segments);

        if (segments.Count == 0)
            return null;

        TypePath current = anchor;

        while (true)
        {
            // The enclosing type's own children come first, then each ancestor's.
            TypePath candidate = current.Append(segments);

            if (tree.Find(candidate) is not null)
                return candidate;

            if (current.IsRoot)
                return null;

            current = current.Parent;
        }
    }
}
