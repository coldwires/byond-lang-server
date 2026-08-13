using System.Collections.Generic;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

internal abstract class ExpressionSyntax : SyntaxNode
{
    protected ExpressionSyntax(TextSpan span) : base(span)
    {
    }
}

internal enum LiteralKind
{
    Number,
    String,
    Resource,
    Null,
}

/// <summary>A number, a string with no interpolation, a resource literal, or <c>null</c>.</summary>
internal sealed class LiteralExpressionSyntax : ExpressionSyntax
{
    public LiteralExpressionSyntax(LiteralKind kind, string text, TextSpan span) : base(span)
    {
        Kind = kind;
        Text = text;
    }

    public LiteralKind Kind { get; }

    /// <summary>Source text as written, delimiters included. Escapes are not decoded here.</summary>
    public string Text { get; }

    public override string ToString() => Text;
}

/// <summary>A bare name, including <c>src</c>, <c>usr</c>, <c>world</c> and <c>global</c>.</summary>
internal sealed class IdentifierExpressionSyntax : ExpressionSyntax
{
    public IdentifierExpressionSyntax(string name, TextSpan span) : base(span) => Name = name;

    public string Name { get; }

    public override string ToString() => Name;
}

/// <summary>A path used as a value, such as <c>/obj/item</c> or the relative <c>.Village/Guard</c>.</summary>
/// <remarks>
/// Only paths with a leading separator get here. A bare <c>a.b</c> is member access, not a path —
/// PLAN.md §4a, "no leading separator means it is not a path at all".
/// </remarks>
internal sealed class PathExpressionSyntax : ExpressionSyntax
{
    public PathExpressionSyntax(PathSyntax path) : base(path.Span) => Path = path;

    public PathSyntax Path { get; }

    public override string ToString() => Path.Text;
}

/// <summary>The bare <c>.</c>, which is the implicit return value of the enclosing proc.</summary>
internal sealed class ReturnValueExpressionSyntax : ExpressionSyntax
{
    public ReturnValueExpressionSyntax(TextSpan span) : base(span)
    {
    }

    public override string ToString() => ".";
}

internal enum MemberAccessKind
{
    /// <summary><c>.</c> — checked against the declared type.</summary>
    Dot,

    /// <summary><c>:</c> — checked against the declared type and its subtypes.</summary>
    Colon,

    /// <summary><c>?.</c></summary>
    NullDot,

    /// <summary><c>?:</c></summary>
    NullColon,

    /// <summary><c>::</c></summary>
    Scope,
}

/// <summary>Member access. <c>.</c> and <c>:</c> stay distinct nodes — they check different things.</summary>
/// <remarks>
/// <see cref="Target"/> is null for the leading forms <c>::A</c> and <c>::A()</c>.
/// </remarks>
internal sealed class MemberAccessExpressionSyntax : ExpressionSyntax
{
    public MemberAccessExpressionSyntax(
        ExpressionSyntax? target,
        MemberAccessKind kind,
        string name,
        TextSpan nameSpan,
        bool isProcReference,
        TextSpan span)
        : base(span)
    {
        Target = target;
        Kind = kind;
        Name = name;
        NameSpan = nameSpan;
        IsProcReference = isProcReference;
    }

    public ExpressionSyntax? Target { get; }

    public MemberAccessKind Kind { get; }

    public string Name { get; }

    /// <summary>Span of the member name alone, for go-to-definition and rename.</summary>
    public TextSpan NameSpan { get; }

    /// <summary>
    /// True for the <c>A::B()</c> and <c>::A()</c> forms, which name a proc rather than calling it.
    /// The trailing parentheses are part of the reference, so this is not an invocation.
    /// </summary>
    public bool IsProcReference { get; }
}

/// <summary>An index expression, <c>L[i]</c> or the null-conditional <c>L?[i]</c>.</summary>
internal sealed class IndexExpressionSyntax : ExpressionSyntax
{
    public IndexExpressionSyntax(ExpressionSyntax target, ExpressionSyntax? index, bool isNullConditional, TextSpan span)
        : base(span)
    {
        Target = target;
        Index = index;
        IsNullConditional = isNullConditional;
    }

    public ExpressionSyntax Target { get; }

    /// <summary>Null when the brackets are empty, which is legal in a declaration position.</summary>
    public ExpressionSyntax? Index { get; }

    public bool IsNullConditional { get; }
}

/// <summary>
/// One argument. DM allows an associative form, <c>list(a = 1, b = 2)</c>, where the name is a key
/// rather than a parameter name, and a weighted form, <c>pick(20;"brown", 1;"albino")</c>.
/// </summary>
internal sealed class ArgumentSyntax : SyntaxNode
{
    public ArgumentSyntax(
        ExpressionSyntax? name,
        ExpressionSyntax value,
        TextSpan span,
        ExpressionSyntax? weight = null)
        : base(span)
    {
        Name = name;
        Value = value;
        Weight = weight;
    }

    /// <summary>The left side of an <c>=</c> inside an argument list, or null for a plain argument.</summary>
    public ExpressionSyntax? Name { get; }

    public ExpressionSyntax Value { get; }

    /// <summary>
    /// The left side of a <c>;</c> inside an argument, as <c>pick()</c> takes: null for everything
    /// else.
    /// </summary>
    /// <remarks>
    /// The separator is a semicolon rather than a comma because a comma already separates the
    /// arguments, so <c>pick(20;"brown", 1;"albino")</c> is two weighted choices and not four
    /// arguments. Kept as its own slot rather than folded into the value, so a consumer counting
    /// arguments still counts two.
    /// </remarks>
    public ExpressionSyntax? Weight { get; }
}

internal sealed class InvocationExpressionSyntax : ExpressionSyntax
{
    public InvocationExpressionSyntax(ExpressionSyntax target, IReadOnlyList<ArgumentSyntax> arguments, TextSpan span)
        : base(span)
    {
        Target = target;
        Arguments = arguments;
    }

    public ExpressionSyntax Target { get; }

    public IReadOnlyList<ArgumentSyntax> Arguments { get; }
}

/// <summary>
/// A call to the parent implementation, <c>..()</c>.
/// </summary>
/// <remarks>
/// Empty parentheses forward the current arguments rather than passing none — verified against
/// dm.exe 516.1666, see PLAN.md §8. <see cref="Arguments"/> being empty therefore does not mean the
/// parent is called with nothing.
/// </remarks>
internal sealed class ParentCallExpressionSyntax : ExpressionSyntax
{
    public ParentCallExpressionSyntax(IReadOnlyList<ArgumentSyntax> arguments, TextSpan span) : base(span)
        => Arguments = arguments;

    public IReadOnlyList<ArgumentSyntax> Arguments { get; }
}

internal enum UnaryOperatorKind
{
    Not,
    Negate,
    BitwiseNot,
    PreIncrement,
    PreDecrement,
    PostIncrement,
    PostDecrement,

    /// <summary>Unary <c>*</c>, a pointer dereference (515+). Binds at level 4, not level 6.</summary>
    Dereference,

    /// <summary>Unary <c>&amp;</c>, taking a reference. Binds at level 4, not level 11.</summary>
    AddressOf,
}

internal sealed class UnaryExpressionSyntax : ExpressionSyntax
{
    public UnaryExpressionSyntax(UnaryOperatorKind kind, ExpressionSyntax operand, TextSpan span) : base(span)
    {
        Kind = kind;
        Operand = operand;
    }

    public UnaryOperatorKind Kind { get; }

    public ExpressionSyntax Operand { get; }

    public bool IsPostfix => Kind is UnaryOperatorKind.PostIncrement or UnaryOperatorKind.PostDecrement;
}

/// <summary>A binary operator application. <c>in</c> is one of these, at the lowest precedence.</summary>
internal sealed class BinaryExpressionSyntax : ExpressionSyntax
{
    public BinaryExpressionSyntax(ExpressionSyntax left, TokenKind operatorToken, ExpressionSyntax right, TextSpan span)
        : base(span)
    {
        Left = left;
        OperatorToken = operatorToken;
        Right = right;
    }

    public ExpressionSyntax Left { get; }

    /// <summary>The operator as lexed. <see cref="TokenKind.KeywordIn"/> for <c>in</c>.</summary>
    public TokenKind OperatorToken { get; }

    public ExpressionSyntax Right { get; }
}

/// <summary>
/// An assignment. Kept separate from <see cref="BinaryExpressionSyntax"/> because it is
/// right-associative and because its left side is a target rather than a value.
/// </summary>
internal sealed class AssignmentExpressionSyntax : ExpressionSyntax
{
    public AssignmentExpressionSyntax(
        ExpressionSyntax target,
        TokenKind operatorToken,
        ExpressionSyntax value,
        TextSpan span)
        : base(span)
    {
        Target = target;
        OperatorToken = operatorToken;
        Value = value;
    }

    public ExpressionSyntax Target { get; }

    public TokenKind OperatorToken { get; }

    public ExpressionSyntax Value { get; }
}

internal sealed class ConditionalExpressionSyntax : ExpressionSyntax
{
    public ConditionalExpressionSyntax(
        ExpressionSyntax condition,
        ExpressionSyntax whenTrue,
        ExpressionSyntax whenFalse,
        TextSpan span)
        : base(span)
    {
        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
    }

    public ExpressionSyntax Condition { get; }

    public ExpressionSyntax WhenTrue { get; }

    public ExpressionSyntax WhenFalse { get; }
}

/// <summary>A <c>new</c> expression, with or without a type and with or without arguments.</summary>
/// <remarks>
/// <see cref="Type"/> is null for the bare <c>new</c> used when the target's type is already known,
/// as in <c>var/list/L = new</c>. It can also be a <see cref="ModifiedTypeExpressionSyntax"/>.
/// </remarks>
internal sealed class NewExpressionSyntax : ExpressionSyntax
{
    public NewExpressionSyntax(ExpressionSyntax? type, IReadOnlyList<ArgumentSyntax> arguments, TextSpan span)
        : base(span)
    {
        Type = type;
        Arguments = arguments;
    }

    public ExpressionSyntax? Type { get; }

    public IReadOnlyList<ArgumentSyntax> Arguments { get; }
}

/// <summary>
/// A modified-type initialiser, <c>/obj/thing{hp = 42; label = "set"}</c>.
/// </summary>
/// <remarks>
/// Legal anywhere a type value is. The braces are mandatory here even though braces are optional
/// elsewhere in DM, and <c>;</c> separates entries written on one line. Verified against dm.exe
/// 516.1666, see PLAN.md §8.
/// </remarks>
internal sealed class ModifiedTypeExpressionSyntax : ExpressionSyntax
{
    public ModifiedTypeExpressionSyntax(
        ExpressionSyntax type,
        IReadOnlyList<ExpressionSyntax> assignments,
        TextSpan span)
        : base(span)
    {
        Type = type;
        Assignments = assignments;
    }

    public ExpressionSyntax Type { get; }

    /// <summary>The <c>name = value</c> entries between the braces.</summary>
    public IReadOnlyList<ExpressionSyntax> Assignments { get; }
}

/// <summary>One piece of an interpolated string: either literal text or an embedded expression.</summary>
internal sealed class InterpolatedStringPartSyntax : SyntaxNode
{
    public InterpolatedStringPartSyntax(string? text, ExpressionSyntax? expression, TextSpan span) : base(span)
    {
        Text = text;
        Expression = expression;
    }

    /// <summary>Literal text, or null when this part is a hole.</summary>
    public string? Text { get; }

    /// <summary>The expression inside <c>[ ]</c>, or null when this part is literal text.</summary>
    public ExpressionSyntax? Expression { get; }
}

/// <summary>A string containing at least one <c>[expression]</c> hole.</summary>
internal sealed class InterpolatedStringExpressionSyntax : ExpressionSyntax
{
    public InterpolatedStringExpressionSyntax(IReadOnlyList<InterpolatedStringPartSyntax> parts, TextSpan span)
        : base(span)
        => Parts = parts;

    public IReadOnlyList<InterpolatedStringPartSyntax> Parts { get; }
}

/// <summary>
/// An <c>as</c> clause, as in <c>input("pick") as text|null</c>.
/// </summary>
/// <remarks>
/// The clause constrains what the call accepts, and on a proc declaration it also gives the return
/// type the binder needs — <c>.</c> degrades to <c>:</c> without one, see PLAN.md §4a.
/// </remarks>
internal sealed class AsExpressionSyntax : ExpressionSyntax
{
    public AsExpressionSyntax(ExpressionSyntax expression, IReadOnlyList<string> inputTypes, TextSpan span)
        : base(span)
    {
        Expression = expression;
        InputTypes = inputTypes;
    }

    public ExpressionSyntax Expression { get; }

    /// <summary>The names between <c>as</c> and the end of the clause, split on <c>|</c>.</summary>
    public IReadOnlyList<string> InputTypes { get; }
}

/// <summary>Stands in for an expression that could not be parsed, so recovery has something to return.</summary>
internal sealed class ErrorExpressionSyntax : ExpressionSyntax
{
    public ErrorExpressionSyntax(TextSpan span) : base(span)
    {
    }
}
