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

    /// <summary>
    /// After <c>as</c> — the input-type filters a verb argument accepts.
    /// </summary>
    /// <remarks>
    /// A closed vocabulary rather than anything from the tree, so the list is exact. It is NOT the
    /// type system: <c>as datum</c>, <c>as list</c> and <c>as client</c> are all rejected by the
    /// compiler while <c>as movable</c> and <c>as atom</c> are accepted, which no rule about types
    /// predicts. See <see cref="SyntaxFacts.InputTypes"/>.
    /// </remarks>
    InputType = 6,

    /// <summary>
    /// A bare leading <c>.</c> — DM's return-value variable, not member access.
    /// </summary>
    /// <remarks>
    /// The list is empty: the variable is untyped and offering the whole identifier list after a
    /// typed <c>.</c> is noise every client would have to suppress by guessing. A distinct value
    /// moves the decision to where the knowledge is (dm-patch's §4).
    /// </remarks>
    ReturnValue = 5,
}

/// <summary>
/// How a receiver's type was arrived at, which is what <c>inferred</c> summarises.
/// </summary>
/// <remarks>
/// <para>
/// <c>inferred</c> answers "would dm.exe refuse this", which is the fact a client needs to decide
/// whether to badge an item. It does NOT answer "did the author say so", and the two come apart in
/// exactly one place: an <c>as</c> clause is <b>written down</b> and still not a type the compiler
/// checks members through. A client rendering "inferred" over `f(n as num)` is telling the author
/// their own words were a guess.
/// </para>
/// <para>
/// So the flag stays — renaming a field shipped at 0.14 would break a consumer for a wording
/// problem — and this says which of the four routes produced the type.
/// </para>
/// </remarks>
public enum TypeSource
{
    /// <summary>Nothing resolved the type.</summary>
    None = 0,

    /// <summary>A declared type: <c>var/mob/M</c>. The only one dm.exe checks.</summary>
    Written = 1,

    /// <summary>An untyped local's initialiser: <c>var/x = new /obj/item</c>.</summary>
    Initializer = 2,

    /// <summary>The nearest assignment before the cursor: <c>x = new /obj/item</c>.</summary>
    Assignment = 3,

    /// <summary>A parameter's <c>as</c> clause: <c>f(n as num)</c>. Written, and still unchecked.</summary>
    InputFilter = 4,

    /// <summary>
    /// A bare type name used as a receiver: <c>mob.</c>.
    /// </summary>
    /// <remarks>
    /// The one route where NO edit makes the expression legal. <c>mob.loc</c> is "undefined var" —
    /// a bare <c>mob</c> is neither a variable nor a path, since §4a needs a leading separator for
    /// that — so unlike an untyped local, which becomes valid the moment a type is written, this
    /// cannot compile in any form. Offered anyway because exploring a type's members by name is
    /// useful and PLAN §1 asks for it, and marked so a client can say so.
    /// </remarks>
    BareTypeName = 5,
}

/// <summary>One entry in a completion list.</summary>
public sealed class CompletionItem
{
    public CompletionItem(
        string name, CompletionKind kind, string detail, bool isBuiltin, string documentation = "",
        bool inferred = false, int rank = 0, string declaredType = "", string initialValue = "",
        TypeSource typeSource = TypeSource.None)
    {
        Name = name;
        Kind = kind;
        Detail = detail;
        IsBuiltin = isBuiltin;
        Documentation = documentation;
        Inferred = inferred;
        Rank = rank;
        DeclaredType = declaredType;
        InitialValue = initialValue;
        TypeSource = typeSource;
    }

    /// <summary>
    /// Scope distance, lowest first: how near this name was declared to the position asking.
    /// </summary>
    /// <remarks>
    /// The list is returned in this order, so the order IS the ranking and a client needs nothing
    /// else. Not exposed across the ABI for that reason — a number invites a client to re-sort by
    /// it and disagree with us.
    /// </remarks>
    internal int Rank { get; }

    public string Name { get; }

    public CompletionKind Kind { get; }

    /// <summary>Where it came from, or its signature. Empty when there is nothing useful to add.</summary>
    public string Detail { get; }

    /// <summary>True for a BYOND builtin rather than something the project declared.</summary>
    public bool IsBuiltin { get; }

    /// <summary>
    /// The <c>///</c> comment above the declaration, or empty.
    /// </summary>
    /// <remarks>
    /// Only populated when the caller supplied a way to read other files — the text lives wherever
    /// the member was declared, which is rarely the file being completed in.
    /// </remarks>
    public string Documentation { get; }

    /// <summary>
    /// True when this item rides on type inference <c>dm.exe</c> does not do — the receiver's type
    /// was worked out from a <c>new</c>, an <c>as</c> clause or an assignment rather than written
    /// down, so accepting the item can produce code that does not compile.
    /// </summary>
    /// <remarks>
    /// The compiler checks only a written type (PLAN.md §6, the one deliberate divergence).
    /// Clients had been guessing this from the trigger context; the flag replaces the guess.
    /// </remarks>
    public bool Inferred { get; }

    /// <summary>
    /// The item's own declared type — <c>/mob</c> for <c>var/mob/M</c> — or empty when it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the type OF THE ITEM, not of the receiver the list came from, so it is what a client
    /// renders beside the name. Empty is the honest answer for an untyped var and is not rare:
    /// <c>var/fatigue = 6</c> has no declared type at all. DM has no <c>num</c> or <c>text</c> to
    /// name — an initialiser does not type a variable (§8), so anything a client shows for those
    /// comes from <see cref="InitialValue"/> rather than from here.
    /// </para>
    /// <para>
    /// A parameter's <c>as</c> clause is deliberately not folded in. <c>f(n as num)</c> leaves `n`
    /// untyped as far as <c>dm.exe</c> is concerned, so reporting <c>num</c> here would claim a
    /// type the compiler does not hold; the clause is in <see cref="Detail"/>, where it reads as
    /// what it is.
    /// </para>
    /// </remarks>
    public string DeclaredType { get; }

    /// <summary>
    /// The initialiser as written — <c>6</c> for <c>var/fatigue = 6</c> — or empty when there is none.
    /// </summary>
    /// <remarks>
    /// Source text, not an evaluated value: <c>5 + 1</c> stays <c>5 + 1</c>. A constant evaluator
    /// would fold it, and until there is one this is the author's text rather than a claim about
    /// what it comes to. For a <c>const</c> it is most of what a reader wants.
    /// </remarks>
    public string InitialValue { get; }

    /// <summary>
    /// Which route produced the RECEIVER's type, when this item came from a member list.
    /// </summary>
    /// <remarks>
    /// The detail behind <see cref="Inferred"/>, for a client that wants to say why rather than
    /// just that. The pair comes apart on <see cref="Services.TypeSource.InputFilter"/>: the author
    /// WROTE <c>as num</c> and dm.exe still refuses members through it, so "inferred" is the right
    /// warning and the wrong word.
    /// </remarks>
    public TypeSource TypeSource { get; }

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>A completion list plus the context that produced it.</summary>
public sealed class CompletionResult
{
    public CompletionResult(
        CompletionContext context,
        System.Collections.Generic.IReadOnlyList<CompletionItem> items,
        bool truncated = false)
    {
        Context = context;
        Items = items;
        Truncated = truncated;
    }

    public CompletionContext Context { get; }

    /// <summary>
    /// Ordered by scope distance, nearest first: locals, parameters, members of the enclosing
    /// type, globals, macros, and BYOND's builtins last.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<CompletionItem> Items { get; }

    /// <summary>
    /// True when a caller-supplied limit cut the list.
    /// </summary>
    /// <remarks>
    /// Reported rather than inferred, the same as <c>subtypesOf</c> and <c>references</c>: a list
    /// exactly as long as the limit is indistinguishable from one that was cut. It also decides
    /// whether a client may filter locally — over a truncated list, local filtering silently omits
    /// the item the user is typing toward.
    /// </remarks>
    public bool Truncated { get; }
}
