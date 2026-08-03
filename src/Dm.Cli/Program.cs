using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Diagnostics;
using Dm.Core.Includes;
using Dm.Core.Services;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Cli;

/// <summary>
/// Development driver for the analysis library.
/// </summary>
/// <remarks>
/// This is the fastest debugging loop for the pipeline, and it is the arbiter when a client IDE
/// reports a bug: if the CLI reproduces it, the bug is in the core.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            return args[0] switch
            {
                "dump-tokens" => DumpTokens(args),
                "classify" => Classify(args),
                "scan" => Scan(args),
                "includes" => Includes(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void Usage()
    {
        Console.Error.WriteLine("usage: dmc <command> [args]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  dump-tokens <file>       print the token stream");
        Console.Error.WriteLine("  classify <file>          render the file with syntax colouring");
        Console.Error.WriteLine("      --spans              print the raw span table instead");
        Console.Error.WriteLine("      --no-color           plain text, one line per span");
        Console.Error.WriteLine("  scan <file-or-dir>       lex and report unknown tokens and diagnostics");
        Console.Error.WriteLine("  includes <file.dme>      walk the include graph in compile order");
        Console.Error.WriteLine("      --tree               show nesting instead of a flat list");
        Console.Error.WriteLine("      --orphans            also list .dm files on disk that nothing includes");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        Usage();
        return 1;
    }

    private static int DumpTokens(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: dump-tokens needs a file");
            return 1;
        }

        LexResult result = LexFile(args[1]);
        Console.Out.Write(result.ToDebugString());
        return result.Diagnostics.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Walks the include graph from a <c>.dme</c> and prints it in compile order.
    /// </summary>
    /// <remarks>
    /// Compile order is the point. DM resolves overrides by include order, and the path ambiguity
    /// in PLAN.md 4a depends on what the compiler had already seen, so this listing is the ground
    /// truth for both.
    /// </remarks>
    private static int Includes(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: includes needs a .dme file");
            return 1;
        }

        bool tree = Array.IndexOf(args, "--tree") >= 0;
        bool orphans = Array.IndexOf(args, "--orphans") >= 0;

        IncludeGraph graph = IncludeGraph.Build(args[1]);
        string root = Path.GetDirectoryName(graph.DmePath) ?? ".";

        int dm = 0, library = 0;

        foreach (IncludedFile file in graph.Files)
        {
            if (file.Kind == IncludeKind.DmSource)
                dm++;
            if (file.FromLibrary)
                library++;

            string shown = file.FromLibrary ? file.Path : Relative(root, file.Path);
            string marker = file.Kind switch
            {
                IncludeKind.Interface => "  [interface]",
                IncludeKind.Map => "  [map]",
                IncludeKind.Other => "  [other]",
                _ => string.Empty,
            };

            if (file.FromLibrary)
                marker += "  [library]";

            Console.Out.WriteLine(tree
                ? new string(' ', file.Depth * 2) + shown + marker
                : shown + marker);
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{graph.Files.Count} file(s) in compile order, {dm} DM source, {library} from libraries");

        foreach (Diagnostic diagnostic in graph.Diagnostics)
            Console.Out.WriteLine($"  {diagnostic.Severity}: {diagnostic.Id}  {diagnostic.Message}");

        if (orphans)
            ReportOrphans(graph, root);

        bool failed = false;
        foreach (Diagnostic diagnostic in graph.Diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
                failed = true;
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// Lists <c>.dm</c> files under the project root that the graph never reaches.
    /// </summary>
    /// <remarks>
    /// These are dead as far as the compiler is concerned. Usually a disabled subsystem or a file
    /// someone forgot to wire up, and neither is visible from the source itself.
    /// </remarks>
    private static void ReportOrphans(IncludeGraph graph, string root)
    {
        HashSet<string> reached = new(StringComparer.OrdinalIgnoreCase);
        foreach (IncludedFile file in graph.Files)
            reached.Add(file.Path);

        List<string> orphans = new();
        foreach (string path in Directory.EnumerateFiles(root, "*.dm", SearchOption.AllDirectories))
        {
            if (!reached.Contains(Path.GetFullPath(path)))
                orphans.Add(Relative(root, Path.GetFullPath(path)));
        }

        Console.Out.WriteLine();
        if (orphans.Count == 0)
        {
            Console.Out.WriteLine("no orphaned .dm files: everything on disk is included");
            return;
        }

        Console.Out.WriteLine($"{orphans.Count} .dm file(s) on disk that the .dme never reaches:");
        orphans.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string orphan in orphans)
            Console.Out.WriteLine($"  {orphan}");
    }

    private static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }

    /// <summary>
    /// Renders a file the way an IDE would colour it, using the same spans the C ABI hands out.
    /// </summary>
    /// <remarks>
    /// This exists so classification can be checked by eye. A span table is hard to review; a
    /// coloured file is not, and a mis-classified token is obvious at a glance.
    /// </remarks>
    private static int Classify(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: classify needs a file");
            return 1;
        }

        bool spansOnly = Array.IndexOf(args, "--spans") >= 0;
        bool noColor = Array.IndexOf(args, "--no-color") >= 0;

        LexResult lex = LexFile(args[1]);
        SourceText text = lex.Text;
        IReadOnlyList<ClassifiedSpan> spans = ClassificationService.Classify(lex);

        if (spansOnly || noColor)
        {
            foreach (ClassifiedSpan span in spans)
            {
                LinePosition position = text.GetLinePosition(span.Span.Start, PositionEncoding.Utf16);
                Console.Out.WriteLine(
                    $"{position.Line,5}:{position.Character,-4} " +
                    $"utf16={span.Span.Start,-6} utf8={text.GetUtf8Offset(span.Span.Start),-6} " +
                    $"len={span.Span.Length,-4} {(int)span.Kind,2} {span.Kind,-24} " +
                    $"{Quote(text.ToString(span.Span))}");
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine($"{spans.Count} span(s)");
            return 0;
        }

        // Walk the file, colouring the classified runs and printing the gaps between them as-is.
        int cursor = 0;
        foreach (ClassifiedSpan span in spans)
        {
            if (span.Span.Start > cursor)
                Console.Out.Write(text.Content.AsSpan(cursor, span.Span.Start - cursor));

            string colour = AnsiFor(span.Kind);

            if (colour.Length > 0)
                Console.Out.Write(colour);

            Console.Out.Write(text.AsSpan(span.Span));

            if (colour.Length > 0)
            Console.Out.Write("[0m");

            cursor = span.Span.End;
        }

        if (cursor < text.Length)
            Console.Out.Write(text.Content.AsSpan(cursor));

        Console.Out.WriteLine();
        return 0;
    }

    private static string AnsiFor(ClassificationKind kind) => kind switch
    {
        ClassificationKind.Comment => "[32m",                 // green
        ClassificationKind.Keyword => "[94m",                 // bright blue
        ClassificationKind.Number => "[96m",                  // bright cyan
        ClassificationKind.String => "[33m",                  // yellow
        ClassificationKind.InterpolationDelimiter => "[95m",  // bright magenta
        ClassificationKind.Resource => "[93m",                // bright yellow
        ClassificationKind.PreprocessorDirective => "[35m",   // magenta
        ClassificationKind.Operator => "[90m",                // grey
        ClassificationKind.Punctuation => "[90m",             // grey
        ClassificationKind.Identifier => string.Empty,
        ClassificationKind.None => string.Empty,
        ClassificationKind.Error => "[41;97m",                // white on red
        _ => "[0m",
    };

    /// <summary>
    /// Lexes one file or a whole directory and summarises what the lexer failed to understand.
    /// </summary>
    /// <remarks>
    /// An <see cref="TokenKind.Unknown"/> token means an operator or construct is missing from the
    /// lexer, so pointing this at real DM is how the token table gets validated. That is more
    /// reliable than working from the reference, which does not enumerate every operator.
    /// </remarks>
    private static int Scan(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: scan needs a file or directory");
            return 1;
        }

        List<string> files = new();

        if (Directory.Exists(args[1]))
            files.AddRange(Directory.EnumerateFiles(args[1], "*.dm", SearchOption.AllDirectories));
        else
            files.Add(args[1]);

        int totalTokens = 0;
        int totalUnknown = 0;
        int filesWithProblems = 0;

        foreach (string file in files)
        {
            LexResult result = LexFile(file);
            totalTokens += result.Tokens.Count;

            List<Token> unknown = new();
            foreach (Token token in result.Tokens)
            {
                if (token.Kind == TokenKind.Unknown)
                    unknown.Add(token);
            }

            totalUnknown += unknown.Count;

            if (unknown.Count == 0 && result.Diagnostics.Count == 0)
                continue;

            filesWithProblems++;
            Console.Out.WriteLine(file);

            foreach (Token token in unknown)
            {
                LinePosition position = result.Text.GetLinePosition(token.Span.Start, PositionEncoding.Utf16);
                Console.Out.WriteLine($"  {position.Line + 1}:{position.Character + 1}  unknown token  {Quote(result.GetText(token))}");
            }

            foreach (Diagnostic diagnostic in result.Diagnostics)
            {
                LinePosition position = result.Text.GetLinePosition(diagnostic.Span.Start, PositionEncoding.Utf16);
                Console.Out.WriteLine($"  {position.Line + 1}:{position.Character + 1}  {diagnostic.Id}  {diagnostic.Message}");
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{files.Count} file(s), {totalTokens} tokens, {totalUnknown} unknown, {filesWithProblems} file(s) with problems");

        // Non-zero for diagnostics too, not just unknown tokens. An unterminated string is a
        // lexer-visible problem and should fail a regression run the same way.
        return totalUnknown == 0 && filesWithProblems == 0 ? 0 : 1;
    }

    private static LexResult LexFile(string path)
    {
        // Through SourceFileReader, not File.ReadAllText: archives contain Windows-1252 files, and
        // decoding one as UTF-8 turns every high byte into U+FFFD.
        return Lexer.Lex(SourceFileReader.Read(path));
    }

    private static string Quote(string text) => "'" + text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'";
}

