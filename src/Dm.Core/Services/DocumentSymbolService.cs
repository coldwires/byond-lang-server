using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Dm.Core.Symbols;
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

        return Build(
            parse.Root.Declarations, parse.Text, TypePath.Root, includeParameters, encoding,
            cancellationToken);
    }

    private static List<DocumentSymbol> Build(
        IReadOnlyList<DeclarationSyntax> declarations,
        SourceText text,
        TypePath enclosing,
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
                // declaration, which is where a reader expects to find them. It can still carry a
                // type path in front of the keyword — `mob/proc` heads a block on /mob — so the
                // owner comes from the tree builder's rule, not from passing `enclosing` through.
                case TypeDeclarationSyntax { IsGroupHeader: true } group:
                    symbols.AddRange(Build(
                        group.Members, text, TypeTreeBuilder.GroupOwner(enclosing, group.Path),
                        includeParameters, encoding, cancellationToken));
                    break;

                case TypeDeclarationSyntax type:
                {
                    TypePath path = TypeTreeBuilder.Combine(enclosing, type.Path);

                    symbols.Add(Create(
                        type,
                        SymbolKind.Type,
                        Describe(type),
                        text,
                        encoding,
                        Build(type.Members, text, path, includeParameters, encoding, cancellationToken),
                        path.Parent.Text));
                    break;
                }

                case ProcDeclarationSyntax proc:
                {
                    TypePath owner = TypeTreeBuilder.ProcOwner(enclosing, proc.Path);

                    symbols.Add(Create(
                        proc,
                        proc.IsVerb ? SymbolKind.Verb : SymbolKind.Proc,
                        Describe(proc),
                        text,
                        encoding,
                        includeParameters
                            ? Parameters(proc, owner, text, encoding)
                            : Array.Empty<DocumentSymbol>(),
                        owner.Text));
                    break;
                }

                case VarDeclarationSyntax variable:
                    symbols.Add(CreateVar(variable, text, enclosing, encoding));

                    // Names sharing one `var/` are siblings in the tree but peers in an outline —
                    // `var/a = 1, b = 2` should list two variables, not one with a child.
                    foreach (VarDeclarationSyntax sibling in variable.Siblings)
                        symbols.Add(CreateVar(sibling, text, enclosing, encoding));

                    break;
            }
        }

        return symbols;
    }

    private static DocumentSymbol CreateVar(
        VarDeclarationSyntax variable, SourceText text, TypePath enclosing, PositionEncoding encoding)
    {
        // Same fork as the tree builder: under a `var` the leading segments are the declared type
        // and the variable belongs to the enclosing type; a bare assignment's leading segments ARE
        // the type being overridden.
        TypePath owner = variable.InVarContext
            ? TypeTreeBuilder.VarOwner(enclosing, variable.Path)
            : TypeTreeBuilder.BareAssignmentOwner(enclosing, variable.Path);

        return Create(
            variable,
            SymbolKind.Variable,
            Describe(variable),
            text,
            encoding,
            Array.Empty<DocumentSymbol>(),
            owner.Text);
    }

    private static DocumentSymbol Create(
        DeclarationSyntax declaration,
        SymbolKind kind,
        string detail,
        SourceText text,
        PositionEncoding encoding,
        IReadOnlyList<DocumentSymbol> children,
        string owner)
        => new(
            declaration.Name,
            detail,
            kind,
            text.GetLinePosition(declaration.Span.Start, encoding),
            text.GetLinePosition(declaration.Span.End, encoding),
            text.GetLinePosition(declaration.NameSpan.Start, encoding),
            text.GetLinePosition(declaration.NameSpan.End, encoding),
            children,
            owner);

    private static List<DocumentSymbol> Parameters(
        ProcDeclarationSyntax proc, TypePath procOwner, SourceText text, PositionEncoding encoding)
    {
        // A parameter's owner is the proc itself, spelled the way the reference index spells
        // `inside` — `/mob/heal()` — so the two facts join without string surgery.
        string owner = procOwner.IsRoot ? $"/{proc.Name}()" : $"{procOwner.Text}/{proc.Name}()";

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
                Array.Empty<DocumentSymbol>(),
                owner));
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
