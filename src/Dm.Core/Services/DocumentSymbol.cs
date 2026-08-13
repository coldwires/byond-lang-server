using System.Collections.Generic;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>
/// What a symbol is. Values are a permanent contract across the ABI and are never reused.
/// </summary>
/// <remarks>
/// These map onto LSP's <c>SymbolKind</c> at M10, but they are DM's categories rather than LSP's:
/// DM has no classes or methods, and a <c>verb</c> is worth telling apart from a <c>proc</c>
/// because it is player-invocable.
/// </remarks>
public enum SymbolKind
{
    /// <summary>A node in the type tree, such as <c>/obj/item</c>.</summary>
    Type = 0,

    /// <summary>A <c>var</c>, at type level or global.</summary>
    Variable = 1,

    /// <summary>A proc.</summary>
    Proc = 2,

    /// <summary>A verb, which a player can invoke directly.</summary>
    Verb = 3,

    /// <summary>A proc parameter.</summary>
    Parameter = 4,
}

/// <summary>
/// One entry in a file's outline, with its children.
/// </summary>
/// <remarks>
/// A value type by design: a service that handed back live <see cref="Syntax.SyntaxNode"/>s could
/// not be serialised over the ABI or LSP. The C# IDE can still reach past this into
/// <c>Dm.Core</c> for the tree itself.
/// </remarks>
public sealed class DocumentSymbol
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public DocumentSymbol(
        string name,
        string detail,
        SymbolKind kind,
        LinePosition start,
        LinePosition end,
        LinePosition selectionStart,
        LinePosition selectionEnd,
        IReadOnlyList<DocumentSymbol> children,
        string owner = "")
    {
        Name = name;
        Detail = detail;
        Kind = kind;
        Start = start;
        End = end;
        SelectionStart = selectionStart;
        SelectionEnd = selectionEnd;
        Children = children;
        Owner = owner;
    }

    /// <summary>The name as the outline shows it, without any path.</summary>
    public string Name { get; }

    /// <summary>
    /// A short annotation for the outline: the declared type of a var, or a proc's parameter list.
    /// Empty when there is nothing useful to add.
    /// </summary>
    public string Detail { get; }

    /// <summary>
    /// The resolved path of whatever contains this symbol: a member's owning type, a type's parent
    /// by path, a parameter's proc as <c>/mob/heal()</c>. <c>/</c> for anything at the root.
    /// </summary>
    /// <remarks>
    /// Resolved with the tree builder's own owner rules — a one-line <c>/mob/TEA()</c> puts
    /// <c>TEA</c> on <c>/mob</c>, a <c>mob/proc</c> group header carries its path, a typed var in a
    /// <c>var</c> block belongs to the enclosing type while a bare override's leading segments are
    /// the owner. Clients had been reconstructing this by string-slicing hover details, which is
    /// exactly the kind of fact that should exist in one place.
    /// </remarks>
    public string Owner { get; }

    /// <summary>What the symbol is, so a client can pick an icon.</summary>
    public SymbolKind Kind { get; }

    /// <summary>Start of the whole declaration, including any members beneath it.</summary>
    public LinePosition Start { get; }

    /// <summary>End of the whole declaration, pairing with <see cref="Start"/>.</summary>
    public LinePosition End { get; }

    /// <summary>
    /// Start of the name alone. This is what an editor highlights when the outline is used to
    /// navigate, and what rename would replace.
    /// </summary>
    public LinePosition SelectionStart { get; }

    /// <summary>End of the name alone, closing the range <see cref="SelectionStart"/> opens.</summary>
    public LinePosition SelectionEnd { get; }

    /// <summary>Nested symbols, in source order. Empty for a leaf.</summary>
    public IReadOnlyList<DocumentSymbol> Children { get; }

    /// <summary>Debug rendering: kind and name.</summary>
    public override string ToString() => $"{Kind} {Name}";
}
