using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// A macro created by <c>#define</c>.
/// </summary>
/// <remarks>
/// <see cref="Parameters"/> is null for an object-like macro. The distinction is decided by whether
/// the <c>(</c> touches the name, exactly as in C — verified against dm.exe:
/// <c>#define A (x)</c> is object-like and expands to <c>(x)</c>, while <c>#define B(v)</c> is
/// function-like and a bare <c>B</c> is not expanded at all.
/// </remarks>
internal sealed class MacroDefinition
{
    internal MacroDefinition(
        string name,
        IReadOnlyList<string>? parameters,
        bool isVariadic,
        bool hasNamedRest,
        IReadOnlyList<Token> body,
        SourceText source,
        TextSpan nameSpan)
    {
        Name = name;
        Parameters = parameters;
        IsVariadic = isVariadic;
        HasNamedRest = hasNamedRest;
        Body = body;
        Source = source;
        NameSpan = nameSpan;
    }

    public string Name { get; }

    /// <summary>Parameter names, or null for an object-like macro.</summary>
    public IReadOnlyList<string>? Parameters { get; }

    /// <summary>
    /// True when the last parameter ended in <c>...</c>, meaning it absorbs all remaining arguments
    /// and is optional.
    /// </summary>
    public bool IsVariadic { get; }

    /// <summary>
    /// True for <c>M(a, rest...)</c>, false for the anonymous <c>M(...)</c>.
    /// </summary>
    /// <remarks>
    /// Both are variadic and they bind differently. A named rest parameter absorbs every remaining
    /// argument, so the last parameter is where they land. An anonymous one has nowhere to put them
    /// and simply discards them — so <c>#define MIXED(a, ...) (a)</c> called as
    /// <c>MIXED(7, 8, 9)</c> is <c>(7)</c>, not <c>(7, 8, 9)</c>. Treating the two alike made the
    /// first named parameter swallow the lot.
    /// </remarks>
    public bool HasNamedRest { get; }

    /// <summary>Replacement tokens, indexing <see cref="Source"/>.</summary>
    public IReadOnlyList<Token> Body { get; }

    /// <summary>The file this was defined in; <see cref="Body"/> spans index it.</summary>
    public SourceText Source { get; }

    /// <summary>Span of the macro name, for go-to-definition.</summary>
    public TextSpan NameSpan { get; }

    public bool IsFunctionLike => Parameters is not null;

    public override string ToString()
        => IsFunctionLike ? $"{Name}({string.Join(", ", Parameters!)})" : Name;

    /// <summary>
    /// Reads a <c>#define</c> directive. Returns null and reports a diagnostic if it is malformed.
    /// </summary>
    public static MacroDefinition? Parse(LexResult lex, Directive directive, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(lex);
        ArgumentNullException.ThrowIfNull(diagnostics);

        IReadOnlyList<Token> tokens = lex.Tokens;

        if (!directive.HasArguments)
        {
            diagnostics.Add(Diagnostic.Error("DM0110", directive.Span, "#define requires a macro name"));
            return null;
        }

        int index = directive.ArgumentStart;
        Token nameToken = tokens[index];

        // Keywords are legal macro names; DM has few reserved words and the lexer classifies
        // several contextual ones, so accept anything name-shaped.
        if (!IsNameLike(nameToken.Kind))
        {
            diagnostics.Add(Diagnostic.Error("DM0110", nameToken.Span, "#define requires a macro name"));
            return null;
        }

        string name = lex.GetText(nameToken);
        index++;

        List<string>? parameters = null;
        bool variadic = false;
        bool namedRest = false;

        // Function-like only when the '(' touches the name. With whitespace between, the paren is
        // part of the replacement text instead.
        bool functionLike = index < tokens.Count
                            && tokens[index].Kind == TokenKind.OpenParen
                            && tokens[index].Span.Start == nameToken.Span.End;

        if (functionLike)
        {
            parameters = new List<string>();
            index++;

            while (index < directive.ArgumentEnd && tokens[index].Kind != TokenKind.CloseParen)
            {
                Token parameter = tokens[index];

                if (IsNameLike(parameter.Kind))
                {
                    string parameterName = lex.GetText(parameter);
                    index++;

                    // A trailing `...` makes this parameter variadic and optional.
                    if (index < directive.ArgumentEnd && IsEllipsis(tokens, index, directive.ArgumentEnd))
                    {
                        variadic = true;
                        namedRest = true;
                        index = SkipEllipsis(tokens, index);
                    }

                    parameters.Add(parameterName);
                }
                else if (parameter.Kind == TokenKind.Comma)
                {
                    index++;
                }

                // A bare `...` with no name in front of it. DM has both spellings and real code uses
                // both: /tg/station defines PERFORM_ALL_TESTS as `(focus...)` in one branch of an
                // `#ifdef` and `(...)` in the other, so which one is live depends on the build's
                // flags. Rejecting the anonymous form made every call to it an arity error.
                //
                // The arguments are unreachable without a name, so nothing is recorded for them -
                // the macro simply accepts any number.
                else if (IsEllipsis(tokens, index, directive.ArgumentEnd))
                {
                    variadic = true;
                    index = SkipEllipsis(tokens, index);
                }
                else
                {
                    diagnostics.Add(Diagnostic.Error(
                        "DM0111", parameter.Span, $"unexpected token in the parameter list of macro '{name}'"));
                    index++;
                }
            }

            if (index < directive.ArgumentEnd && tokens[index].Kind == TokenKind.CloseParen)
            {
                index++;
            }
            else
            {
                diagnostics.Add(Diagnostic.Error(
                    "DM0112", directive.Span, $"unterminated parameter list for macro '{name}'"));
            }
        }

        List<Token> body = new();
        for (int i = index; i < directive.ArgumentEnd; i++)
        {
            if (tokens[i].Kind != TokenKind.Comment)
                body.Add(tokens[i]);
        }

        return new MacroDefinition(name, parameters, variadic, namedRest, body, lex.Text, nameToken.Span);
    }

    /// <summary>
    /// The lexer emits <c>...</c> as <c>DotDot</c> + <c>Dot</c>, since <c>..</c> is the parent-call
    /// token. Both orderings are accepted here rather than relying on one.
    /// </summary>
    private static bool IsEllipsis(IReadOnlyList<Token> tokens, int index, int limit)
    {
        if (index >= limit)
            return false;

        if (tokens[index].Kind == TokenKind.DotDot)
            return index + 1 < limit && tokens[index + 1].Kind == TokenKind.Dot;

        return tokens[index].Kind == TokenKind.Dot
               && index + 1 < limit && tokens[index + 1].Kind == TokenKind.DotDot;
    }

    private static int SkipEllipsis(IReadOnlyList<Token> tokens, int index)
        => tokens[index].Kind == TokenKind.DotDot ? index + 2 : index + 2;

    private static bool IsNameLike(TokenKind kind)
        => kind == TokenKind.Identifier || (kind >= TokenKind.KeywordVar && kind <= TokenKind.KeywordGlobal);
}
