using System;
using System.Collections.Generic;
using Dm.Core.Symbols;
using Dm.Core.Syntax;

namespace Dm.Core.Binding;

/// <summary>
/// Works out the type of an expression that does not carry one in its declaration.
/// </summary>
/// <remarks>
/// <para>
/// <b>This deliberately goes further than the compiler.</b> dm.exe performs no local type
/// inference at all: <c>var/x = new /obj/item</c> followed by <c>x.hp</c> is
/// <i>"x.hp: undefined var"</i>, even though <c>hp</c> is the right member of the type on the
/// same line. Only a written type — <c>var/obj/item/x</c> — is ever checked. Verified against
/// 516.1666; the table is in PLAN.md §8.
/// </para>
/// <para>
/// So everything here answers "what did the author almost certainly mean", not "what will
/// compile". A client offering these completions will offer members that dm.exe then rejects.
/// That is a deliberate product decision — the common case is a half-written declaration the
/// author is about to type a type into — and it is called out at the ABI boundary so an
/// integrator is not surprised by it. PLAN.md §6 records the trade.
/// </para>
/// <para>
/// Nothing here invents a type it cannot see written down somewhere. Inference walks back to a
/// <c>new /path</c>, an <c>as</c> clause, or another declaration that already had a type, and
/// gives up otherwise rather than guessing.
/// </para>
/// </remarks>
public static class TypeInference
{
    /// <summary>
    /// The <c>as</c> input types that name a real type in the tree.
    /// </summary>
    /// <remarks>
    /// The rest of the <c>as</c> vocabulary — <c>text</c>, <c>num</c>, <c>message</c>, <c>key</c>,
    /// <c>color</c>, <c>null</c>, <c>anything</c> — describes a value rather than an object, so
    /// there is nothing to resolve a member against.
    /// </remarks>
    private static readonly Dictionary<string, string> InputTypePaths = new(StringComparer.Ordinal)
    {
        ["mob"] = "/mob",
        ["obj"] = "/obj",
        ["turf"] = "/turf",
        ["area"] = "/area",
        ["atom"] = "/atom",
        ["movable"] = "/atom/movable",
        ["icon"] = "/icon",
        ["sound"] = "/sound",
        ["file"] = "/file",
    };

    /// <summary>
    /// The type an expression produces, or null when nothing in reach says what it is.
    /// </summary>
    /// <param name="expression">The expression to read a type off, or null.</param>
    /// <param name="lookup">
    /// Resolves a bare name to its declared type. Supplied by the caller because the scope chain
    /// lives with the service asking the question, not here.
    /// </param>
    public static TypePath? Infer(ExpressionSyntax? expression, Func<string, TypePath?>? lookup = null)
    {
        switch (expression)
        {
            case null:
                return null;

            // `new /obj/item`, with or without arguments.
            case NewExpressionSyntax { Type: { } created }:
                return FromTypeExpression(created);

            // `new` on its own carries no type of its own; the declaration it initialises does.
            case NewExpressionSyntax:
                return null;

            // `input("pick") as mob`.
            case AsExpressionSyntax clause:
                return FromInputTypes(clause.InputTypes);

            // A path written as a value. This is the type itself, not an instance of it, but it is
            // what a member lookup should resolve against either way.
            case PathExpressionSyntax path when path.Path.Anchor == PathAnchor.Absolute:
                return TypePath.FromSegments(path.Path.Segments);

            // `/obj/thing{hp = 42}` is still an /obj/thing.
            case ModifiedTypeExpressionSyntax modified:
                return FromTypeExpression(modified.Type);

            // Assignment is an expression in DM, so `x = new /obj/item` used as a value has the
            // type of what was assigned.
            case AssignmentExpressionSyntax assignment when assignment.OperatorToken == TokenKind.Assign:
                return Infer(assignment.Value, lookup);

            // Another name that already had a type.
            case IdentifierExpressionSyntax identifier:
                return lookup?.Invoke(identifier.Name);

            // A parenthesised or otherwise wrapped value keeps its type.
            case ConditionalExpressionSyntax conditional:
            {
                // Both branches have to agree, or we know nothing.
                TypePath? whenTrue = Infer(conditional.WhenTrue, lookup);
                TypePath? whenFalse = Infer(conditional.WhenFalse, lookup);

                return whenTrue is { } a && whenFalse is { } b && a == b ? a : null;
            }

            default:
                return null;
        }
    }

    /// <summary>Reads a type out of the expression in a <c>new</c>, which may itself be modified.</summary>
    private static TypePath? FromTypeExpression(ExpressionSyntax type) => type switch
    {
        PathExpressionSyntax path => TypePath.FromSegments(path.Path.Segments),
        ModifiedTypeExpressionSyntax modified => FromTypeExpression(modified.Type),

        // `new mob/test(...)` — a path with no leading separator parses as an identifier chain.
        IdentifierExpressionSyntax identifier => TypePath.Root.Append(identifier.Name),
        _ => null,
    };

    /// <summary>
    /// Maps an <c>as</c> clause written as one string, such as a parameter's <c>mob|null</c>.
    /// </summary>
    public static TypePath? FromInputType(string? inputType)
        => string.IsNullOrEmpty(inputType) ? null : FromInputTypes(inputType.Split('|'));

    /// <summary>
    /// Maps an <c>as</c> clause to a type.
    /// </summary>
    /// <remarks>
    /// <c>as mob|null</c> is still a mob as far as a member lookup is concerned, so <c>null</c> is
    /// skipped. Two different object types in one clause mean the value is genuinely either, and
    /// there is no single answer to give.
    /// </remarks>
    private static TypePath? FromInputTypes(IReadOnlyList<string> inputTypes)
    {
        TypePath? found = null;

        foreach (string inputType in inputTypes)
        {
            if (string.Equals(inputType, "null", StringComparison.Ordinal))
                continue;

            if (!InputTypePaths.TryGetValue(inputType, out string? path))
                continue;

            TypePath candidate = TypePath.Parse(path);

            if (found is { } already && already != candidate)
                return null;

            found = candidate;
        }

        return found;
    }
}
