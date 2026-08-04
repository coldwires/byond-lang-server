using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
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

    /// <summary>
    /// Macros to define before the walk, in <c>dm.exe -D</c> spelling: <c>CBT</c>,
    /// <c>NAME=value</c>, or <c>FN(x)=body</c>.
    /// </summary>
    /// <remarks>
    /// These decide which <c>#ifdef</c> branches exist, so a project built with flags we do not
    /// receive is a different program from the one we analyse. Pass whatever the build passes.
    /// </remarks>
    public IReadOnlyList<string>? Defines { get; init; }

    /// <summary>
    /// Supplies a file's text, or null to read it from disk.
    /// </summary>
    /// <remarks>
    /// The hook that lets a client's unsaved buffers reach the preprocessor. PLAN.md §4 makes a
    /// pushed buffer the only source for its path, and the include walk would otherwise go straight
    /// to disk and analyse the file as it was last saved — which is exactly wrong on every keystroke
    /// between saves. Paths arrive fully resolved.
    /// </remarks>
    public Func<string, SourceText?>? SourceProvider { get; init; }

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
/// Conditionals are evaluated during the walk, so an <c>#include</c> inside a false <c>#ifdef</c>
/// is not followed. Macro state is threaded through in include order, which is what makes that
/// possible and is also what decides override resolution.
/// </para>
/// </remarks>
public sealed class IncludeGraph
{
    private IncludeGraph(
        string dmePath,
        IReadOnlyList<IncludedFile> files,
        IReadOnlyList<Diagnostic> diagnostics,
        MacroTable macros)
    {
        DmePath = dmePath;
        Files = files;
        Diagnostics = diagnostics;
        Macros = macros;
    }

    public string DmePath { get; }

    /// <summary>Every file reached, in compile order, deduplicated by resolved path.</summary>
    public IReadOnlyList<IncludedFile> Files { get; }

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    /// Macro state at the end of the walk.
    /// </summary>
    /// <remarks>
    /// The <i>final</i> state, not the state at any particular file. Good enough for offering
    /// macro names in a completion list, since a project defines them in headers included early and
    /// rarely undefines them; it is not good enough for deciding what a given line saw, which is
    /// what the M3 boundary snapshots are for.
    /// </remarks>
    public MacroTable Macros { get; }

    /// <summary>Walks the graph without expanding macros.</summary>
    public static IncludeGraph Build(string dmePath, IncludeOptions? options = null)
        => BuildCore(dmePath, options, collectTokens: false).Graph;

    internal static (IncludeGraph Graph, List<ExpandedToken> Tokens) BuildCore(
        string dmePath,
        IncludeOptions? options,
        bool collectTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dmePath);

        string root = Path.GetFullPath(dmePath);
        if (!File.Exists(root))
            throw new FileNotFoundException("dme not found", root);

        List<ExpandedToken> tokens = new();
        Builder builder = new(options ?? new IncludeOptions())
        {
            Tokens = collectTokens ? tokens : null,
        };

        // `__MAIN__` is defined only in the .dme being compiled, never in the files it includes.
        builder.Macros.Define(MacroBuilder.Empty("__MAIN__"));

        builder.Walk(root, includedFrom: null, depth: 0, fromLibrary: false);

        return (new IncludeGraph(root, builder.Files, builder.Diagnostics, builder.Macros), tokens);
    }

    /// <summary>
    /// Walks the graph while acting as a preprocessor pass.
    /// </summary>
    /// <remarks>
    /// Includes cannot be collected without preprocessing, because an <c>#include</c> inside a false
    /// <c>#ifdef</c> is not compiled. That means macro state has to be threaded through the
    /// traversal in include order — which is also why DM's override resolution and the §4a path
    /// ambiguity are order-dependent.
    /// </remarks>
    private sealed class Builder
    {
        private readonly IncludeOptions _options;
        private readonly HashSet<string> _seen;
        private readonly HashSet<string> _onStack;
        private readonly HashSet<string> _reincludable;

        public Builder(IncludeOptions options)
        {
            _options = options;

            StringComparer comparer =
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

            _seen = new HashSet<string>(comparer);
            _onStack = new HashSet<string>(comparer);
            _reincludable = new HashSet<string>(comparer);

            Macros.SeedPredefined();

            // After the predefined ones and before any file, which is where dm.exe puts them: a
            // -D flag is visible to the very first line of the .dme, including its conditionals.
            foreach (MacroDefinition macro in CommandLineDefine.ParseAll(options.Defines, Diagnostics))
                Macros.Define(macro);
        }

        public List<IncludedFile> Files { get; } = new();

        public List<Diagnostic> Diagnostics { get; } = new();

        /// <summary>Macro state, carried across the whole traversal in include order.</summary>
        public MacroTable Macros { get; } = new();

        /// <summary>
        /// Preprocessed output in compile order, or null when only the file list is wanted.
        /// </summary>
        /// <remarks>
        /// Expansion is the expensive part, so callers that only need the include list — the
        /// <c>includes</c> command, orphan detection — skip it entirely.
        /// </remarks>
        public List<ExpandedToken>? Tokens { get; init; }

        public void Walk(string path, string? includedFrom, int depth, bool fromLibrary)
        {
            // A file already being walked further up the stack is a cycle; stop rather than recurse.
            if (!_onStack.Add(path))
                return;

            try
            {
                WalkCore(path, includedFrom, depth, fromLibrary);
            }
            finally
            {
                _onStack.Remove(path);
            }
        }

        private void WalkCore(string path, string? includedFrom, int depth, bool fromLibrary)
        {
            _seen.Add(path);

            IncludeKind kind = ClassifyByExtension(path);
            Files.Add(new IncludedFile(path, kind, includedFrom, depth, fromLibrary));

            // Only DM source contains further directives. A .dmm map is enormous and has nothing
            // for us here.
            if (kind != IncludeKind.DmSource && depth > 0)
                return;

            SourceText text;
            try
            {
                // A pushed buffer wins over disk, so an editor's unsaved text is what gets analysed.
                text = _options.SourceProvider?.Invoke(path) ?? SourceFileReader.Read(path);
            }
            catch (IOException ex)
            {
                Diagnostics.Add(Diagnostic.Error("DM0100", new TextSpan(0, 0), $"cannot read {path}: {ex.Message}"));
                return;
            }

            LexResult lex = Lexer.Lex(text);
            IReadOnlyList<Directive> directives = DirectiveScanner.Scan(lex);
            ConditionalStack conditionals = new();

            // Directives are interleaved with code, and each one can change macro state or pull in
            // another file. Walking token-by-token keeps the emitted stream in true compile order:
            // a run of code is expanded with the macro state that applied at that point, before the
            // next directive gets a chance to change it.
            Dictionary<int, Directive> byHashIndex = new();
            foreach (Directive directive in directives)
                byHashIndex[directive.HashIndex] = directive;

            List<Token> pending = new();
            int tokenIndex = 0;

            // Indentation has to be re-levelled rather than passed through. The lexer's
            // Indent/Dedent tokens describe the file as written, and a skipped `#if` region takes
            // its Indents with it while the matching Dedents survive in code that is still live.
            // The stream then pops levels it never pushed, every later declaration is read one
            // level too shallow, and members end up on the root. Tracking the file's own depth
            // across skipped regions and emitting the difference before each real token keeps the
            // surviving stream self-consistent.
            int sourceDepth = 0;
            int emittedDepth = 0;

            while (tokenIndex < lex.Tokens.Count)
            {
                if (!byHashIndex.TryGetValue(tokenIndex, out Directive directive))
                {
                    Token token = lex.Tokens[tokenIndex];

                    if (token.Kind == TokenKind.Indent)
                        sourceDepth++;
                    else if (token.Kind == TokenKind.Dedent)
                        sourceDepth--;
                    else if (conditionals.IsActive && IsCode(token.Kind))
                        AppendLevelled(pending, token, sourceDepth, ref emittedDepth);

                    tokenIndex++;
                    continue;
                }

                FlushPending(text, pending);
                tokenIndex = System.Math.Max(directive.ArgumentEnd, directive.HashIndex + 2);

                switch (directive.Kind)
                {
                    case DirectiveKind.If:
                        conditionals.PushIf(() => ConditionalEvaluator.Evaluate(lex, directive, Macros, Diagnostics));
                        break;

                    case DirectiveKind.Ifdef:
                    case DirectiveKind.Ifndef:
                        conditionals.PushIf(() => EvaluateIfdef(lex, directive));
                        break;

                    case DirectiveKind.Elif:
                        if (!conditionals.Elif(() => ConditionalEvaluator.Evaluate(lex, directive, Macros, Diagnostics)))
                            Unmatched(directive);
                        break;

                    case DirectiveKind.Else:
                        if (!conditionals.Else())
                            Unmatched(directive);
                        break;

                    case DirectiveKind.Endif:
                        if (!conditionals.Endif())
                            Unmatched(directive);
                        break;

                    case DirectiveKind.Define when conditionals.IsActive:
                        if (MacroDefinition.Parse(lex, directive, Diagnostics) is { } macro)
                            Macros.Define(macro);
                        break;

                    case DirectiveKind.Undef when conditionals.IsActive:
                        if (directive.HasArguments)
                            Macros.Undefine(lex.GetText(lex.Tokens[directive.ArgumentStart]));
                        break;

                    case DirectiveKind.Pragma when conditionals.IsActive:
                        // `#pragma multiple` lets a file be included more than once, opting out of
                        // the compiler's include-once rule.
                        if (directive.HasArguments && lex.GetText(lex.Tokens[directive.ArgumentStart]) == "multiple")
                            _reincludable.Add(path);

                        KeepIfGrammarPragma(text, lex, directive);
                        break;

                    case DirectiveKind.Include when conditionals.IsActive:
                        FollowInclude(lex, directive, path, depth, fromLibrary);
                        break;
                }
            }

            // Close whatever the file left open, so the next file starts from the root rather than
            // inheriting this one's depth.
            while (emittedDepth > 0)
            {
                pending.Add(new Token(TokenKind.Dedent, new TextSpan(text.Length, 0)));
                emittedDepth--;
            }

            FlushPending(text, pending);

            if (conditionals.Depth > 0)
            {
                Diagnostics.Add(Diagnostic.Error(
                    "DM0103",
                    new TextSpan(text.Length, 0),
                    $"{conditionals.Depth} unterminated conditional block(s) at end of file"));
            }
        }

        /// <summary>
        /// Puts a <c>#pragma</c> that changes the grammar back into the output stream.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every other directive is the preprocessor's business and is consumed here.
        /// <c>#pragma syntax C for</c> and <c>#pragma syntax C switch</c> are not: they change what
        /// the <b>parser's</b> grammar is from that line onward, and the parser runs on this stream
        /// after every directive has been stripped out of it. The mode therefore has to ride the
        /// stream as data, or a body written under a pragma is parsed under the default grammar and
        /// reports errors on code dm.exe compiles with none.
        /// </para>
        /// <para>
        /// <c>push</c> and <c>pop</c> come along because they scope it. Emitting the <c>syntax</c>
        /// lines alone would leave a pop unmatched, and the mode would then leak into the rest of
        /// the file instead of ending where the author ended it.
        /// </para>
        /// <para>
        /// The tokens go out unexpanded and carry no <see cref="MacroExpansion"/>: they are the
        /// author's own text at their own position, so a diagnostic on the line points at the line.
        /// A directive carries no indentation of its own (PLAN.md §8), so nothing is emitted into
        /// the Indent/Dedent bookkeeping — only the trailing Newline the parser needs to know where
        /// the directive's words stop.
        /// </para>
        /// </remarks>
        private void KeepIfGrammarPragma(SourceText text, LexResult lex, Directive directive)
        {
            if (Tokens is null || !directive.HasArguments)
                return;

            string first = lex.GetText(lex.Tokens[directive.ArgumentStart]);

            if (first is not ("syntax" or "push" or "pop"))
                return;

            for (int i = directive.HashIndex; i < directive.ArgumentEnd; i++)
            {
                Token token = lex.Tokens[i];
                Tokens.Add(new ExpandedToken(token.Kind, text, token.Span, null));
            }

            Token last = lex.Tokens[directive.ArgumentEnd - 1];
            Tokens.Add(new ExpandedToken(TokenKind.Newline, text, new TextSpan(last.Span.End, 0), null));
        }

        /// <summary>
        /// Expands and emits a run of code, then clears it.
        /// </summary>
        /// <remarks>
        /// Called before every directive and at end of file, so each run is expanded against the
        /// macro state that actually applied to it. Deferring all expansion to the end of a file
        /// would use the file's final macro state for code written above the defines.
        /// </remarks>
        private void FlushPending(SourceText text, List<Token> pending)
        {
            if (pending.Count == 0 || Tokens is null)
                return;

            Tokens.AddRange(MacroExpander.Expand(text, pending, Macros, Diagnostics));
            pending.Clear();
        }

        /// <summary>Layout and comments carry no meaning for the parser.</summary>
        private static bool IsCode(TokenKind kind)
            => kind is not (TokenKind.Comment or TokenKind.EndOfFile);

        /// <summary>
        /// Appends a token, first emitting whatever Indent/Dedent the surviving stream still owes.
        /// </summary>
        /// <remarks>
        /// The debt is only ever non-zero at the first real token of a line whose depth changed, so
        /// the loops are no-ops the rest of the time. Synthesised layout tokens are zero-length at
        /// the token's own start, which keeps every span inside the file it came from.
        /// </remarks>
        private static void AppendLevelled(List<Token> pending, Token token, int sourceDepth, ref int emittedDepth)
        {
            while (emittedDepth < sourceDepth)
            {
                pending.Add(new Token(TokenKind.Indent, new TextSpan(token.Span.Start, 0)));
                emittedDepth++;
            }

            while (emittedDepth > sourceDepth)
            {
                pending.Add(new Token(TokenKind.Dedent, new TextSpan(token.Span.Start, 0)));
                emittedDepth--;
            }

            pending.Add(token);
        }

        private bool EvaluateIfdef(LexResult lex, Directive directive)
        {
            if (!directive.HasArguments)
            {
                Diagnostics.Add(Diagnostic.Error("DM0120", directive.Span, $"#{directive.Name} requires a macro name"));
                return false;
            }

            bool defined = Macros.IsDefined(lex.GetText(lex.Tokens[directive.ArgumentStart]));
            return directive.Kind == DirectiveKind.Ifdef ? defined : !defined;
        }

        private void Unmatched(Directive directive)
            => Diagnostics.Add(Diagnostic.Error(
                "DM0104", directive.Span, $"#{directive.Name} without a matching #if"));

        private void FollowInclude(LexResult lex, Directive directive, string path, int depth, bool fromLibrary)
        {
            if (!IncludeDirective.TryRead(lex, directive, out IncludeDirective include))
            {
                Diagnostics.Add(Diagnostic.Error("DM0105", directive.Span, "malformed #include"));
                return;
            }

            string? resolved = Resolve(include, path, out string attempted);

            if (resolved is null)
            {
                Diagnostics.Add(Diagnostic.Error(
                    "DM0101",
                    include.Span,
                    $"unable to open \"{include.Target}\" (looked for {attempted})"));
                return;
            }

            if (_seen.Contains(resolved) && !_reincludable.Contains(resolved))
            {
                // The compiler ignores a repeat silently. Worth surfacing, not worth failing: real
                // .dme files hit this when DreamMaker's generated block re-adds a manual entry.
                Diagnostics.Add(new Diagnostic(
                    "DM0102",
                    DiagnosticSeverity.Information,
                    include.Span,
                    $"\"{include.Target}\" was already included; the compiler ignores the repeat"));
                return;
            }

            // `__MAIN__` is defined only while processing the .dme itself. Included files must not
            // see it, and it has to come back when we return to the .dme's remaining directives.
            bool wasMain = Macros.IsDefined(MainMacro);
            if (wasMain)
                Macros.Undefine(MainMacro);

            Walk(resolved, path, depth + 1, fromLibrary || include.IsLibrary);

            if (wasMain)
                Macros.Define(MacroBuilder.Empty(MainMacro));
        }

        private const string MainMacro = "__MAIN__";

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
