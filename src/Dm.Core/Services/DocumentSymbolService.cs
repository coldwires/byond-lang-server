using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>
/// Builds a file's outline from its syntax tree.
/// </summary>
/// <remarks>
/// <para>
/// Needs the AST only, not the object tree, which is why this ships at M4 rather than waiting for
/// M5. The outline describes <b>one file</b>: a type declared across four files appears in each,
/// showing only what that file contributes. Merging across files is the object tree's job.
/// </para>
/// <para>
/// Positions are line/column rather than offsets, so a client that normalised its line endings
/// still lands in the right place — see PLAN.md §4b.
/// </para>
/// </remarks>
public static class DocumentSymbolService
{
    /// <summary>Builds the outline for a parsed file.</summary>
    /// <param name="parse">The file's parse result.</param>
    /// <param name="includeParameters">
    /// Whether proc parameters appear as children. An outline pane usually wants them off; a
    /// breadcrumb or a symbol search wants them on.
    /// </param>
    /// <param name="encoding">
    /// How a column is counted. LSP wants UTF-16 code units and the C ABI wants UTF-8 bytes; the two
    /// agree for ASCII, so a wrong choice survives testing and then misplaces every position the
    /// first time a file holds a non-ASCII character.
    /// </param>
    /// <param name="cancellationToken">Honoured between declarations, so a large file can be dropped.</param>
    public static IReadOnlyList<DocumentSymbol> GetSymbols(
        ParseResult parse,
        bool includeParameters = false,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parse);

        return Build(parse.Root.Declarations, parse.Text, includeParameters, encoding, cancellationToken);
    }

    private static List<DocumentSymbol> Build(
        IReadOnlyList<DeclarationSyntax> declarations,
        SourceText text,
        bool includeParameters,
        PositionEncoding encoding,
        CancellationToken cancellationToken)
    {
        List<DocumentSymbol> symbols = new(declarations.Count);

        foreach (DeclarationSyntax declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (declaration)
            {
                // A bare `var`/`proc` header is not a symbol; its children belong to the enclosing
                // declaration, which is where a reader expects to find them.
                case TypeDeclarationSyntax { IsGroupHeader: true } group:
                    symbols.AddRange(Build(group.Members, text, includeParameters, encoding, cancellationToken));
                    break;

                case TypeDeclarationSyntax type:
                    symbols.Add(Create(
                        type,
                        SymbolKind.Type,
                        Describe(type),
                        text,
                        encoding,
                        Build(type.Members, text, includeParameters, encoding, cancellationToken)));
                    break;

                case ProcDeclarationSyntax proc:
                    symbols.Add(Create(
                        proc,
                        proc.IsVerb ? SymbolKind.Verb : SymbolKind.Proc,
                        Describe(proc),
                        text,
                        encoding,
                        includeParameters ? Parameters(proc, text, encoding) : Array.Empty<DocumentSymbol>()));
                    break;

                case VarDeclarationSyntax variable:
                    symbols.Add(Create(
                        variable,
                        SymbolKind.Variable,
                        Describe(variable),
                        text,
                        encoding,
                        Array.Empty<DocumentSymbol>()));

                    // Names sharing one `var/` are siblings in the tree but peers in an outline —
                    // `var/a = 1, b = 2` should list two variables, not one with a child.
                    foreach (VarDeclarationSyntax sibling in variable.Siblings)
                    {
                        symbols.Add(Create(
                            sibling,
                            SymbolKind.Variable,
                            Describe(sibling),
                            text,
                            encoding,
                            Array.Empty<DocumentSymbol>()));
                    }

                    break;
            }
        }

        return symbols;
    }

    private static DocumentSymbol Create(
        DeclarationSyntax declaration,
        SymbolKind kind,
        string detail,
        SourceText text,
        PositionEncoding encoding,
        IReadOnlyList<DocumentSymbol> children)
        => new(
            declaration.Name,
            detail,
            kind,
            text.GetLinePosition(declaration.Span.Start, encoding),
            text.GetLinePosition(declaration.Span.End, encoding),
            text.GetLinePosition(declaration.NameSpan.Start, encoding),
            text.GetLinePosition(declaration.NameSpan.End, encoding),
            children);

    private static List<DocumentSymbol> Parameters(
        ProcDeclarationSyntax proc, SourceText text, PositionEncoding encoding)
    {
        List<DocumentSymbol> parameters = new(proc.Parameters.Count);

        foreach (ParameterSyntax parameter in proc.Parameters)
        {
            LinePosition start = text.GetLinePosition(parameter.Span.Start, encoding);
            LinePosition end = text.GetLinePosition(parameter.Span.End, encoding);

            parameters.Add(new DocumentSymbol(
                parameter.Name,
                parameter.DeclaredType?.Text ?? parameter.InputType ?? string.Empty,
                SymbolKind.Parameter,
                start,
                end,
                start,
                end,
                Array.Empty<DocumentSymbol>()));
        }

        return parameters;
    }

    /// <summary>The enclosing path, so a nested declaration shows where it lands in the tree.</summary>
    private static string Describe(TypeDeclarationSyntax type)
        => type.Path.Segments.Count > 1 ? type.Path.Text : string.Empty;

    private static string Describe(VarDeclarationSyntax variable)
    {
        StringBuilder detail = new();

        foreach (string modifier in variable.Modifiers)
        {
            detail.Append(modifier);
            detail.Append(' ');
        }

        if (variable.DeclaredType is { } type)
            detail.Append(type.Text);

        return detail.ToString().TrimEnd();
    }

    private static string Describe(ProcDeclarationSyntax proc)
    {
        StringBuilder detail = new();
        detail.Append('(');

        for (int i = 0; i < proc.Parameters.Count; i++)
        {
            if (i > 0)
                detail.Append(", ");

            detail.Append(proc.Parameters[i].Name);
        }

        detail.Append(')');

        if (!proc.IsNewDeclaration)
            detail.Append(" override");

        return detail.ToString();
    }
}
