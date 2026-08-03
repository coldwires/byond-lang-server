using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Diagnostics;
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

        return totalUnknown == 0 ? 0 : 1;
    }

    private static LexResult LexFile(string path)
    {
        string content = File.ReadAllText(path);
        return Lexer.Lex(SourceText.From(content, path));
    }

    private static string Quote(string text) => "'" + text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "'";
}
