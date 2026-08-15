using Dm.Core.Syntax;

namespace Dm.Core.Symbols;

/// <summary>
/// The type a declaration's own syntax gives it, before anything is inferred.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT in <see cref="Dm.Core.Binding.TypeInference"/>, which exists to go further
/// than the compiler. Everything here is a type <c>dm.exe</c> itself checks members through, so a
/// receiver typed from this is <b>written</b> rather than inferred: it takes no inlay hint and its
/// completion items are not marked <c>inferred</c>.
/// </para>
/// <para>
/// Shared because the rule had three copies — <c>TypeTreeBuilder</c> for type-level vars,
/// <c>Binder</c> for proc locals, and completion's own local walk, which never learned it at all
/// and so answered nothing for <c>players.</c> after <c>var/players[0]</c>. That gap was invisible
/// to every gate this project has: diagdiff never sees completion.
/// </para>
/// </remarks>
internal static class DeclaredType
{
    /// <summary>The type a bracketed declaration carries when it writes none of its own.</summary>
    internal static readonly TypePath ListPath = TypePath.Parse("/list");

    /// <summary>
    /// The declaration's written type, else <c>/list</c> when it carries brackets, else null.
    /// </summary>
    /// <remarks>
    /// <b>Brackets type a var, and the compiler agrees.</b> Probed against 516.1687 with a
    /// negative control: <c>var/players[0]</c> then <c>players.Add(1)</c> and <c>players.len</c>
    /// compile, while <c>players.nonexistent_xyz</c> is an error — so it is a checked type rather
    /// than an untyped var that happens to hold a list. mlaas writes exactly that shape throughout.
    /// Sized or not makes no difference, at type level and proc level alike.
    /// <para>
    /// A written type WINS over brackets, so <c>var/list/mob/L[]</c> stays what it says. A caller
    /// with a var-block header's inherited type applies it after this returns null, since a
    /// header types only the children that write nothing themselves.
    /// </para>
    /// </remarks>
    /// <param name="written">The declaration's own type path, if it wrote one.</param>
    /// <param name="hasBrackets">Whether the declaration carries <c>[]</c>, sized or not.</param>
    internal static TypePath? Of(PathSyntax? written, bool hasBrackets)
        => written is { Segments.Count: > 0 } path
            ? TypePath.FromSegments(path.Segments)
            : hasBrackets
                ? ListPath
                : null;
}
