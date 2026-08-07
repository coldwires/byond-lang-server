using System.Collections.Generic;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Includes;

/// <summary>
/// The target of one <c>#include</c>.
/// </summary>
public readonly struct IncludeDirective
{
    public IncludeDirective(string target, bool isLibrary, TextSpan span, TextSpan targetSpan = default)
    {
        Target = target;
        IsLibrary = isLibrary;
        Span = span;
        TargetSpan = targetSpan;
    }

    /// <summary>The path as written, before normalisation.</summary>
    public string Target { get; }

    /// <summary>True for <c>&lt;vendor/name&gt;</c>, which resolves outside the project.</summary>
    public bool IsLibrary { get; }

    /// <summary>Span of the whole directive, for diagnostics.</summary>
    public TextSpan Span { get; }

    /// <summary>
    /// Span of the path text alone, inside the quotes or brackets.
    /// </summary>
    /// <remarks>
    /// What a document link makes clickable: underlining the whole <c>#include "x.dm"</c> line
    /// would put the hit target on the directive keyword as well as the file it names.
    /// </remarks>
    public TextSpan TargetSpan { get; }

    public override string ToString() => IsLibrary ? $"<{Target}>" : $"\"{Target}\"";

    /// <summary>
    /// Reads the target of an <c>#include</c> directive.
    /// </summary>
    /// <remarks>
    /// Two forms. A quoted path resolves relative to the including file's directory; an
    /// angle-bracket path resolves against the BYOND library root. The angle-bracket form is
    /// reassembled from the raw span between the brackets rather than from the tokens, because a
    /// library path like <c>deadron/characterhandling</c> lexes as identifiers and slashes.
    /// </remarks>
    public static bool TryRead(LexResult lex, Directive directive, out IncludeDirective include)
    {
        include = default;

        if (directive.Kind != DirectiveKind.Include || !directive.HasArguments)
            return false;

        IReadOnlyList<Token> tokens = lex.Tokens;
        int start = directive.ArgumentStart;

        if (tokens[start].Kind == TokenKind.StringStart)
        {
            int end = start;
            while (end < directive.ArgumentEnd && tokens[end].Kind != TokenKind.StringEnd)
                end++;

            if (end >= directive.ArgumentEnd)
                return false;

            TextSpan inner = TextSpan.FromBounds(tokens[start].Span.End, tokens[end].Span.Start);
            include = new IncludeDirective(lex.Text.ToString(inner), isLibrary: false, directive.Span, inner);
            return true;
        }

        if (tokens[start].Kind == TokenKind.Less)
        {
            int end = start + 1;
            while (end < directive.ArgumentEnd && tokens[end].Kind != TokenKind.Greater)
                end++;

            if (end >= directive.ArgumentEnd)
                return false;

            TextSpan inner = TextSpan.FromBounds(tokens[start].Span.End, tokens[end].Span.Start);
            include = new IncludeDirective(lex.Text.ToString(inner).Trim(), isLibrary: true, directive.Span, inner);
            return true;
        }

        return false;
    }
}
