using System.Collections.Generic;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

public abstract class StatementSyntax : SyntaxNode
{
    protected StatementSyntax(TextSpan span) : base(span)
    {
    }
}

/// <summary>A group of statements, from an indented block, a brace block, or one inline statement.</summary>
public sealed class BlockStatementSyntax : StatementSyntax
{
    public BlockStatementSyntax(IReadOnlyList<StatementSyntax> statements, TextSpan span) : base(span)
        => Statements = statements;

    public IReadOnlyList<StatementSyntax> Statements { get; }
}

/// <summary>An expression evaluated for its effect, including calls and assignments.</summary>
public sealed class ExpressionStatementSyntax : StatementSyntax
{
    public ExpressionStatementSyntax(ExpressionSyntax expression, TextSpan span) : base(span)
        => Expression = expression;

    public ExpressionSyntax Expression { get; }
}

/// <summary>A local <c>var</c> declaration inside a proc body.</summary>
public sealed class LocalVarStatementSyntax : StatementSyntax
{
    public LocalVarStatementSyntax(
        string name,
        TextSpan nameSpan,
        PathSyntax? declaredType,
        IReadOnlyList<string> modifiers,
        ExpressionSyntax? initializer,
        IReadOnlyList<LocalVarStatementSyntax> siblings,
        TextSpan span,
        IReadOnlyList<ExpressionSyntax>? dimensions = null)
        : base(span)
    {
        Name = name;
        NameSpan = nameSpan;
        DeclaredType = declaredType;
        Modifiers = modifiers;
        Initializer = initializer;
        Siblings = siblings;
        Dimensions = dimensions ?? System.Array.Empty<ExpressionSyntax>();
    }

    public string Name { get; }

    public TextSpan NameSpan { get; }

    /// <summary>The declared type, so <c>var/mob/test/t</c> gives <c>/mob/test</c>.</summary>
    public PathSyntax? DeclaredType { get; }

    public IReadOnlyList<string> Modifiers { get; }

    public ExpressionSyntax? Initializer { get; }

    /// <summary>Further names from the same <c>var/</c>, as in <c>var/a = 1, b = 2</c>.</summary>
    public IReadOnlyList<LocalVarStatementSyntax> Siblings { get; }

    /// <summary>
    /// The sizes of a bracket declaration — <c>var/M[10]</c>, <c>var/grid[10][5]</c>. Empty for
    /// <c>var/L[]</c>, which states no size, and for a declaration with no brackets at all.
    /// These are ordinary expressions and routinely read variables
    /// (<c>var/list/tier_list[max_tier]</c>); the parser consumed and discarded them until
    /// 2026-08-12, which hid those reads from the binder and the reference index alike.
    /// </summary>
    public IReadOnlyList<ExpressionSyntax> Dimensions { get; }
}

public sealed class IfStatementSyntax : StatementSyntax
{
    public IfStatementSyntax(
        ExpressionSyntax condition,
        StatementSyntax? then,
        StatementSyntax? otherwise,
        TextSpan span)
        : base(span)
    {
        Condition = condition;
        Then = then;
        Otherwise = otherwise;
    }

    public ExpressionSyntax Condition { get; }

    public StatementSyntax? Then { get; }

    /// <summary>The <c>else</c> branch, itself an <see cref="IfStatementSyntax"/> for <c>else if</c>.</summary>
    public StatementSyntax? Otherwise { get; }
}

public sealed class WhileStatementSyntax : StatementSyntax
{
    public WhileStatementSyntax(ExpressionSyntax condition, StatementSyntax? body, TextSpan span) : base(span)
    {
        Condition = condition;
        Body = body;
    }

    public ExpressionSyntax Condition { get; }

    public StatementSyntax? Body { get; }
}

public sealed class DoWhileStatementSyntax : StatementSyntax
{
    public DoWhileStatementSyntax(StatementSyntax? body, ExpressionSyntax? condition, TextSpan span) : base(span)
    {
        Body = body;
        Condition = condition;
    }

    public StatementSyntax? Body { get; }

    public ExpressionSyntax? Condition { get; }
}

/// <summary>Which of DM's four <c>for</c> shapes a header used.</summary>
public enum ForKind
{
    /// <summary>Clause form: <c>for(var/i = 1, i &lt;= 5, i++)</c>, or the same with semicolons.</summary>
    Clauses,

    /// <summary><c>for(var/x in L)</c>, including the 516 <c>for(var/k, v in assoc)</c> form.</summary>
    In,

    /// <summary><c>for(var/i = 1 to 10 step 2)</c>.</summary>
    Range,

    /// <summary>
    /// <c>for(var/obj/O)</c> with no clause at all, which iterates the world's contents.
    /// </summary>
    /// <remarks>
    /// Identical to <c>for(var/obj/O in world)</c>, so anything in nullspace is invisible to it —
    /// compiler-verified, PLAN.md §8.
    /// </remarks>
    Bare,
}

public sealed class ForStatementSyntax : StatementSyntax
{
    public ForStatementSyntax(
        ForKind kind,
        IReadOnlyList<StatementSyntax> initializers,
        ExpressionSyntax? condition,
        IReadOnlyList<StatementSyntax> increments,
        ExpressionSyntax? sequence,
        ExpressionSyntax? rangeEnd,
        ExpressionSyntax? step,
        StatementSyntax? body,
        TextSpan span)
        : base(span)
    {
        Kind = kind;
        Initializers = initializers;
        Condition = condition;
        Increments = increments;
        Sequence = sequence;
        RangeEnd = rangeEnd;
        Step = step;
        Body = body;
    }

    public ForKind Kind { get; }

    /// <summary>The first clause. More than one only under <c>#pragma syntax C for</c>.</summary>
    public IReadOnlyList<StatementSyntax> Initializers { get; }

    public ExpressionSyntax? Condition { get; }

    public IReadOnlyList<StatementSyntax> Increments { get; }

    /// <summary>The list in an <c>in</c> form, or the start value in a range form.</summary>
    public ExpressionSyntax? Sequence { get; }

    public ExpressionSyntax? RangeEnd { get; }

    public ExpressionSyntax? Step { get; }

    public StatementSyntax? Body { get; }
}

/// <summary>One arm of a <c>switch</c>.</summary>
/// <remarks>
/// DM's default grammar spells these <c>if(1)</c>, <c>if(2,3)</c>, <c>if(a to b)</c> and
/// <c>else</c>. Under <c>#pragma syntax C switch</c> they are <c>case 1:</c> and <c>default:</c>,
/// and fall-through becomes real — a genuinely different grammar, not an alias.
/// </remarks>
public sealed class SwitchCaseSyntax : SyntaxNode
{
    public SwitchCaseSyntax(
        IReadOnlyList<ExpressionSyntax> values,
        IReadOnlyList<ExpressionSyntax> rangeEnds,
        bool isDefault,
        StatementSyntax? body,
        TextSpan span)
        : base(span)
    {
        Values = values;
        RangeEnds = rangeEnds;
        IsDefault = isDefault;
        Body = body;
    }

    public IReadOnlyList<ExpressionSyntax> Values { get; }

    /// <summary>Upper bounds for <c>if(a to b)</c> arms, aligned with <see cref="Values"/>.</summary>
    public IReadOnlyList<ExpressionSyntax> RangeEnds { get; }

    public bool IsDefault { get; }

    public StatementSyntax? Body { get; }
}

public sealed class SwitchStatementSyntax : StatementSyntax
{
    public SwitchStatementSyntax(
        ExpressionSyntax value,
        IReadOnlyList<SwitchCaseSyntax> cases,
        bool isCStyle,
        TextSpan span)
        : base(span)
    {
        Value = value;
        Cases = cases;
        IsCStyle = isCStyle;
    }

    public ExpressionSyntax Value { get; }

    public IReadOnlyList<SwitchCaseSyntax> Cases { get; }

    /// <summary>True when parsed under <c>#pragma syntax C switch</c>, where cases fall through.</summary>
    public bool IsCStyle { get; }
}

public sealed class ReturnStatementSyntax : StatementSyntax
{
    public ReturnStatementSyntax(ExpressionSyntax? value, TextSpan span) : base(span) => Value = value;

    public ExpressionSyntax? Value { get; }
}

/// <summary>A <c>break</c> or <c>continue</c>, each of which takes an optional loop label.</summary>
public sealed class BreakStatementSyntax : StatementSyntax
{
    public BreakStatementSyntax(bool isContinue, string? label, TextSpan span) : base(span)
    {
        IsContinue = isContinue;
        Label = label;
    }

    public bool IsContinue { get; }

    public string? Label { get; }
}

/// <summary>A loop label, written <c>name:</c> on its own line.</summary>
public sealed class LabelStatementSyntax : StatementSyntax
{
    public LabelStatementSyntax(string name, StatementSyntax? body, TextSpan span) : base(span)
    {
        Name = name;
        Body = body;
    }

    public string Name { get; }

    public StatementSyntax? Body { get; }
}

public sealed class GotoStatementSyntax : StatementSyntax
{
    public GotoStatementSyntax(string? label, TextSpan span) : base(span) => Label = label;

    public string? Label { get; }
}

/// <summary><c>spawn</c>, with an optional delay in parentheses.</summary>
public sealed class SpawnStatementSyntax : StatementSyntax
{
    public SpawnStatementSyntax(ExpressionSyntax? delay, StatementSyntax? body, TextSpan span) : base(span)
    {
        Delay = delay;
        Body = body;
    }

    public ExpressionSyntax? Delay { get; }

    public StatementSyntax? Body { get; }
}

/// <summary><c>del x</c> or <c>throw x</c>, both of which take a bare operand with no parentheses.</summary>
public sealed class UnaryStatementSyntax : StatementSyntax
{
    public UnaryStatementSyntax(TokenKind keyword, ExpressionSyntax? operand, TextSpan span) : base(span)
    {
        Keyword = keyword;
        Operand = operand;
    }

    public TokenKind Keyword { get; }

    public ExpressionSyntax? Operand { get; }
}

public sealed class TryStatementSyntax : StatementSyntax
{
    public TryStatementSyntax(
        StatementSyntax? body,
        LocalVarStatementSyntax? exception,
        StatementSyntax? catchBody,
        TextSpan span)
        : base(span)
    {
        Body = body;
        Exception = exception;
        CatchBody = catchBody;
    }

    public StatementSyntax? Body { get; }

    /// <summary>The <c>catch(var/exception/e)</c> parameter, if written.</summary>
    public LocalVarStatementSyntax? Exception { get; }

    public StatementSyntax? CatchBody { get; }
}

/// <summary>A <c>set</c> statement, as in <c>set category = "Debug"</c> or <c>set src in view()</c>.</summary>
public sealed class SetStatementSyntax : StatementSyntax
{
    public SetStatementSyntax(string name, ExpressionSyntax? value, TextSpan span) : base(span)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public ExpressionSyntax? Value { get; }
}
