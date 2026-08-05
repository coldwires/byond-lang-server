using System.Globalization;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Builds macros that have no <c>#define</c> behind them.
/// </summary>
/// <remarks>
/// A macro body is a list of tokens indexing a <see cref="SourceText"/>, so a synthetic macro needs
/// synthetic backing text. Manufacturing one keeps every macro uniform — the expander never has to
/// ask whether a definition came from source or from the compiler.
/// </remarks>
internal static class MacroBuilder
{
    public static MacroDefinition Number(string name, int value)
    {
        string literal = value.ToString(CultureInfo.InvariantCulture);
        SourceText text = SourceText.From(literal, $"<predefined:{name}>");

        return new MacroDefinition(
            name,
            parameters: null,
            isVariadic: false,
            hasNamedRest: false,
            body: new[] { new Token(TokenKind.Number, new TextSpan(0, literal.Length)) },
            source: text,
            nameSpan: new TextSpan(0, 0));
    }

    public static MacroDefinition Empty(string name)
    {
        SourceText text = SourceText.From(string.Empty, $"<predefined:{name}>");

        return new MacroDefinition(
            name,
            parameters: null,
            isVariadic: false,
            hasNamedRest: false,
            body: System.Array.Empty<Token>(),
            source: text,
            nameSpan: new TextSpan(0, 0));
    }
}
