using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Includes;

public enum IncludeKind
{
    /// <summary>A <c>.dm</c> source file. The only kind we recurse into.</summary>
    DmSource,

    /// <summary>A <c>.dmf</c> interface file.</summary>
    Interface,

    /// <summary>A <c>.dmm</c> map file.</summary>
    Map,

    /// <summary>Anything else that appears in the graph.</summary>
    Other,
}

/// <summary>One file reached from the <c>.dme</c>, in compile order.</summary>
public sealed class IncludedFile
{
    internal IncludedFile(string path, IncludeKind kind, string? includedFrom, int depth, bool fromLibrary)
    {
        Path = path;
        Kind = kind;
        IncludedFrom = includedFrom;
        Depth = depth;
        FromLibrary = fromLibrary;
    }

    /// <summary>Absolute, normalised path.</summary>
    public string Path { get; }

    public IncludeKind Kind { get; }

    /// <summary>The file whose directive pulled this one in, or null for the <c>.dme</c> itself.</summary>
    public string? IncludedFrom { get; }

    /// <summary>Nesting depth, with the <c>.dme</c> at 0.</summary>
    public int Depth { get; }

    /// <summary>True if reached through an angle-bracket include, so it lives outside the project.</summary>
    public bool FromLibrary { get; }

    public override string ToString() => $"{Path} ({Kind})";
}

public sealed class IncludeOptions
{
    /// <summary>
    /// Root for angle-bracket includes. Defaults to the BYOND user library folder.
    /// </summary>
    public string? LibraryRoot { get; init; }

    internal string ResolveLibraryRoot()
        => LibraryRoot ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BYOND",
            "lib");
}

/// <summary>
/// The ordered set of files a <c>.dme</c> pulls in.
/// </summary>
/// <remarks>
/// <para>
/// Order matters: DM resolves overrides by include order, and the path ambiguity in PLAN.md §4a is
/// decided by what the compiler had already seen at that line. The traversal is therefore
/// depth-first in directive order, matching the compiler.
/// </para>
/// <para>
/// Resolution rules, all verified against dm.exe 516.1666:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A quoted include resolves relative to the <b>including file's</b> directory, not the
/// <c>.dme</c>'s. Confirmed: <c>sub/a.dm</c> including <c>"b.dm"</c> finds <c>sub/b.dm</c>, and
/// fails outright when only <c>./b.dm</c> exists.
/// </description></item>
/// <item><description>
/// An angle-bracket include resolves against the BYOND library root, outside the project entirely.
/// </description></item>
/// <item><description>
/// Both <c>\</c> and <c>/</c> are accepted as separators. Real <c>.dme</c> files use backslashes,
/// so normalising is what makes a project load on Linux.
/// </description></item>
/// <item><description>
/// Including the same file twice is silently ignored, so dedupe keys on the resolved path rather
/// than the literal string.
/// </description></item>
/// </list>
/// <para>
/// <b>Limitation.</b> Conditional compilation is not yet evaluated, so an <c>#include</c> inside a
/// false <c>#ifdef</c> is still followed. That resolves when the preprocessor lands.
/// </para>
/// </remarks>
public sealed class IncludeGraph
{
    private IncludeGraph(string dmePath, IReadOnlyList<IncludedFile> files, IReadOnlyList<Diagnostic> diagnostics)
    {
        DmePath = dmePath;
        Files = files;
        Diagnostics = diagnostics;
    }

    public string DmePath { get; }

    /// <summary>Every file reached, in compile order, deduplicated by resolved path.</summary>
    public IReadOnlyList<IncludedFile> Files { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public static IncludeGraph Build(string dmePath, IncludeOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dmePath);

        string root = Path.GetFullPath(dmePath);
        if (!File.Exists(root))
            throw new FileNotFoundException("dme not found", root);

        Builder builder = new(options ?? new IncludeOptions());
        builder.Walk(root, includedFrom: null, depth: 0, fromLibrary: false);

        return new IncludeGraph(root, builder.Files, builder.Diagnostics);
    }

    private sealed class Builder
    {
        private readonly IncludeOptions _options;
        private readonly HashSet<string> _seen;

        public Builder(IncludeOptions options)
        {
            _options = options;
            _seen = new HashSet<string>(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }

        public List<IncludedFile> Files { get; } = new();

        public List<Diagnostic> Diagnostics { get; } = new();

        public void Walk(string path, string? includedFrom, int depth, bool fromLibrary)
        {
            if (!_seen.Add(path))
                return;

            IncludeKind kind = ClassifyByExtension(path);
            Files.Add(new IncludedFile(path, kind, includedFrom, depth, fromLibrary));

            // Only DM source contains further directives. A .dmm map is enormous and has nothing
            // for us here.
            if (kind != IncludeKind.DmSource && depth > 0)
                return;

            SourceText text;
            try
            {
                text = SourceFileReader.Read(path);
            }
            catch (IOException ex)
            {
                Diagnostics.Add(Diagnostic.Error("DM0100", new TextSpan(0, 0), $"cannot read {path}: {ex.Message}"));
                return;
            }

            foreach (IncludeDirective directive in IncludeDirective.FindAll(Lexer.Lex(text)))
            {
                string? resolved = Resolve(directive, path, out string attempted);

                if (resolved is null)
                {
                    Diagnostics.Add(Diagnostic.Error(
                        "DM0101",
                        directive.Span,
                        $"unable to open \"{directive.Target}\" (looked for {attempted})"));
                    continue;
                }

                if (_seen.Contains(resolved))
                {
                    // The compiler ignores a repeat silently. Worth surfacing, not worth failing:
                    // real .dme files hit this when DreamMaker's generated block re-adds a manual
                    // entry.
                    Diagnostics.Add(new Diagnostic(
                        "DM0102",
                        DiagnosticSeverity.Information,
                        directive.Span,
                        $"\"{directive.Target}\" was already included; the compiler ignores the repeat"));
                    continue;
                }

                Walk(resolved, path, depth + 1, fromLibrary || directive.IsLibrary);
            }
        }

        private string? Resolve(IncludeDirective directive, string includingFile, out string attempted)
        {
            string relative = directive.Target.Replace('\\', '/');

            if (directive.IsLibrary)
            {
                // <vendor/name> lives at <libroot>/vendor/name/name.dm.
                string libRoot = _options.ResolveLibraryRoot();
                string leaf = relative.Contains('/') ? relative[(relative.LastIndexOf('/') + 1)..] : relative;

                attempted = Path.GetFullPath(Path.Combine(libRoot, relative, leaf + ".dm"));
                if (File.Exists(attempted))
                    return attempted;

                string flat = Path.GetFullPath(Path.Combine(libRoot, relative + ".dm"));
                if (File.Exists(flat))
                    return flat;

                return null;
            }

            // Quoted includes are relative to the including file's own directory.
            string baseDirectory = Path.GetDirectoryName(includingFile) ?? ".";
            attempted = Path.GetFullPath(Path.Combine(baseDirectory, relative));

            return File.Exists(attempted) ? attempted : null;
        }

        private static IncludeKind ClassifyByExtension(string path) =>
            Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".dm" or ".dme" => IncludeKind.DmSource,
                ".dmf" => IncludeKind.Interface,
                ".dmm" => IncludeKind.Map,
                _ => IncludeKind.Other,
            };
    }
}
