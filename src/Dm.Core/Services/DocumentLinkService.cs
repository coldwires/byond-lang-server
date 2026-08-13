using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Dm.Core.Includes;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>One clickable span in a file and the path it opens.</summary>
public sealed class DocumentLink
{
    /// <summary>The span a client underlines and the file it opens.</summary>
    public DocumentLink(TextSpan span, string target)
    {
        Span = span;
        Target = target;
    }

    /// <summary>The path text alone, inside its quotes or brackets.</summary>
    public TextSpan Span { get; }

    /// <summary>Absolute path to the file this opens.</summary>
    public string Target { get; }

    /// <summary>Debug rendering.</summary>
    public override string ToString() => $"{Span} -> {Target}";
}

/// <summary>
/// The <c>#include</c> targets in one file, resolved to absolute paths.
/// </summary>
/// <remarks>
/// <para>
/// Per file and off the token stream, not off the include graph: a document link is wanted for the
/// file on screen whether or not the project has been walked, and a directive inside a comment is
/// correctly not a directive because the scanner reads tokens rather than text.
/// </para>
/// <para>
/// Resolution repeats the two rules the walk uses — a quoted path is relative to the including
/// file's own directory, an angle-bracket path lives at
/// <c>&lt;libroot&gt;/vendor/name/name.dm</c> — because those are the forms verified against
/// <c>dm.exe</c> in PLAN §3. <b>An unresolved include yields no link at all.</b> A link to a file
/// that is not there would offer navigation that dead-ends, and a missing include is exactly the
/// case where a reader most wants to notice rather than be reassured.
/// </para>
/// </remarks>
public static class DocumentLinkService
{
    /// <summary>The resolvable <c>#include</c> targets in the file; an unresolved include yields no link.</summary>
    public static IReadOnlyList<DocumentLink> LinksFor(
        Document document,
        string? libraryRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<DocumentLink> links = new();
        LexResult lex = document.Lex;

        foreach (Directive directive in DirectiveScanner.Scan(lex))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (directive.Kind != DirectiveKind.Include)
                continue;

            if (!IncludeDirective.TryRead(lex, directive, out IncludeDirective include))
                continue;

            if (include.TargetSpan.IsEmpty)
                continue;

            if (Resolve(include, document.Path, libraryRoot) is { } resolved)
                links.Add(new DocumentLink(include.TargetSpan, resolved));
        }

        return links;
    }

    private static string? Resolve(IncludeDirective include, string includingFile, string? libraryRoot)
    {
        // Windows separators are the norm in real .dme files and both forms work — verified against
        // dm.exe — so normalise before combining or nothing resolves off Windows.
        string relative = include.Target.Replace('\\', '/');

        if (relative.Length == 0)
            return null;

        try
        {
            if (include.IsLibrary)
            {
                if (libraryRoot is null)
                    return null;

                string leaf = relative.Contains('/') ? relative[(relative.LastIndexOf('/') + 1)..] : relative;

                string nested = Path.GetFullPath(Path.Combine(libraryRoot, relative, leaf + ".dm"));
                if (File.Exists(nested))
                    return nested;

                string flat = Path.GetFullPath(Path.Combine(libraryRoot, relative + ".dm"));
                return File.Exists(flat) ? flat : null;
            }

            string baseDirectory = Path.GetDirectoryName(includingFile) ?? ".";
            string full = Path.GetFullPath(Path.Combine(baseDirectory, relative));

            return File.Exists(full) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException
            or UnauthorizedAccessException)
        {
            // A malformed path is an unresolved include, not a failed request.
            return null;
        }
    }
}
