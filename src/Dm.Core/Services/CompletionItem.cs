namespace Dm.Core.Services;

/// <summary>What a completion item is, so a client can pick an icon.</summary>
public enum CompletionKind
{
    /// <summary>A type path.</summary>
    Type = 0,

    /// <summary>A var on a type, or a global.</summary>
    Variable = 1,

    Proc = 2,

    /// <summary>A verb, which a player can invoke directly.</summary>
    Verb = 3,

    /// <summary>A parameter of the enclosing proc.</summary>
    Parameter = 4,

    /// <summary>A local declared earlier in the enclosing proc.</summary>
    Local = 5,

    /// <summary>A preprocessor macro.</summary>
    Macro = 6,

    /// <summary>A keyword.</summary>
    Keyword = 7,
}

/// <summary>Why a completion list contains what it does, which is worth surfacing while debugging.</summary>
public enum CompletionContext
{
    /// <summary>No useful context; the list is empty.</summary>
    None = 0,

    /// <summary>A bare identifier: locals, parameters, <c>src</c> members and globals.</summary>
    Identifier = 1,

    /// <summary>After <c>.</c> on a value — members of the declared type only.</summary>
    Member = 2,

    /// <summary>
    /// After <c>:</c> — members of the declared type <b>and its subtypes</b>.
    /// </summary>
    /// <remarks>
    /// Not "everything". <c>:</c> widens the check rather than removing it: reaching a member of an
    /// unrelated type is still a compile error. See PLAN.md §4a.
    /// </remarks>
    SubtypeMember = 3,

    /// <summary>Mid-path after <c>/</c> — child type paths.</summary>
    TypePath = 4,
}

/// <summary>One entry in a completion list.</summary>
public sealed class CompletionItem
{
    public CompletionItem(string name, CompletionKind kind, string detail, bool isBuiltin)
    {
        Name = name;
        Kind = kind;
        Detail = detail;
        IsBuiltin = isBuiltin;
    }

    public string Name { get; }

    public CompletionKind Kind { get; }

    /// <summary>Where it came from, or its signature. Empty when there is nothing useful to add.</summary>
    public string Detail { get; }

    /// <summary>True for a BYOND builtin rather than something the project declared.</summary>
    public bool IsBuiltin { get; }

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>A completion list plus the context that produced it.</summary>
public sealed class CompletionResult
{
    public CompletionResult(CompletionContext context, System.Collections.Generic.IReadOnlyList<CompletionItem> items)
    {
        Context = context;
        Items = items;
    }

    public CompletionContext Context { get; }

    public System.Collections.Generic.IReadOnlyList<CompletionItem> Items { get; }
}
