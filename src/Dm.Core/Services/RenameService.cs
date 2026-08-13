using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Binding;
using Dm.Core.Preprocessing;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>Why a rename was refused outright. <see cref="None"/> means edits were produced.</summary>
public enum RenameRefusal
{
    /// <summary>Not refused: the rename produced edits.</summary>
    None = 0,

    /// <summary>
    /// Nothing at the position resolves to an index symbol. Locals and parameters are among these:
    /// they are deliberately not in the reference index yet.
    /// </summary>
    NothingAtPosition = 1,

    /// <summary>BYOND declares the symbol: there is no source to edit and a game cannot rename it.</summary>
    Builtin = 2,

    /// <summary>
    /// The position names a type. Renaming a type means rewriting path segments — declarations,
    /// path literals, the leading segments of subtype declarations — which is a different edit
    /// engine, and one that is not built.
    /// </summary>
    Type = 3,

    /// <summary>The new name does not lex as a single identifier, or is a DM keyword.</summary>
    InvalidName = 4,
}

/// <summary>Why a site is reported instead of edited.</summary>
public enum UncertainReason
{
    /// <summary>
    /// A <c>:</c>, <c>?:</c>, <c>?.</c> or <c>::</c> access carrying the member name. These
    /// lookups are wider than a written type, so nothing proves the receiver is ours — and a
    /// receiver that IS ours breaks at runtime if the site is left behind.
    /// </summary>
    ColonAccess = 0,

    /// <summary>
    /// A <c>.</c> access carrying the member name through a receiver with no written type — a call
    /// result, an index, an untyped var. <c>dm.exe</c> degrades these to <c>:</c> and stops
    /// checking, and so must we.
    /// </summary>
    UntypedReceiver = 1,

    /// <summary>
    /// A string literal whose whole text IS the name. <c>vars["health"]</c>,
    /// <c>call(g, "attack")</c> and savefile keys dispatch on exactly the bare name, so a rename
    /// can break a site no resolver can see; prose that merely contains the word is not flagged.
    /// </summary>
    StringLiteral = 2,
}

/// <summary>One place the rename will edit: the old name's span, to be replaced with the new name.</summary>
public sealed class RenameEdit
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public RenameEdit(string file, TextSpan span)
    {
        File = file;
        Span = span;
    }

    /// <summary>The file to edit.</summary>
    public string File { get; }

    /// <summary>The existing name's span in that file.</summary>
    public TextSpan Span { get; }

    /// <summary>Debug rendering: file and span.</summary>
    public override string ToString() => $"{File}{Span}";
}

/// <summary>One place the rename will NOT edit, and why a human has to look at it.</summary>
public sealed class UncertainSite
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public UncertainSite(string file, TextSpan span, UncertainReason reason)
    {
        File = file;
        Span = span;
        Reason = reason;
    }

    /// <summary>The file the site sits in.</summary>
    public string File { get; }

    /// <summary>The name's span at the site — the string's contents for a literal hit.</summary>
    public TextSpan Span { get; }

    /// <summary>Why the site could not be edited.</summary>
    public UncertainReason Reason { get; }

    /// <summary>Debug rendering: file, span and reason.</summary>
    public override string ToString() => $"{File}{Span} ({Reason})";
}

/// <summary>What a rename would do: the provable edits, and the sites it refuses to guess about.</summary>
public sealed class RenameResult
{
    internal RenameResult(RenameRefusal refusal, string target, string newName,
        IReadOnlyList<RenameEdit> edits, IReadOnlyList<UncertainSite> uncertain)
    {
        Refusal = refusal;
        Target = target;
        NewName = newName;
        Edits = edits;
        Uncertain = uncertain;
    }

    /// <summary>Why nothing was produced; <see cref="RenameRefusal.None"/> when edits exist.</summary>
    public RenameRefusal Refusal { get; }

    /// <summary>The canonical symbol being renamed — <c>/mob/test/hp</c> — or empty on refusal.</summary>
    public string Target { get; }

    /// <summary>The replacement text every edit writes, as the caller gave it.</summary>
    public string NewName { get; }

    /// <summary>Every provable site, declarations and overrides included, deduplicated and ordered.</summary>
    public IReadOnlyList<RenameEdit> Edits { get; }

    /// <summary>Every site left alone, for the human who has to check them.</summary>
    public IReadOnlyList<UncertainSite> Uncertain { get; }
}

/// <summary>
/// Renames a symbol everywhere it can be proven and lists everywhere it cannot.
/// </summary>
/// <remarks>
/// <para>
/// A sound rename is impossible in DM — <c>:</c> searches the whole subtype tree, and
/// <c>call("name")</c> dispatches on strings — so this is deliberately best-effort with the
/// uncertainty REPORTED rather than guessed at. The edits come from the same binder walk the
/// reference index and diagnostics use, so a site is edited exactly when diagnostics resolution
/// would succeed on it; the uncertain list comes from the same walk's refusal points plus a
/// string-literal scan. Nothing in the uncertain list is touched.
/// </para>
/// <para>
/// A rename that misses a live site does not error; it silently changes what a game does. That is
/// why the uncertain list is the product here, not an apology attached to it.
/// </para>
/// </remarks>
public static class RenameService
{
    /// <summary>Renames the symbol at a position across the given files.</summary>
    /// <param name="tree">The finished object tree.</param>
    /// <param name="files">Every file to edit and scan, with its parse — the project in compile order.</param>
    /// <param name="document">The file holding the position.</param>
    /// <param name="line">Zero-based line.</param>
    /// <param name="character">Zero-based character, in <paramref name="encoding"/> units.</param>
    /// <param name="newName">The replacement name, validated by the real lexer.</param>
    /// <param name="encoding">How <paramref name="character"/> counts columns.</param>
    /// <param name="cancellationToken">Checked once per file.</param>
    /// <param name="macros">The walk's macro table, so resolution matches definition's.</param>
    /// <param name="lexFor">
    /// A per-file lex, for the string-literal scan. Null skips that scan — acceptable only for a
    /// caller that already knows the project has no string dispatch.
    /// </param>
    public static RenameResult RenameAt(
        ObjectTree tree,
        IReadOnlyList<(string File, ParseResult Parse)> files,
        Document document,
        int line,
        int character,
        string newName,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default,
        MacroTable? macros = null,
        Func<string, LexResult?>? lexFor = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newName);

        if (!IsValidIdentifier(newName))
            return Refuse(RenameRefusal.InvalidName, newName);

        // A typed LOCAL resolves — through definition — to its type's declaration, so without this
        // guard renaming at one would refuse with "that names a type", which is the wrong reason.
        // Locals are refused as not-indexed, which is the true one.
        int offset = document.Text.GetOffset(new LinePosition(line, character), encoding);
        int index = DefinitionService.IndexAt(document.Lex.Tokens, offset);

        if (index >= 0
            && document.Lex.Tokens[index].Kind == TokenKind.Identifier
            && CompletionService.FindLocalType(
                document, offset, document.Lex.GetText(document.Lex.Tokens[index]), out bool _) is not null)
        {
            return Refuse(RenameRefusal.NothingAtPosition, newName);
        }

        // Builtins included so "that is BYOND's" can be said rather than "nothing here" — the
        // resolution is definition's, so rename and go-to-definition cannot disagree on a target.
        IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
            tree, document, line, character, encoding, cancellationToken, macros, includeBuiltins: true);

        if (found.Count == 0)
            return Refuse(RenameRefusal.NothingAtPosition, newName);

        foreach (DefinitionLocation location in found)
        {
            if (location.File.Length == 0)
                return Refuse(RenameRefusal.Builtin, newName);
        }

        // The canonical target, matching the index's canonicalisation: renaming any override
        // renames the family.
        string target = found[^1].Detail;

        // A detail with no `()` that names an existing type is a type, not a var.
        if (!target.EndsWith("()", StringComparison.Ordinal)
            && tree.Find(TypePath.Parse(target)) is not null)
        {
            return Refuse(RenameRefusal.Type, newName);
        }

        string name = ReferenceService.NameOf(target);
        List<RenameEdit> edits = new();
        HashSet<(string File, int Start)> seen = new();

        void AddEdit(string file, TextSpan span)
        {
            if (file.Length > 0 && seen.Add((file, span.Start)))
                edits.Add(new RenameEdit(file, span));
        }

        // The declarations. Definition already returns every declaring site — reopened types and
        // the override chain both — with the name's own span.
        foreach (DefinitionLocation location in found)
            AddEdit(location.File, location.NameSpan);

        // The uses and the refusal points, from one walk per file: the same resolution diagnostics
        // run, with both sinks attached.
        List<UncertainSite> uncertain = new();

        foreach ((string file, ParseResult parse) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Binder.Bind(
                tree,
                parse.Root,
                file,
                reference =>
                {
                    if (string.Equals(reference.Target, target, StringComparison.Ordinal))
                        AddEdit(reference.File, reference.Span);
                },
                name,
                site => uncertain.Add(site));
        }

        // The string scan. The literal's WHOLE text equalling the name is the discriminator:
        // string dispatch spells the bare name and nothing else — `vars["health"]`,
        // `call(g, "attack")`, `hascall(o, "proc_name")` — while prose merely contains it.
        // Measured before narrowed: whole-word matching flagged 8 sites on mlaas, 7 of them
        // player-facing sentences and one an `icon_state = "health"` that shares the var's
        // spelling; exact matching keeps that one and drops the prose.
        if (lexFor is not null)
        {
            foreach ((string file, ParseResult _) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (lexFor(file) is not { } lex)
                    continue;

                foreach (Token token in lex.Tokens)
                {
                    if (token.Kind == TokenKind.StringText
                        && string.Equals(lex.GetText(token), name, StringComparison.Ordinal))
                    {
                        uncertain.Add(new UncertainSite(file, token.Span, UncertainReason.StringLiteral));
                    }
                }
            }
        }

        edits.Sort(static (a, b) => a.File != b.File
            ? string.CompareOrdinal(a.File, b.File)
            : a.Span.Start.CompareTo(b.Span.Start));
        uncertain.Sort(static (a, b) => a.File != b.File
            ? string.CompareOrdinal(a.File, b.File)
            : a.Span.Start.CompareTo(b.Span.Start));

        return new RenameResult(RenameRefusal.None, target, newName, edits, uncertain);
    }

    /// <summary>
    /// Valid means the real lexer reads it as exactly one identifier — which also rejects
    /// keywords, since those lex as keyword tokens rather than names.
    /// </summary>
    private static bool IsValidIdentifier(string name)
    {
        if (name.Length == 0)
            return false;

        IReadOnlyList<Token> tokens = Lexer.Lex(SourceText.From(name)).Tokens;

        return tokens.Count == 2
            && tokens[0].Kind == TokenKind.Identifier
            && tokens[1].Kind == TokenKind.EndOfFile;
    }

    private static RenameResult Refuse(RenameRefusal refusal, string newName)
        => new(refusal, string.Empty, newName, Array.Empty<RenameEdit>(), Array.Empty<UncertainSite>());
}
