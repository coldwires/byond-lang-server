using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Diagnostics;
using Dm.Core;
using Dm.Core.Includes;
using Dm.Core.Preprocessing;
using System.Linq;
using Dm.Core.Services;
using Dm.Core.Symbols;
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
                "preprocess" => Preprocess(args),
                "outline" => Outline(args),
                "symbols" => Symbols(args),
                "tree" => Tree(args),
                "complete" => Complete(args),
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
        Console.Error.WriteLine("  symbols <file>           the document outline, as the ABI returns it");
        Console.Error.WriteLine("      --params             include proc parameters");
        Console.Error.WriteLine("      --utf8               columns in UTF-8 bytes instead of UTF-16 units");
        Console.Error.WriteLine("  tree <file.dme>          build the object tree in compile order");
        Console.Error.WriteLine("      --under <path>       list what is declared under a type path");
        Console.Error.WriteLine("      --members <path>     show one type's vars and procs, inherited too");
        Console.Error.WriteLine("      --no-builtins        project declarations only");
        Console.Error.WriteLine("  complete <dme> <file> <line> <col>   what can be typed there");
        Console.Error.WriteLine("      lines and columns are 1-based here, unlike the ABI");
        Console.Error.WriteLine("  preprocess <file.dme>    expand the whole project in compile order");
        Console.Error.WriteLine("      --macros             show tokens that came from a macro");
        Console.Error.WriteLine("      --dump               print every token");
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
    /// Runs a whole project through the preprocessor and reports what came out.
    /// </summary>
    /// <remarks>
    /// The macro view is the useful one: it shows which tokens were produced by expansion and
    /// which invocation each traces back to, which is the part that goes wrong silently if the
    /// source map is broken.
    /// </remarks>
    private static int Preprocess(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: preprocess needs a .dme file");
            return 1;
        }

        bool showMacros = Array.IndexOf(args, "--macros") >= 0;
        bool dump = Array.IndexOf(args, "--dump") >= 0;

        PreprocessResult result = Preprocessor.Run(args[1]);

        int fromMacros = 0;
        Dictionary<string, int> byMacro = new(StringComparer.Ordinal);

        foreach (ExpandedToken token in result.Tokens)
        {
            if (!token.IsFromMacro)
                continue;

            fromMacros++;
            string name = token.Expansion!.Outermost.Macro.Name;
            byMacro[name] = byMacro.TryGetValue(name, out int n) ? n + 1 : 1;
        }

        if (dump)
        {
            foreach (ExpandedToken token in result.Tokens)
            {
                (SourceText source, TextSpan span) = token.ReportAt;
                LinePosition at = source.GetLinePosition(span.Start, PositionEncoding.Utf16);
                string origin = token.IsFromMacro ? $"  <- {token.Expansion!.Macro.Name}" : string.Empty;

                Console.Out.WriteLine(
                    $"{Path.GetFileName(source.Path ?? "?"),-24} {at.Line + 1,6}:{at.Character,-4} " +
                    $"{token.Kind,-22} {Quote(token.Text)}{origin}");
            }
        }

        if (showMacros)
        {
            Console.Out.WriteLine("tokens produced per macro:");
            foreach (KeyValuePair<string, int> entry in byMacro.OrderByDescending(e => e.Value).Take(25))
                Console.Out.WriteLine($"  {entry.Value,7}  {entry.Key}");

            Console.Out.WriteLine();
        }

        Console.Out.WriteLine(
            $"{result.Graph.Files.Count} file(s), {result.Tokens.Count} tokens after expansion, " +
            $"{fromMacros} from macros ({byMacro.Count} distinct)");

        int errors = 0;
        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
                continue;

            errors++;
            if (errors <= 20)
                Console.Out.WriteLine($"  {diagnostic.Id}  {diagnostic.Message}");
        }

        if (errors > 20)
            Console.Out.WriteLine($"  ... and {errors - 20} more");

        return errors == 0 ? 0 : 1;
    }

    /// <summary>
    /// Prints the declaration structure of a file, or counts it across a directory.
    /// </summary>
    /// <summary>
    /// Prints the document-symbol tree. This is the same call the ABI and the LSP server make, so an
    /// IDE dev seeing a wrong outline can check here first and tell whose bug it is.
    /// </summary>
    private static int Symbols(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: symbols needs a file");
            return 1;
        }

        bool includeParameters = args.Contains("--params");
        PositionEncoding encoding = args.Contains("--utf8") ? PositionEncoding.Utf8 : PositionEncoding.Utf16;

        ParseResult parsed = DeclarationParser.Parse(LexFile(args[1]));
        IReadOnlyList<DocumentSymbol> symbols =
            DocumentSymbolService.GetSymbols(parsed, includeParameters, encoding);

        PrintSymbols(symbols, 0);

        foreach (Diagnostic diagnostic in parsed.Diagnostics)
        {
            LinePosition at = parsed.Text.GetLinePosition(diagnostic.Span.Start, encoding);
            Console.Out.WriteLine($"  {at.Line + 1}:{at.Character + 1}  {diagnostic.Id}  {diagnostic.Message}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{Total(symbols)} symbol(s), {parsed.Diagnostics.Count} diagnostic(s)");
        return 0;
    }

    private static void PrintSymbols(IReadOnlyList<DocumentSymbol> symbols, int depth)
    {
        foreach (DocumentSymbol symbol in symbols)
        {
            string detail = string.IsNullOrEmpty(symbol.Detail) ? string.Empty : $"  {symbol.Detail}";

            Console.Out.WriteLine(
                $"{symbol.Start.Line + 1,6}  {new string(' ', depth * 2)}{symbol.Kind.ToString().ToLowerInvariant()} " +
                $"{symbol.Name}{detail}   [{symbol.SelectionStart.Line + 1}:{symbol.SelectionStart.Character + 1}]");

            PrintSymbols(symbol.Children, depth + 1);
        }
    }

    private static int Total(IReadOnlyList<DocumentSymbol> symbols)
    {
        int count = 0;

        foreach (DocumentSymbol symbol in symbols)
            count += 1 + Total(symbol.Children);

        return count;
    }

    /// <summary>
    /// Builds the object tree for a project and reports what is in it.
    /// </summary>
    /// <remarks>
    /// Driven off the include graph rather than a directory glob, because DM resolves overrides by
    /// compile order — the same files in a different order are a different program.
    /// </remarks>
    private static int Tree(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: tree needs a .dme file");
            return 1;
        }

        IncludeGraph graph = IncludeGraph.Build(args[1]);
        List<(string, ParseResult)> parsed = new();
        int problems = 0;

        foreach (IncludedFile file in graph.Files)
        {
            if (file.Kind != IncludeKind.DmSource)
                continue;

            ParseResult parse = DeclarationParser.Parse(LexFile(file.Path));
            parsed.Add((file.Path, parse));

            if (parse.Diagnostics.Count > 0)
                problems++;
        }

        // Builtins first: a project reopens /mob to add its own members, and the two merge.
        ObjectTree tree = new();

        bool withBuiltins = !args.Contains("--no-builtins");
        if (withBuiltins)
            Builtins.Seed(tree);

        foreach ((string path, ParseResult parse) in parsed)
            TypeTreeBuilder.AddFile(tree, path, parse);

        string? under = OptionValue(args, "--under");
        if (under is not null)
        {
            TypeSymbol? root = tree.Find(under);

            if (root is null)
            {
                Console.Error.WriteLine($"error: no type '{under}'");
                return 1;
            }

            PrintSubtree(root, 0);
        }

        string? members = OptionValue(args, "--members");
        if (members is not null)
        {
            TypeSymbol? type = tree.Find(members);

            if (type is null)
            {
                Console.Error.WriteLine($"error: no type '{members}'");
                return 1;
            }

            foreach (TypeSymbol step in tree.InheritanceChain(type))
            {
                Console.Out.WriteLine($"  from {step.Path}:");

                foreach (VarSymbol variable in step.Vars.OrderBy(v => v.Name, StringComparer.Ordinal))
                    Console.Out.WriteLine($"      var  {variable}");

                foreach (ProcSymbol proc in step.Procs.OrderBy(p => p.Name, StringComparer.Ordinal))
                    Console.Out.WriteLine($"      {(proc.IsVerb ? "verb" : "proc")} {proc}");
            }
        }

        int declared = 0, vars = 0, procs = 0, overrides = 0;

        foreach (TypeSymbol type in tree.Types)
        {
            if (type.IsDeclared)
                declared++;

            vars += type.Vars.Count;

            foreach (ProcSymbol proc in type.Procs)
            {
                procs++;
                overrides += proc.Sites.Count - proc.DeclaringCount;
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(
            withBuiltins ? $"builtins: BYOND {Builtins.Version}" : "builtins: not loaded");
        Console.Out.WriteLine(
            $"{parsed.Count} file(s): {tree.Count} type node(s), {declared} declared, " +
            $"{vars} var(s), {procs} proc(s), {overrides} override(s), {problems} file(s) with problems");

        return 0;
    }

    /// <summary>
    /// Completes at a position, the way an editor would.
    /// </summary>
    /// <remarks>
    /// Takes 1-based line and column because that is what an editor's status bar shows. The ABI and
    /// the services underneath are 0-based.
    /// </remarks>
    private static int Complete(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("error: complete needs <dme> <file> <line> <col>");
            return 1;
        }

        if (!int.TryParse(args[3], out int line) || !int.TryParse(args[4], out int column))
        {
            Console.Error.WriteLine("error: line and column must be numbers");
            return 1;
        }

        IncludeGraph graph = IncludeGraph.Build(args[1]);
        ObjectTree tree = new();
        Builtins.Seed(tree);

        foreach (IncludedFile file in graph.Files)
        {
            if (file.Kind == IncludeKind.DmSource)
                TypeTreeBuilder.AddFile(tree, file.Path, DeclarationParser.Parse(LexFile(file.Path)));
        }

        Document document = Document.FromText(args[2], SourceText.From(File.ReadAllText(args[2]), args[2]));
        CompletionResult result = CompletionService.CompleteAt(tree, document, line - 1, column - 1);

        Console.Out.WriteLine($"context: {result.Context}");

        foreach (CompletionItem item in result.Items)
        {
            string mark = item.IsBuiltin ? "*" : " ";
            string detail = string.IsNullOrEmpty(item.Detail) ? string.Empty : $"   {item.Detail}";
            Console.Out.WriteLine($" {mark} {item.Kind.ToString().ToLowerInvariant(),-9} {item.Name}{detail}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{result.Items.Count} item(s)   (* = BYOND builtin)");
        return 0;
    }

    private static void PrintSubtree(TypeSymbol type, int depth)
    {
        Console.Out.WriteLine($"{new string(' ', depth * 2)}{type.Path}{(type.IsDeclared ? string.Empty : "   (implied)")}");

        foreach (TypeSymbol child in type.Children.OrderBy(c => c.Path.Text, StringComparer.Ordinal))
            PrintSubtree(child, depth + 1);
    }

    private static string? OptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        }

        return null;
    }

    private static int Outline(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: outline needs a file or directory");
            return 1;
        }

        if (Directory.Exists(args[1]))
        {
            int files = 0, types = 0, vars = 0, procs = 0, problems = 0;

            foreach (string path in Directory.EnumerateFiles(args[1], "*.dm", SearchOption.AllDirectories))
            {
                ParseResult parsed = DeclarationParser.Parse(LexFile(path));
                files++;

                Count(parsed.Root.Declarations, ref types, ref vars, ref procs);

                if (parsed.Diagnostics.Count > 0)
                {
                    problems++;
                    if (problems <= 15)
                    {
                        LinePosition at = parsed.Text.GetLinePosition(parsed.Diagnostics[0].Span.Start);
                        Console.Out.WriteLine(
                            $"{path}({at.Line + 1},{at.Character + 1}): {parsed.Diagnostics[0].Id} " +
                            $"{parsed.Diagnostics[0].Message}  [{parsed.Diagnostics.Count} total]");
                    }
                }
            }

            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"{files} file(s): {types} types, {vars} vars, {procs} procs, {problems} file(s) with problems");

            return problems == 0 ? 0 : 1;
        }

        ParseResult result = DeclarationParser.Parse(LexFile(args[1]));
        PrintDeclarations(result, result.Root.Declarations, 0);

        foreach (Diagnostic diagnostic in result.Diagnostics)
        {
            LinePosition at = result.Text.GetLinePosition(diagnostic.Span.Start);
            Console.Out.WriteLine($"  {at.Line + 1}:{at.Character + 1}  {diagnostic.Id}  {diagnostic.Message}");
        }

        return result.Diagnostics.Count == 0 ? 0 : 1;
    }

    private static void Count(IReadOnlyList<DeclarationSyntax> declarations, ref int types, ref int vars, ref int procs)
    {
        foreach (DeclarationSyntax declaration in declarations)
        {
            switch (declaration)
            {
                case ProcDeclarationSyntax:
                    procs++;
                    break;

                case VarDeclarationSyntax variable:
                    vars += 1 + variable.Siblings.Count;
                    break;

                case TypeDeclarationSyntax type:
                    types++;
                    Count(type.Members, ref types, ref vars, ref procs);
                    break;
            }
        }
    }

    private static void PrintDeclarations(ParseResult result, IReadOnlyList<DeclarationSyntax> declarations, int depth)
    {
        foreach (DeclarationSyntax declaration in declarations)
        {
            LinePosition at = result.Text.GetLinePosition(declaration.NameSpan.Start);
            string indent = new(' ', depth * 2);

            string description = declaration switch
            {
                ProcDeclarationSyntax p =>
                    $"{(p.IsVerb ? "verb" : "proc")} {p.Path}({string.Join(", ", p.Parameters)})"
                    + (p.IsNewDeclaration ? string.Empty : "   [override]"),
                VarDeclarationSyntax v =>
                    $"var {v.Path}" + (v.DeclaredType is null ? string.Empty : $"   : {v.DeclaredType}")
                    + (v.Modifiers.Count > 0 ? $"   [{string.Join(" ", v.Modifiers)}]" : string.Empty)
                    + (v.Siblings.Count > 0 ? $"   (+{v.Siblings.Count} more)" : string.Empty),
                _ => $"type {declaration.Path}",
            };

            Console.Out.WriteLine($"{at.Line + 1,6}  {indent}{description}");

            if (declaration is TypeDeclarationSyntax type)
                PrintDeclarations(result, type.Members, depth + 1);
        }
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


