using System;
using System.Collections.Generic;
using System.IO;
using Dm.Assets;
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
                "definition" => Definition(args),
                "hover" => Hover(args),
                "signature" => Signature(args),
                "hints" => Hints(args),
                "colors" => Colors(args),
                "icons" => Icons(args),
                "references" => References(args),
                "rename" => Rename(args),
                "wsymbols" => WorkspaceSymbols(args),
                "query" => Query(args),
                "bench" => Bench.Run(args),
                "diagdiff" => DiagnosticDiff.Run(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");

            // A stack is the difference between "something threw" and a fix, and this is a dev tool.
            if (Environment.GetEnvironmentVariable("DMC_STACK") is not null)
                Console.Error.WriteLine(ex);

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
        Console.Error.WriteLine("  outline <file-or-dir>    the declaration structure: types, vars, proc");
        Console.Error.WriteLine("                           signatures, with any parse problems");
        Console.Error.WriteLine("  symbols <file>           the document outline, as the ABI returns it");
        Console.Error.WriteLine("      --params             include proc parameters");
        Console.Error.WriteLine("      --utf8               columns in UTF-8 bytes instead of UTF-16 units");
        Console.Error.WriteLine("  tree <file.dme>          build the object tree in compile order");
        Console.Error.WriteLine("      --under <path>       list what is declared under a type path");
        Console.Error.WriteLine("      --members <path>     show one type's vars and procs, inherited too");
        Console.Error.WriteLine("      --no-builtins        project declarations only");
        Console.Error.WriteLine("      --procs | --vars     flat \"owner name\" list, for diffing");
        Console.Error.WriteLine("      --raw                parse each file's own text instead of");
        Console.Error.WriteLine("                           the expanded stream; macros stay opaque");
        Console.Error.WriteLine("      --problems           aggregate parse diagnostics by message");
        Console.Error.WriteLine("      --verbose            with --problems, name a file per problem");
        Console.Error.WriteLine("  complete <dme> <file> <line> <col>   what can be typed there");
        Console.Error.WriteLine("      lines and columns are 1-based here, unlike the ABI");
        Console.Error.WriteLine("  hover <dme> <file> <line> <col>      the declaration, as a tooltip");
        Console.Error.WriteLine("  signature <dme> <file> <line> <col>  the call enclosing the position,");
        Console.Error.WriteLine("                           its parameters, and which one the position is in");
        Console.Error.WriteLine("      --brief              omit documentation, as dm_complete_brief does");
        Console.Error.WriteLine("      --resolve <name>     one item's documentation, as dm_complete_resolve");
        Console.Error.WriteLine("  hints <dme> <file> [start end]       inferred-type inlay hints, 1-based lines");
        Console.Error.WriteLine("  colors <file>            the colours written in it, and what a picker may write back");
        Console.Error.WriteLine("  icons <file-or-dir>      icon states in a .dmi, or across a tree");
        Console.Error.WriteLine("      --states             one line per state, for diffing");
        Console.Error.WriteLine("  references <dme> <file> <line> <col> every use of the symbol there");
        Console.Error.WriteLine("      --path <target>      query by canonical path instead: /mob/hp,");
        Console.Error.WriteLine("                           /mob/heal(), /heal() for a global, a type path");
        Console.Error.WriteLine("      --limit <n>          cap (default 1000)");
        Console.Error.WriteLine("  rename <dme> <file> <line> <col> <new-name>");
        Console.Error.WriteLine("                           provable edits, plus the sites left for a human");
        Console.Error.WriteLine("  wsymbols <dme> <query>               search the project by name");
        Console.Error.WriteLine("      --limit <n>          how many hits to show (default 200)");
        Console.Error.WriteLine("  definition <dme> <file> <line> <col> where the symbol is declared");
        Console.Error.WriteLine("      several results is normal: types reopen and procs override");
        Console.Error.WriteLine("  query <file.dme>         browse the tree, as dm_query_json answers it");
        Console.Error.WriteLine("      --path <path>        the node to browse (default /)");
        Console.Error.WriteLine("      --depth <n>          levels of children (default 1)");
        Console.Error.WriteLine("      --subtypes <path>    flat list of everything beneath a path");
        Console.Error.WriteLine("      --members <path>     a type's vars and procs, inherited included");
        Console.Error.WriteLine("      --own                with --members, skip what it inherits");
        Console.Error.WriteLine("      --limit <n>          cap on --subtypes (default 500)");
        Console.Error.WriteLine("      --no-builtins        the project's own declarations only");
        Console.Error.WriteLine("  diagdiff <file.dme>      diff our diagnostics against dm.exe");
        Console.Error.WriteLine("      --dm <path>          dm.exe to compare against");
        Console.Error.WriteLine("      --verbose            show example locations per cause");
        Console.Error.WriteLine("  bench <file.dme>         time a cold open and a warm edit");
        Console.Error.WriteLine("      --rounds <n>         warm rounds to time (default 3)");
        Console.Error.WriteLine("      --file <path>        which file to 'edit' (default: the first)");
        Console.Error.WriteLine("  preprocess <file.dme>    expand the whole project in compile order");
        Console.Error.WriteLine("      --macros             show tokens that came from a macro");
        Console.Error.WriteLine("      --dump               print every token");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  -DNAME, -DNAME=value, -DFN(x)=body");
        Console.Error.WriteLine("      define a macro before the walk, as dm.exe -D does. accepted by");
        Console.Error.WriteLine("      every command that reads a .dme. PASS WHAT THE BUILD PASSES:");
        Console.Error.WriteLine("      the flags decide which #ifdef branches exist, so without them");
        Console.Error.WriteLine("      you are analysing a different program. /tg/station needs -DCBT.");
        Console.Error.WriteLine("      a bare -DNAME defines it EMPTY, not 1, matching the compiler.");
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

        IncludeGraph graph = IncludeGraph.Build(args[1], BuildOptions(args));
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

        PreprocessResult result = Preprocessor.Run(args[1], BuildOptions(args));

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

        List<(string, ParseResult)> parsed = new();
        int problems = 0;

        // The preprocessed stream is the default: a declaration produced by a macro is then the
        // declaration it expands to rather than the macro's name. --raw keeps the old per-file
        // parse, which cannot see through a macro, for comparing the two. See PLAN.md §9.
        if (args.Contains("--raw"))
        {
            IncludeGraph graph = IncludeGraph.Build(args[1], BuildOptions(args));

            foreach (IncludedFile file in graph.Files)
            {
                if (file.Kind != IncludeKind.DmSource)
                    continue;

                ParseResult parse = DeclarationParser.Parse(LexFile(file.Path));
                parsed.Add((file.Path, parse));

                if (parse.Diagnostics.Count > 0)
                    problems++;
            }
        }
        else
        {
            PreprocessResult preprocessed = Preprocessor.Run(args[1], BuildOptions(args));

            foreach ((string file, TokenSource source) in PreprocessedSplitter.Split(preprocessed))
            {
                ParseResult parse = DeclarationParser.Parse(source);
                parsed.Add((file, parse));

                if (parse.Diagnostics.Count > 0)
                    problems++;
            }
        }

        // What actually went wrong, rather than how many files it went wrong in. The count alone
        // cannot tell a real regression from a fixture that was always broken.
        if (args.Contains("--problems"))
        {
            Dictionary<string, int> byMessage = new(StringComparer.Ordinal);

            foreach ((string file, ParseResult parse) in parsed)
            {
                foreach (Diagnostic diagnostic in parse.Diagnostics)
                {
                    string key = $"{diagnostic.Id} {diagnostic.Message}";
                    byMessage[key] = byMessage.GetValueOrDefault(key) + 1;
                }

                if (parse.Diagnostics.Count > 0 && args.Contains("--verbose"))
                {
                    Diagnostic first = parse.Diagnostics[0];
                    LinePosition at = parse.Text.GetLinePosition(first.Span.Start);
                    Console.Out.WriteLine($"{file}({at.Line + 1},{at.Character + 1}): {first.Id} {first.Message}");
                }
            }

            foreach ((string message, int count) in byMessage.OrderByDescending(e => e.Value))
                Console.Out.WriteLine($"{count,8}  {message}");

            return 0;
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

        // A flat "ownerpath name" dump, for diffing against `dm.exe -o`.
        if (args.Contains("--procs") || args.Contains("--vars"))
        {
            bool wantVars = args.Contains("--vars");
            List<string> lines = new();

            foreach (TypeSymbol type in tree.Types)
            {
                string owner = type.Path.IsRoot ? "/" : type.Path.Text;

                if (wantVars)
                {
                    foreach (VarSymbol variable in type.Vars)
                    {
                        if (!variable.IsBuiltin)
                            lines.Add($"{owner} {variable.Name}");
                    }

                    continue;
                }

                foreach (ProcSymbol proc in type.Procs)
                {
                    // Declaration sites, not the builtin flag. A project overriding `/client/New`
                    // leaves the symbol flagged builtin - it was seeded from builtins.txt first -
                    // so filtering on the flag drops procs the project really did declare, and the
                    // diff then reports a gap that only exists in this dump.
                    if (proc.Sites.Count == 0)
                        continue;

                    lines.Add($"{owner} {proc.Name}");
                }
            }

            lines.Sort(StringComparer.Ordinal);

            foreach (string entry in lines)
                Console.Out.WriteLine(entry);

            return 0;
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
    /// <summary>
    /// Prints every declaration of the symbol under a position.
    /// </summary>
    /// <remarks>
    /// Several results is the normal case rather than an error: a type is reopened across files and
    /// a proc has an override chain, and a reader usually wants to see all of them.
    /// </remarks>
    /// <summary>
    /// Browses the object tree the way an IDE panel does, through the same service
    /// <c>dm_query_json</c> answers with.
    /// </summary>
    /// <remarks>
    /// Rendered as a tree rather than as the raw JSON, because the point of the CLI is to show what
    /// the library believes. A client comparing bytes should read <c>abi/schema/</c>, which is the
    /// frozen contract.
    /// </remarks>
    private static int Query(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: query needs a .dme");
            return 1;
        }

        string path = OptionValue(args, "--path") ?? "/";
        bool noBuiltins = Array.IndexOf(args, "--no-builtins") >= 0;

        using Workspace workspace = OpenWorkspace(args);
        ObjectTree tree = workspace.GetObjectTree();

        if (OptionValue(args, "--members") is { } memberPath)
        {
            TypeMembers? members = TreeQueryService.Members(
                tree, memberPath, inherited: Array.IndexOf(args, "--own") < 0, includeBuiltins: !noBuiltins);

            if (members is null)
            {
                Console.Error.WriteLine($"error: no type '{memberPath}'");
                return 1;
            }

            foreach (MemberEntry member in members.Vars)
                PrintMember(member);

            foreach (MemberEntry member in members.Procs)
                PrintMember(member);

            Console.Out.WriteLine();
            Console.Out.WriteLine($"{members.Vars.Count} var(s), {members.Procs.Count} proc(s)");
            return 0;
        }

        if (OptionValue(args, "--subtypes") is { } subtypePath)
        {
            int limit = OptionValue(args, "--limit") is { } given && int.TryParse(given, out int parsed)
                ? parsed
                : TreeQueryService.DefaultSubtypeLimit;

            SubtypeListing? listing = TreeQueryService.Subtypes(
                tree, subtypePath, limit, includeBuiltins: !noBuiltins);

            if (listing is null)
            {
                Console.Error.WriteLine($"error: no type '{subtypePath}'");
                return 1;
            }

            foreach (TreeNode node in listing.Types)
                Console.Out.WriteLine(node.Path);

            Console.Out.WriteLine();
            Console.Out.WriteLine(
                $"{listing.Types.Count} subtype(s){(listing.Truncated ? $", CAPPED at {limit}" : string.Empty)}");

            return 0;
        }

        int depth = OptionValue(args, "--depth") is { } text && int.TryParse(text, out int levels)
            ? levels
            : TreeQueryService.DefaultDepth;

        TreeNode? root = TreeQueryService.Browse(tree, path, depth, includeBuiltins: !noBuiltins);

        if (root is null)
        {
            Console.Error.WriteLine($"error: no type '{path}'");
            return 1;
        }

        PrintNode(root, 0);
        return 0;

        static void PrintNode(TreeNode node, int indent)
        {
            string counts = $"{node.VarCount}v {node.ProcCount}p";
            string more = node.Children.Count < node.ChildCount ? $" (+{node.ChildCount - node.Children.Count})" : "";
            string marks = (node.Declared ? "" : " [implied]") + (node.Builtin ? " [builtin]" : "");

            Console.Out.WriteLine($"{new string(' ', indent * 2)}{node.Path}   {counts}{more}{marks}");

            foreach (TreeNode child in node.Children)
                PrintNode(child, indent + 1);
        }

        static void PrintMember(MemberEntry member)
        {
            string from = member.Inherited ? $"   from {member.Owner}" : string.Empty;
            string builtin = member.Builtin ? "  [builtin]" : string.Empty;

            Console.Out.WriteLine(
                $"{member.Kind.ToString().ToLowerInvariant(),-8} {member.Detail}{from}{builtin}");
        }
    }

    private static int WorkspaceSymbols(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("error: wsymbols needs <dme> <query>");
            return 1;
        }

        int limit = WorkspaceSymbolService.DefaultLimit;
        if (OptionValue(args, "--limit") is { } given && int.TryParse(given, out int parsed))
            limit = parsed;

        using Workspace workspace = OpenWorkspace(args);

        IReadOnlyList<WorkspaceSymbol> hits = WorkspaceSymbolService.Search(
            workspace.GetObjectTree(), args[2], limit);

        foreach (WorkspaceSymbol hit in hits)
        {
            SourceText text = workspace.GetDocument(hit.File).Text;
            LinePosition at = text.GetLinePosition(hit.NameSpan.Start);

            Console.Out.WriteLine(
                $"{hit.Kind.ToString().ToLowerInvariant(),-8} {hit.Detail}"
                + $"   {Relative(workspace.RootDirectory, hit.File)}({at.Line + 1},{at.Character + 1})");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{hits.Count} hit(s)");
        return 0;
    }

    private static int Hover(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("error: hover needs <dme> <file> <line> <col>");
            return 1;
        }

        if (!int.TryParse(args[3], out int line) || !int.TryParse(args[4], out int column))
        {
            Console.Error.WriteLine("error: line and column must be numbers");
            return 1;
        }

        using Workspace workspace = OpenWorkspace(args);
        Document document = workspace.GetDocument(args[2]);

        HoverResult? hover = HoverService.HoverAt(
            workspace.GetObjectTree(), document, line - 1, column - 1,
            macros: workspace.GetMacroTable());

        if (hover is null)
        {
            Console.Out.WriteLine("nothing to show here");
            return 0;
        }

        Console.Out.WriteLine(hover.Detail);
        Console.Out.WriteLine(hover.Signature);

        // What the initialiser comes to (0.30) - beside the signature, as the LSP renders it.
        // Absent until 2026-08-16, which left the ABI's `constant` with no arbiter.
        if (hover.ConstantValue.Length > 0)
            Console.Out.WriteLine($"= {hover.ConstantValue}");

        if (hover.Documentation.Length > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine(hover.Documentation);
        }

        // The CLI is the arbiter, so it shows every field the ABI carries - a value only the ABI
        // renders is a value nobody can check from here.
        if (hover.Reference.Length > 0)
            Console.Out.WriteLine(hover.Reference);

        return 0;
    }

    private static int References(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: references needs <dme> <file> <line> <col>, or <dme> --path <target>");
            return 1;
        }

        using Workspace workspace = OpenWorkspace(args);

        int limit = ReferenceService.DefaultLimit;
        if (OptionValue(args, "--limit") is { } given && int.TryParse(given, out int parsed))
            limit = parsed;

        ReferenceListing? listing;

        if (OptionValue(args, "--path") is { } target)
        {
            listing = ReferenceService.Find(
                workspace.GetObjectTree(), workspace.GetProjectParses(), target, limit);
        }
        else
        {
            if (args.Length < 5
                || !int.TryParse(args[3], out int line) || !int.TryParse(args[4], out int column))
            {
                Console.Error.WriteLine("error: references needs <dme> <file> <line> <col>, or <dme> --path <target>");
                return 1;
            }

            listing = ReferenceService.At(
                workspace.GetObjectTree(),
                workspace.GetProjectParses(),
                workspace.GetDocument(args[2]),
                line - 1,
                column - 1,
                limit: limit);
        }

        if (listing is null)
        {
            Console.Out.WriteLine("nothing at that position is an index symbol");
            return 0;
        }

        foreach (Reference reference in listing.References)
        {
            SourceText text = workspace.GetDocument(reference.File).Text;
            LinePosition at = text.GetLinePosition(reference.Span.Start);

            Console.Out.WriteLine(
                $"{Relative(workspace.RootDirectory, reference.File)}({at.Line + 1},{at.Character + 1}): "
                + $"{reference.Kind.ToString().ToLowerInvariant(),-8} {reference.Target}   inside {reference.Inside}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine(
            $"{listing.References.Count} reference(s){(listing.Truncated ? " (truncated)" : "")}");

        return 0;
    }

    private static int Rename(string[] args)
    {
        if (args.Length < 6
            || !int.TryParse(args[3], out int line) || !int.TryParse(args[4], out int column))
        {
            Console.Error.WriteLine("error: rename needs <dme> <file> <line> <col> <new-name>");
            return 1;
        }

        using Workspace workspace = OpenWorkspace(args);

        RenameResult result = workspace.RenameAt(args[2], line - 1, column - 1, args[5]);

        if (result.Refusal != RenameRefusal.None)
        {
            Console.Out.WriteLine($"refused: {Words.Refusal(result.Refusal)}");
            return 0;
        }

        Console.Out.WriteLine($"rename {result.Target} -> {result.NewName}");
        Console.Out.WriteLine();

        foreach (RenameEdit edit in result.Edits)
        {
            SourceText text = workspace.GetDocument(edit.File).Text;
            LinePosition at = text.GetLinePosition(edit.Span.Start);

            Console.Out.WriteLine(
                $"{Relative(workspace.RootDirectory, edit.File)}({at.Line + 1},{at.Character + 1}): "
                + $"{text.ToString(edit.Span)} -> {result.NewName}");
        }

        if (result.Uncertain.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("NOT edited — check these by hand:");

            foreach (UncertainSite site in result.Uncertain)
            {
                SourceText text = workspace.GetDocument(site.File).Text;
                LinePosition at = text.GetLinePosition(site.Span.Start);

                Console.Out.WriteLine(
                    $"{Relative(workspace.RootDirectory, site.File)}({at.Line + 1},{at.Character + 1}): "
                    + $"{Words.Uncertainty(site.Reason)}   {text.ToString(site.Span)}");
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{result.Edits.Count} edit(s), {result.Uncertain.Count} uncertain");

        return 0;
    }

    /// <summary>The refusal and uncertainty words, spelled once — the ABI and LSP reuse them.</summary>
    private static class Words
    {
        public static string Refusal(RenameRefusal refusal) => refusal switch
        {
            RenameRefusal.NothingAtPosition => "nothing at this position is a renameable symbol"
                + " (locals and parameters are not indexed)",
            RenameRefusal.Builtin => "that symbol is BYOND's; a game cannot rename it",
            RenameRefusal.Type => "that names a type; type rename is not built",
            RenameRefusal.InvalidName => "the new name is not a legal identifier",
            _ => "none",
        };

        public static string Uncertainty(UncertainReason reason) => reason switch
        {
            UncertainReason.ColonAccess => "colon access ",
            UncertainReason.UntypedReceiver => "untyped recv ",
            UncertainReason.StringLiteral => "string       ",
            _ => "?            ",
        };
    }

    private static int Signature(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("error: signature needs <dme> <file> <line> <col>");
            return 1;
        }

        if (!int.TryParse(args[3], out int line) || !int.TryParse(args[4], out int column))
        {
            Console.Error.WriteLine("error: line and column must be numbers");
            return 1;
        }

        using Workspace workspace = OpenWorkspace(args);
        Document document = workspace.GetDocument(args[2]);

        SignatureHelpResult? help = SignatureHelpService.SignatureAt(
            workspace.GetObjectTree(), document, line - 1, column - 1);

        if (help is null)
        {
            Console.Out.WriteLine("no enclosing call here");
            return 0;
        }

        Console.Out.WriteLine(help.Detail);
        Console.Out.WriteLine(help.Label);

        // Which parameter the position sits in, named rather than only counted, since the count
        // is exactly what a caller of this command is trying to confirm.
        string active = help.ActiveParameter < help.Parameters.Count
            ? help.Parameters[help.ActiveParameter]
            : "past the last parameter";

        Console.Out.WriteLine($"active parameter: {help.ActiveParameter} ({active})");
        return 0;
    }

    private static int Definition(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("error: definition needs <dme> <file> <line> <col>");
            return 1;
        }

        if (!int.TryParse(args[3], out int line) || !int.TryParse(args[4], out int column))
        {
            Console.Error.WriteLine("error: line and column must be numbers");
            return 1;
        }

        using Workspace workspace = OpenWorkspace(args);
        Document document = workspace.GetDocument(args[2]);

        IReadOnlyList<DefinitionLocation> found = DefinitionService.DefinitionAt(
            workspace.GetObjectTree(), document, line - 1, column - 1,
            macros: workspace.GetMacroTable());

        if (found.Count == 0)
        {
            Console.Out.WriteLine("no definition found");
            return 0;
        }

        foreach (DefinitionLocation location in found)
        {
            SourceText text = workspace.GetDocument(location.File).Text;
            LinePosition at = text.GetLinePosition(location.NameSpan.Start);

            Console.Out.WriteLine($"{location.File}({at.Line + 1},{at.Character + 1}): {location.Detail}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{found.Count} declaration(s)");
        return 0;
    }

    /// <summary>
    /// The inlay hints the ABI and the LSP serve, rendered per line — the CLI arbiter for the one
    /// call whose output is otherwise only visible inside an editor.
    /// </summary>
    /// <summary>
    /// The colours in a file, as <c>dm_document_colors</c> and the LSP's documentColor answer them.
    /// </summary>
    /// <remarks>
    /// Needs no <c>.dme</c>: colours come off the token stream, so this arbitrates a swatch without
    /// the project being walked. Positions print 1-based, as every other CLI command does.
    /// </remarks>
    private static int Colors(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: colors needs a file");
            return 1;
        }

        PositionEncoding encoding = args.Contains("--utf8") ? PositionEncoding.Utf8 : PositionEncoding.Utf16;

        SourceText text = SourceFileReader.Read(args[1]);
        IReadOnlyList<ColorInformation> colors =
            ColorService.ColorsIn(Document.FromText(args[1], text));

        foreach (ColorInformation color in colors)
        {
            LinePosition at = text.GetLinePosition(color.Span.Start, encoding);
            string swatch = $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}";
            string alpha = color.Alpha >= 255 ? string.Empty : $" alpha {color.Alpha}";

            string form = color.Form == ColorForm.RgbCall ? "rgb()" : "literal";

            Console.Out.WriteLine($"{at.Line + 1}:{at.Character + 1}  {swatch}{alpha}  {form}");
            Console.Out.WriteLine(
                $"      as written  {text.ToString(color.Span)}");
            Console.Out.WriteLine(
                $"      write back  {string.Join("  |  ", ColorService.PresentationsFor(color))}");
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{colors.Count} colour(s)");
        return 0;
    }

    /// <summary>
    /// Icon states in a <c>.dmi</c>, or across a tree of them.
    /// </summary>
    /// <remarks>
    /// A directory argument is what makes this the arbiter for a whole game's assets: totals across
    /// a real project are the check that the reader agrees with the format, and a file that is not
    /// an icon is reported rather than skipped, because three of one project's own <c>.dmi</c>
    /// files are zero bytes.
    /// </remarks>
    private static int Icons(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("error: icons needs a .dmi file or a directory");
            return 1;
        }

        bool perState = args.Contains("--states");
        string target = args[1];

        string[] files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.dmi", SearchOption.AllDirectories)
            : new[] { target };

        Array.Sort(files, StringComparer.Ordinal);

        int icons = 0;
        int states = 0;
        int notIcons = 0;

        foreach (string file in files)
        {
            if (!DmiReader.TryRead(file, out DmiIcon icon))
            {
                notIcons++;

                if (!perState)
                    Console.Out.WriteLine($"{file}  NOT A DMI");

                continue;
            }

            icons++;
            states += icon.States.Count;

            if (perState)
            {
                foreach (DmiState state in icon.States)
                    Console.Out.WriteLine($"{Path.GetFileName(file)}\t{state.Name}\t{state.Dirs}\t{state.Frames}");

                continue;
            }

            string size = icon.Width > 0 ? $"{icon.Width}x{icon.Height}" : "size not stated";
            Console.Out.WriteLine($"{file}  {icon.States.Count} state(s), {size}");

            if (files.Length > 1)
                continue;

            foreach (DmiState state in icon.States)
            {
                string name = state.Name.Length == 0 ? "(default)" : state.Name;
                string movement = state.IsMovement ? "  movement" : string.Empty;
                string delays = state.Delays.Count > 0
                    ? "  delay " + string.Join(",", state.Delays)
                    : string.Empty;
                string looping = state.Loop > 0 ? $"  loop {state.Loop}" : string.Empty;
                string rewind = state.Rewind ? "  rewind" : string.Empty;
                string hotspot = state.Hotspot.Count > 0
                    ? "  hotspot " + string.Join(",", state.Hotspot)
                    : string.Empty;

                Console.Out.WriteLine(
                    $"  {name}  dirs {state.Dirs}  frames {state.Frames}{delays}{looping}{rewind}{movement}{hotspot}");
            }
        }

        if (!perState)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"{icons} icon(s), {states} state(s), {notIcons} not a .dmi");
        }

        return 0;
    }

    private static int Hints(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("error: hints needs <dme> <file> [start-line end-line]");
            return 1;
        }

        int start = 1;
        int end = int.MaxValue;

        if (args.Length >= 5 && int.TryParse(args[3], out int fromLine) && int.TryParse(args[4], out int toLine))
        {
            start = fromLine;
            end = toLine;
        }

        using Workspace workspace = OpenWorkspace(args);
        Document document = workspace.GetDocument(args[2]);

        // CLI positions are 1-based; the service is 0-based like the ABI.
        IReadOnlyList<InlayHint> hints = InlayHintService.HintsFor(
            workspace.GetObjectTree(), document, start - 1, end == int.MaxValue ? int.MaxValue : end - 1);

        foreach (InlayHint hint in hints)
            Console.Out.WriteLine($"{hint.Position.Line + 1}:{hint.Position.Character + 1}  {hint.Label}");

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{hints.Count} hint(s)");
        return 0;
    }

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

        // Through the workspace, so this is the same path the ABI takes: the preprocessed tree,
        // the project's macros, and any -D flags.
        using Workspace workspace = OpenWorkspace(args);
        Document document = workspace.GetDocument(args[2]);

        // `--resolve <name>` is dm_complete_resolve: one item's documentation, nothing else.
        int resolveAt = Array.IndexOf(args, "--resolve");
        if (resolveAt >= 0 && resolveAt + 1 < args.Length)
        {
            string documentation = CompletionService.ResolveDocumentation(
                workspace.GetObjectTree(), document, line - 1, column - 1, args[resolveAt + 1],
                workspace.GetMacroNamesFor(document.Path), workspace.GetFileText);

            Console.Out.WriteLine(documentation.Length > 0 ? documentation : "(no documentation)");
            return 0;
        }

        // `--brief` is dm_complete_brief: the same list with no documentation collected, which is
        // what the lazy-resolve path costs.
        bool brief = Array.IndexOf(args, "--brief") >= 0;

        CompletionResult result = brief
            ? CompletionService.CompleteBriefAt(
                workspace.GetObjectTree(), document, line - 1, column - 1, workspace.GetMacroNamesFor(document.Path))
            : CompletionService.CompleteAt(
                workspace.GetObjectTree(),
                document,
                line - 1,
                column - 1,
                workspace.GetMacroNamesFor(document.Path),
                workspace.GetFileText);

        Console.Out.WriteLine($"context: {result.Context}");

        foreach (CompletionItem item in result.Items)
        {
            string mark = item.IsBuiltin ? "*" : item.Inferred ? "~" : " ";
            string detail = string.IsNullOrEmpty(item.Detail) ? string.Empty : $"   {item.Detail}";

            // The ABI's `value` and `constant` (0.21, 0.30), so they can be checked from here: the
            // author's initialiser as written, then what it folds to when that says something the
            // text does not. `= 5 * 60 -> 300`.
            string value = item.InitialValue.Length > 0 ? $"   = {item.InitialValue}" : string.Empty;
            string constant = item.ConstantValue.Length > 0 ? $" -> {item.ConstantValue}" : string.Empty;

            Console.Out.WriteLine(
                $" {mark} {item.Kind.ToString().ToLowerInvariant(),-9} {item.Name}{detail}{value}{constant}");

            // The first line only: a completion list is a list, not a documentation browser.
            if (item.Documentation.Length > 0)
            {
                string first = item.Documentation.Split('\n')[0];
                Console.Out.WriteLine($"                  {first}");
            }
        }

        Console.Out.WriteLine();
        Console.Out.WriteLine($"{result.Items.Count} item(s)   (* = BYOND builtin, ~ = inferred beyond dm.exe)");
        return 0;
    }

    private static void PrintSubtree(TypeSymbol type, int depth)
    {
        Console.Out.WriteLine($"{new string(' ', depth * 2)}{type.Path}{(type.IsDeclared ? string.Empty : "   (implied)")}");

        foreach (TypeSymbol child in type.Children.OrderBy(c => c.Path.Text, StringComparer.Ordinal))
            PrintSubtree(child, depth + 1);
    }

    /// <summary>
    /// Collects <c>-D</c> flags into the options every graph walk takes.
    /// </summary>
    /// <remarks>
    /// Both spellings the compiler accepts: attached as <c>-DCBT</c>, and separated as
    /// <c>-D CBT</c>. A project built with flags we do not receive is a different program from the
    /// one we analyse, so these belong on every command that walks the graph, not just on `tree`.
    /// </remarks>
    internal static IncludeOptions BuildOptions(string[] args)
    {
        List<string> defines = new();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.Length > 2 && arg.StartsWith("-D", StringComparison.Ordinal))
                defines.Add(arg[2..]);
            else if ((arg == "-D" || arg == "--define") && i + 1 < args.Length)
                defines.Add(args[++i]);
        }

        return new IncludeOptions { Defines = defines.Count > 0 ? defines : null };
    }

    /// <summary>
    /// Opens the project named by <c>args[1]</c>, with the shell's own icon reader attached.
    /// </summary>
    /// <remarks>
    /// Eight commands opened a workspace with the same line, and every one of them would have had
    /// to remember the reader — <c>Dm.Core</c> cannot read a <c>.dmi</c>, so the host supplies it.
    /// One place to forget instead of eight.
    /// </remarks>
    private static Workspace OpenWorkspace(string[] args)
    {
        Workspace workspace = Workspace.Open(args[1], BuildOptions(args).Defines);
        workspace.IconStateReader = DmiReader.StateNames;
        return workspace;
    }

    internal static string? OptionValue(string[] args, string name)
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


