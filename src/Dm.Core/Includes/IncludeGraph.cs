using System;
using System.Collections.Generic;
using System.IO;
using Dm.Core.Diagnostics;
using Dm.Core.Preprocessing;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Includes;

internal enum IncludeKind
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
internal sealed class IncludedFile
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

internal sealed class IncludeOptions
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

    /// <summary>
    /// Supplies a file's lex, or null to lex it here.
    /// </summary>
    /// <remarks>
    /// The hook a caller uses to reuse work across rebuilds. Every rebuild otherwise re-lexes the
    /// whole project, and on a large one that is seconds of repeating what did not change. Whether a
    /// cached lex is still valid is the provider's problem, not the walk's — see
    /// <see cref="Text.SourceCache"/>, which decides by re-probing the file.
    /// </remarks>
    public Func<string, SourceText, LexResult>? LexProvider { get; init; }

    /// <summary>
    /// Remembers what walking each file did, so an unchanged one is replayed instead of re-walked.
    /// </summary>
    /// <remarks>
    /// The walk is the largest cost in a rebuild, and after an edit nearly every file does exactly
    /// what it did last time. Supplying a cache here turns those files into a replay of recorded
    /// steps. Ignored when tokens are not being collected, since there is then nothing to replay.
    /// </remarks>
    public FileEffectCache? Effects { get; init; }

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
internal sealed class IncludeGraph
{
    private IncludeGraph(
        string dmePath,
        IReadOnlyList<IncludedFile> files,
        IReadOnlyList<Diagnostic> diagnostics,
        MacroTable macros,
        PragmaLevels warnings)
    {
        DmePath = dmePath;
        Files = files;
        Diagnostics = diagnostics;
        Macros = macros;
        Warnings = warnings;
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

    /// <summary>
    /// Which warnings <c>#pragma ignore</c> silenced, and where.
    /// </summary>
    /// <remarks>
    /// Positional, unlike <see cref="Macros"/>: the level at a point is what decides whether a
    /// diagnostic there is reported, and the same name is legitimately on and off in one file.
    /// </remarks>
    public PragmaLevels Warnings { get; }

    /// <summary>Walks the graph without expanding macros.</summary>
    public static IncludeGraph Build(string dmePath, IncludeOptions? options = null)
        => BuildCore(dmePath, options, collectTokens: false).Graph;

    internal static (IncludeGraph Graph, RunCollector Runs) BuildCore(
        string dmePath,
        IncludeOptions? options,
        bool collectTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dmePath);

        string root = Path.GetFullPath(dmePath);
        if (!File.Exists(root))
            throw new FileNotFoundException("dme not found", root);

        IncludeOptions settings = options ?? new IncludeOptions();
        settings.Effects?.ResetStatistics();

        (IncludeGraph graph, RunCollector runs) = RunWalk(root, settings, collectTokens);

        // A replay found that one of a file's includes now does something different, so part of this
        // build was replayed against a macro state that no longer holds. Nothing here is worth
        // repairing in place: redo it once with the cache out of the way, which is the same cost as
        // the build we did before any of this existed, in the one case where a macro moved and
        // everything downstream had to be redone regardless.
        if (settings.Effects is { Diverged: true })
        {
            settings.Effects.Clear();

            IncludeOptions withoutCache = new()
            {
                LibraryRoot = settings.LibraryRoot,
                Defines = settings.Defines,
                SourceProvider = settings.SourceProvider,
                LexProvider = settings.LexProvider,
            };

            (graph, runs) = RunWalk(root, withoutCache, collectTokens);
        }

        return (graph, runs);
    }

    private static (IncludeGraph Graph, RunCollector Runs) RunWalk(
        string root, IncludeOptions options, bool collectTokens)
    {
        RunCollector runs = new();
        Builder builder = new(options)
        {
            Runs = collectTokens ? runs : null,
        };

        // `__MAIN__` is defined only in the .dme being compiled, never in the files it includes.
        builder.Macros.Define(MacroBuilder.Empty("__MAIN__"));

        builder.Walk(root, includedFrom: null, depth: 0, fromLibrary: false);

        return (new IncludeGraph(root, builder.Files, builder.Diagnostics, builder.Macros, builder.Warnings), runs);
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

            // stddef.dm's constants, which dm.exe compiles ahead of all source. Before the -D flags
            // and before any file, so a project redefining one of them wins — the compiler's own
            // order, since its stddef is simply the first thing included.
            BuiltinMacros.Seed(Macros);

            // After the predefined ones and before any file, which is where dm.exe puts them: a
            // -D flag is visible to the very first line of the .dme, including its conditionals.
            foreach (MacroDefinition macro in CommandLineDefine.ParseAll(options.Defines, Diagnostics))
                Macros.Define(macro);
        }

        public List<IncludedFile> Files { get; } = new();

        public List<Diagnostic> Diagnostics { get; } = new();

        /// <summary>
        /// Attribute every diagnostic this file raised, at the point the file is finished with.
        /// </summary>
        /// <remarks>
        /// Done here rather than at each <c>Diagnostics.Add</c> because two thirds of the emission
        /// sites are in <c>Preprocessing</c> helpers that receive the list and know nothing about
        /// the walk. Backfilling at the boundary catches all of them, and cannot be forgotten by a
        /// new site the way a per-call stamp can.
        ///
        /// A nested file finishes before its includer resumes and stamps its own on the way out, so
        /// anything still unattributed when this runs belongs to <paramref name="path"/>.
        /// </remarks>
        private void AttributeTo(string path, int from)
        {
            for (int i = from; i < Diagnostics.Count; i++)
            {
                if (Diagnostics[i].File is null)
                    Diagnostics[i] = Diagnostics[i].In(path);
            }
        }

        /// <summary>Macro state, carried across the whole traversal in include order.</summary>
        public MacroTable Macros { get; } = new();

        /// <summary>
        /// Which warnings <c>#pragma</c> has silenced, and where. Sequential state like
        /// <see cref="Macros"/>, because §8 verified the level flows through include order.
        /// </summary>
        public PragmaLevels Warnings { get; } = new();

        /// <summary>
        /// Preprocessed output in compile order, or null when only the file list is wanted.
        /// </summary>
        /// <remarks>
        /// Expansion is the expensive part, so callers that only need the include list — the
        /// <c>includes</c> command, orphan detection — skip it entirely.
        /// </remarks>
        public RunCollector? Runs { get; init; }

        /// <summary>
        /// Steps being recorded for the file currently being walked, or null when it is being
        /// replayed or nothing is caching.
        /// </summary>
        /// <remarks>
        /// Saved and restored around each nested file, so a parent resumes recording its own steps
        /// once an include returns.
        /// </remarks>
        private List<EffectStep>? _recording;

        /// <summary>
        /// Where the file currently being walked started in its own run.
        /// </summary>
        /// <remarks>
        /// Zero except for a file entered twice under <c>#pragma multiple</c>, where the run already
        /// holds the first pass. Recorded offsets are relative to this so a replay can index the
        /// slice it was given rather than the accumulated run.
        /// </remarks>
        private int _runStart;

        /// <summary>
        /// The run that receives everything below an expression-position include, or null in the
        /// ordinary case.
        /// </summary>
        /// <remarks>
        /// dm.exe allows an <c>#include</c> inside an open bracket, splicing the file into the
        /// surrounding expression — tgstation's <c>ApiVersion()</c> returns
        /// <c>new /datum/tgs_version(</c> + <c>#include "__interop_version.dm"</c> + <c>)</c>.
        /// A parser working per-file run cannot see across that seam, so the spliced file's tokens
        /// are routed into the INCLUDING file's run instead: the expression is whole again, and
        /// the spliced file contributes no run of its own to parse as bogus declarations. Position
        /// reporting needs nothing extra — <c>TokenSource.FromExpanded</c> already collapses a
        /// span from another file onto the furthest point reached, which is the include site.
        /// The field stays set for the whole subtree, so anything a spliced file itself includes
        /// lands in the same expression.
        /// </remarks>
        private SourceText? _inlineTarget;

        /// <summary>
        /// True when the file currently being walked must not be cached: an inline include put
        /// tokens into its run with no recorded step to cover them, so a replay would drop them.
        /// </summary>
        private bool _effectPoisoned;

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

            // Replay, if this file's text and the macro state it is being entered with are both what
            // they were last time. Everything below - the lex, the directive scan, the token loop
            // and the expansion - is what that skips.
            //
            // A file inside an inline-include subtree never touches the cache in either direction:
            // its tokens belong to another file's run, so a cached effect would be empty and a
            // replayed one would append to the wrong place.
            FileEffectCache? effects = Runs is null || _inlineTarget is not null ? null : _options.Effects;
            int entryHash = Macros.StateHash;

            if (effects is not null && effects.TryGet(path, text, entryHash, out FileEffect cached))
            {
                Replay(cached, path, depth, fromLibrary, effects);
                return;
            }

            List<EffectStep>? outerRecording = _recording;
            int outerRunStart = _runStart;
            bool outerPoisoned = _effectPoisoned;

            List<EffectStep>? steps = effects is null ? null : new List<EffectStep>();
            int runStart = Runs?.LengthOf(text) ?? 0;

            _recording = steps;
            _runStart = runStart;
            _effectPoisoned = false;

            bool poisoned;

            try
            {
                WalkFile(path, depth, fromLibrary, text);
            }
            finally
            {
                poisoned = _effectPoisoned;
                _recording = outerRecording;
                _runStart = outerRunStart;
                _effectPoisoned = outerPoisoned;
            }

            if (effects is not null && steps is not null && !poisoned)
            {
                // Read from the local: the field is back to the enclosing file's start by now.
                ExpandedToken[] run = Runs?.Slice(text, runStart) ?? System.Array.Empty<ExpandedToken>();
                effects.Add(path, new FileEffect(text, entryHash, steps, run, Macros.StateHash));
            }
        }

        /// <summary>Walks one file's tokens and directives, recording its effect if a cache wants it.</summary>
        private void WalkFile(string path, int depth, bool fromLibrary, SourceText text)
        {
            int from = Diagnostics.Count;

            try
            {
                WalkFileCore(path, depth, fromLibrary, text);
            }
            finally
            {
                AttributeTo(path, from);
            }
        }

        private void WalkFileCore(string path, int depth, bool fromLibrary, SourceText text)
        {
            LexResult lex = _options.LexProvider?.Invoke(path, text) ?? Lexer.Lex(text);
            IReadOnlyList<Directive> directives = DirectiveScanner.Scan(lex);
            ConditionalStack conditionals = new();

            // The level map this file is walked with, so a query inside it starts from what an
            // earlier file left rather than from the default.
            Warnings.EnterFile(path);

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

            // Open brackets the live code has not closed yet. Non-zero at an #include means the
            // include is EXPRESSION-POSITION - the file is spliced into the surrounding brackets -
            // and its tokens have to join this file's run. See _inlineTarget.
            int bracketDepth = 0;

            while (tokenIndex < lex.Tokens.Count)
            {
                if (!byHashIndex.TryGetValue(tokenIndex, out Directive directive))
                {
                    Token token = lex.Tokens[tokenIndex];

                    if (token.Kind == TokenKind.Indent)
                        sourceDepth++;
                    else if (token.Kind == TokenKind.Dedent)
                        sourceDepth--;
                    else if (conditionals.IsActive && token.Kind == TokenKind.Newline)
                    {
                        // A newline is layout, so it does not collect the level debt. Directive
                        // lines are layout-neutral in the lexer, which leaves the newline after an
                        // `#endif` sitting at the SKIPPED content's depth until the next code line
                        // dedents — levelling before it materialised that depth as an Indent
                        // opening a block with nothing in it, reported as "expected a declaration"
                        // on the directive's own line. The debt is paid at the next real token,
                        // whose depth a surviving line actually has.
                        pending.Add(token);
                    }
                    else if (conditionals.IsActive && IsCode(token.Kind))
                    {
                        AppendLevelled(pending, token, sourceDepth, ref emittedDepth);

                        if (token.Kind is TokenKind.OpenParen or TokenKind.OpenBracket
                            or TokenKind.QuestionOpenBracket or TokenKind.OpenBrace)
                        {
                            bracketDepth++;
                        }
                        else if (bracketDepth > 0 && token.Kind is TokenKind.CloseParen
                            or TokenKind.CloseBracket or TokenKind.CloseBrace)
                        {
                            bracketDepth--;
                        }
                    }

                    tokenIndex++;
                    continue;
                }

                FlushPending(text, pending);
                tokenIndex = System.Math.Max(directive.ArgumentEnd, directive.HashIndex + 2);

                // Diagnostics this directive produces are part of this file's effect and have to be
                // replayed with it, or a cached file reports clean on the next build.
                int diagnosticsBefore = Diagnostics.Count;

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

                    // The compiler echoes the author's own text back as a warning, at the
                    // directive's own line: `Turfs.dm:45:warning: #warning its creating this five
                    // times...`. No warning NAME, so no `#pragma` handle and a private id is the
                    // right one. Reporting it at all needed per-file attribution first, which is
                    // why it sat in the missed column while three cheaper checks shipped past it.
                    case DirectiveKind.Warn when conditionals.IsActive:
                        Diagnostics.Add(Diagnostic.Warning(
                            "DM0204", directive.Span, text.ToString(directive.Span).Trim()));
                        break;

                    case DirectiveKind.Define when conditionals.IsActive:
                        if (MacroDefinition.Parse(lex, directive, Diagnostics) is { } macro)
                        {
                            Macros.Define(macro);
                            _recording?.Add(EffectStep.ForDefine(macro));
                        }

                        break;

                    case DirectiveKind.Undef when conditionals.IsActive:
                        if (directive.HasArguments)
                        {
                            string undefined = lex.GetText(lex.Tokens[directive.ArgumentStart]);
                            Macros.Undefine(undefined);
                            _recording?.Add(EffectStep.ForUndef(undefined));
                        }

                        break;

                    case DirectiveKind.Pragma when conditionals.IsActive:
                        // `#pragma multiple` lets a file be included more than once, opting out of
                        // the compiler's include-once rule.
                        if (directive.HasArguments && lex.GetText(lex.Tokens[directive.ArgumentStart]) == "multiple")
                        {
                            _reincludable.Add(path);
                            _recording?.Add(EffectStep.ForReincludable(path));
                        }

                        ReadWarningPragma(path, lex, directive);
                        KeepIfGrammarPragma(text, lex, directive);
                        break;

                    case DirectiveKind.Include when conditionals.IsActive:
                    {
                        string? resolved = ResolveInclude(lex, directive, path, out bool isLibrary, out IncludeSite site);

                        // Whatever resolution had to say belongs to this file. What the included
                        // file has to say belongs to it, and it records its own.
                        RecordDiagnosticsFrom(diagnosticsBefore);

                        if (resolved is not null && bracketDepth > 0)
                        {
                            // Expression-position: the file is spliced into this file's open
                            // brackets, so its tokens go into THIS run — with no recorded step to
                            // cover them, which is why this walk must never be cached.
                            EnterInclude(resolved, path, depth, fromLibrary || isLibrary, site, text);
                            _effectPoisoned = true;
                        }
                        else if (resolved is not null)
                        {
                            EnterInclude(resolved, path, depth, fromLibrary || isLibrary, site);
                            _recording?.Add(
                                EffectStep.ForInclude(resolved, isLibrary, site, Macros.StateHash));
                        }

                        diagnosticsBefore = Diagnostics.Count;
                        break;
                    }
                }

                RecordDiagnosticsFrom(diagnosticsBefore);
            }

            int tailDiagnostics = Diagnostics.Count;

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

            RecordDiagnosticsFrom(tailDiagnostics);
        }

        /// <summary>One stretch of a recorded run, for the rare replay that cannot adopt it whole.</summary>
        private static ExpandedToken[] Slice(ExpandedToken[] run, int start, int count)
        {
            ExpandedToken[] slice = new ExpandedToken[count];
            System.Array.Copy(run, start, slice, 0, count);

            return slice;
        }

        /// <summary>Records diagnostics added since a watermark as part of this file's effect.</summary>
        private void RecordDiagnosticsFrom(int from)
        {
            if (_recording is null)
                return;

            for (int i = from; i < Diagnostics.Count; i++)
                _recording.Add(EffectStep.ForDiagnostic(Diagnostics[i]));
        }

        /// <summary>
        /// Does again what walking this file did last time, without walking it.
        /// </summary>
        /// <remarks>
        /// Includes are re-entered rather than replayed from here: the file an include leads to
        /// decides for itself whether it can be replayed, and it is the one that knows what macro
        /// state it is being entered with now.
        /// </remarks>
        private void Replay(FileEffect effect, string path, int depth, bool fromLibrary, FileEffectCache effects)
        {
            List<EffectStep>? outerRecording = _recording;
            _recording = null;

            // The whole run at once, by reference. A replayed file otherwise copies every token it
            // ever produced into a fresh list, which on a large project is most of what a rebuild
            // costs. Where each stretch sits between this file's includes is replayed separately,
            // from the offsets recorded with the steps.
            bool adopted = Runs is not null && Runs.TryAdopt(effect.Text, effect.Run);

            try
            {
                foreach (EffectStep step in effect.Steps)
                {
                    switch (step.Kind)
                    {
                        case EffectKind.Tokens:
                            if (adopted)
                                Runs!.AddSegment(effect.Text, step.Start, step.Count);
                            else
                                Runs!.Append(effect.Text, Slice(effect.Run, step.Start, step.Count));

                            break;

                        case EffectKind.Define:
                            Macros.Define(step.Macro!);
                            break;

                        case EffectKind.Undef:
                            Macros.Undefine(step.Name!);
                            break;

                        case EffectKind.Reincludable:
                            _reincludable.Add(step.Name!);
                            break;

                        case EffectKind.Diagnostic:
                            Diagnostics.Add(step.Diagnostic);
                            break;

                        case EffectKind.Include:
                            EnterInclude(step.Name!, path, depth, fromLibrary || step.IsLibrary, step.Site);

                            // The include did something different this time, so everything recorded
                            // after it describes an expansion that no longer happens.
                            if (Macros.StateHash != step.HashAfter)
                                effects.MarkDiverged();

                            break;
                    }
                }
            }
            finally
            {
                _recording = outerRecording;
            }

            // A self-test rather than a guard: if the steps really are everything the file did, the
            // state it leaves behind is the state it left behind when it was recorded.
            if (Macros.StateHash != effect.ExitHash)
                effects.MarkDiverged();
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
        /// <summary>
        /// Records <c>#pragma warn|ignore|error &lt;names&gt;</c> and <c>#pragma push|pop</c>.
        /// </summary>
        /// <remarks>
        /// The names take a comma-separated list and are the same vocabulary as the compiler's
        /// <c>-ignore</c>/<c>-warn</c>/<c>-error</c> flags, so a project silencing one in source
        /// expects it silenced here. <c>push</c>/<c>pop</c> scope the level to a region, and the
        /// state carries across files in include order — both compiler-verified, see
        /// <see cref="PragmaLevels"/>.
        /// </remarks>
        private void ReadWarningPragma(string path, LexResult lex, Directive directive)
        {
            if (!directive.HasArguments)
                return;

            string first = lex.GetText(lex.Tokens[directive.ArgumentStart]);

            if (first == "push")
            {
                Warnings.Push();
                return;
            }

            if (first == "pop")
            {
                Warnings.Pop(path, lex.Tokens[directive.HashIndex].Span.Start);
                return;
            }

            PragmaLevel level = first switch
            {
                "ignore" => PragmaLevel.Ignore,
                "warn" => PragmaLevel.Warn,
                "error" => PragmaLevel.Error,
                _ => (PragmaLevel)(-1),
            };

            if (level == (PragmaLevel)(-1))
                return;

            int offset = lex.Tokens[directive.HashIndex].Span.Start;

            // The list is comma-separated and the lexer gives each name its own token, so anything
            // that is not a separator is a name or an id.
            for (int i = directive.ArgumentStart + 1; i <= directive.ArgumentEnd && i < lex.Tokens.Count; i++)
            {
                if (lex.Tokens[i].Kind is TokenKind.Comma or TokenKind.Newline)
                    continue;

                Warnings.Set(path, offset, lex.GetText(lex.Tokens[i]), level);
            }
        }

        private void KeepIfGrammarPragma(SourceText text, LexResult lex, Directive directive)
        {
            if (Runs is null || !directive.HasArguments)
                return;

            string first = lex.GetText(lex.Tokens[directive.ArgumentStart]);

            if (first is not ("syntax" or "push" or "pop"))
                return;

            List<ExpandedToken> kept = new();

            for (int i = directive.HashIndex; i < directive.ArgumentEnd; i++)
            {
                Token token = lex.Tokens[i];
                kept.Add(new ExpandedToken(token.Kind, text, token.Span, null));
            }

            Token last = lex.Tokens[directive.ArgumentEnd - 1];
            kept.Add(new ExpandedToken(TokenKind.Newline, text, new TextSpan(last.Span.End, 0), null));

            Emit(text, kept);
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
            if (pending.Count == 0 || Runs is null)
                return;

            // dm.exe RE-PROCESSES macro expansions for directives: `#define int #define` then
            // `int DEAD 2` defines DEAD, and madridspy builds its whole status-flag vocabulary
            // this way — probed 2026-08-13, with macro-made #undef and a macro carrying a
            // COMPLETE directive both confirmed. A line-starting object-like macro whose body
            // begins with `#` splits the run: code before it is expanded under the state that
            // applied to it, the line itself becomes a directive, and the remainder is expanded
            // after — which is what lets a later line of the same run use the macro this one
            // just made.
            int from = 0;
            bool lineHasCode = false;

            for (int i = 0; i < pending.Count; i++)
            {
                Token token = pending[i];

                if (token.Kind == TokenKind.Newline)
                {
                    lineHasCode = false;
                    continue;
                }

                if (token.Kind is TokenKind.Indent or TokenKind.Dedent)
                    continue;

                bool starts = !lineHasCode;
                lineHasCode = true;

                if (!starts
                    || token.Kind != TokenKind.Identifier
                    || !Macros.TryGet(text.ToString(token.Span), out MacroDefinition head)
                    || head.Parameters is not null
                    || head.Body.Count == 0
                    || head.Body[0].Kind != TokenKind.Hash)
                {
                    continue;
                }

                int end = i;
                while (end < pending.Count && pending[end].Kind != TokenKind.Newline)
                    end++;

                if (i > from)
                    Emit(text, MacroExpander.Expand(text, pending.GetRange(from, i - from), Macros, Diagnostics));

                ProcessMacroMadeDirective(text, pending.GetRange(i, end - i));

                // The newline stays with the next segment, as layout.
                from = end;
                i = end - 1;
            }

            List<Token> tail = from == 0 ? pending : pending.GetRange(from, pending.Count - from);

            if (tail.Count > 0)
                Emit(text, MacroExpander.Expand(text, tail, Macros, Diagnostics));

            pending.Clear();
        }

        /// <summary>
        /// Expands one line whose leading macro produces a <c>#</c>, and runs the result as a
        /// directive — the re-processing <c>dm.exe</c> does for every expansion.
        /// </summary>
        /// <remarks>
        /// The expanded tokens are rendered to synthetic text and put through the same lexer,
        /// scanner and <see cref="MacroDefinition.Parse"/> a real directive line uses — the
        /// <see cref="CommandLineDefine"/> pattern — so the define that comes out is
        /// indistinguishable from a written one, effect recording included. Kinds beyond
        /// define/undef keep the old behaviour and flow into the stream as code: a macro-made
        /// <c>#if</c> would need conditional-stack surgery mid-run, and nothing observed asks
        /// for it.
        /// </remarks>
        private void ProcessMacroMadeDirective(SourceText text, List<Token> lineTokens)
        {
            // Only the HEAD macro expands. A directive's arguments are raw to dm.exe — `#undef
            // FOO` names FOO, it does not expand it — and the first version expanded the whole
            // line, so `U FOO` rendered as `#undef 2` and undefined nothing. The rest of the
            // line joins as written.
            IReadOnlyList<ExpandedToken> produced =
                MacroExpander.Expand(text, lineTokens.GetRange(0, 1), Macros, Diagnostics);

            System.Text.StringBuilder rendered = new();

            foreach (ExpandedToken token in produced)
            {
                if (token.Kind == TokenKind.Newline)
                    continue;

                rendered.Append(token.Source.ToString(token.Span));

                // The hash glues to the keyword after it; everything else separates.
                if (token.Kind != TokenKind.Hash)
                    rendered.Append(' ');
            }

            for (int i = 1; i < lineTokens.Count; i++)
            {
                rendered.Append(text.ToString(lineTokens[i].Span));
                rendered.Append(' ');
            }

            SourceText synthetic = SourceText.From(rendered.ToString() + "\n", "<macro-made-directive>");
            LexResult lex = Lexer.Lex(synthetic);
            IReadOnlyList<Directive> directives = DirectiveScanner.Scan(lex);
            TextSpan at = lineTokens[0].Span;

            switch (directives.Count > 0 ? directives[0].Kind : DirectiveKind.Unknown)
            {
                case DirectiveKind.Define:
                {
                    // Parse errors land at the USE line rather than inside the synthetic text.
                    List<Diagnostic> scratch = new();

                    if (MacroDefinition.Parse(lex, directives[0], scratch) is { } macro)
                    {
                        Macros.Define(macro);
                        _recording?.Add(EffectStep.ForDefine(macro));
                    }

                    foreach (Diagnostic diagnostic in scratch)
                        Diagnostics.Add(new Diagnostic(diagnostic.Id, diagnostic.Severity, at, diagnostic.Message));

                    break;
                }

                case DirectiveKind.Undef when directives[0].HasArguments:
                    string undefined = lex.GetText(lex.Tokens[directives[0].ArgumentStart]);
                    Macros.Undefine(undefined);
                    _recording?.Add(EffectStep.ForUndef(undefined));
                    break;

                default:
                    Emit(text, produced);
                    break;
            }
        }

        /// <summary>
        /// Emits a stretch of tokens for the file being walked, recording it as part of that file's
        /// effect.
        /// </summary>
        /// <remarks>
        /// One array serves both purposes: it is appended to the file's run, and it is the payload a
        /// replay appends when the file has not changed. Emission and recording are the same call so
        /// that a new emission site cannot be added without its tokens being recorded — a replay
        /// that silently dropped part of a file would be an awful bug to find.
        /// </remarks>
        private void Emit(SourceText origin, IReadOnlyList<ExpandedToken> produced)
        {
            if (Runs is null || produced.Count == 0)
                return;

            // Below an expression-position include everything joins the including file's run, so
            // the spliced expression is whole for the parser. Recording is off for the whole
            // subtree (see WalkCore) and the including walk is poisoned, so the bogus step offset
            // this would produce is never written.
            SourceText target = _inlineTarget ?? origin;

            // Where this stretch lands inside the file's own run, which is what a replay needs to
            // put it back in compile order without carrying a copy of it.
            int start = Runs.LengthOf(target) - _runStart;

            Runs.Append(target, produced);
            _recording?.Add(EffectStep.ForTokens(start, produced.Count));
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

        /// <summary>
        /// Works out which file an <c>#include</c> names, reporting why if it cannot.
        /// </summary>
        /// <remarks>
        /// Split from <see cref="EnterInclude"/> because the two halves belong to different files.
        /// Resolution is this file's business and its diagnostics are part of this file's effect;
        /// what happens after belongs to the file being included, which records its own.
        /// </remarks>
        private string? ResolveInclude(
            LexResult lex, Directive directive, string path, out bool isLibrary, out IncludeSite site)
        {
            isLibrary = false;
            site = default;

            if (!IncludeDirective.TryRead(lex, directive, out IncludeDirective include))
            {
                Diagnostics.Add(Diagnostic.Error("DM0105", directive.Span, "malformed #include"));
                return null;
            }

            isLibrary = include.IsLibrary;
            site = new IncludeSite(include.Target, include.Span);

            string? resolved = Resolve(include, path, out string attempted);

            if (resolved is null)
            {
                Diagnostics.Add(Diagnostic.Error(
                    "DM0101",
                    include.Span,
                    $"unable to open \"{include.Target}\" (looked for {attempted})"));

                return null;
            }

            return resolved;
        }

        /// <summary>
        /// Descends into an included file, or reports that the compiler would ignore the repeat.
        /// </summary>
        /// <remarks>
        /// Called both by the walk and by a replay, and it must behave the same either way: the
        /// include-once check reads <c>_seen</c>, which a replay grows in the same order, so a
        /// repeat is still a repeat and the file that is reached is still reached once.
        /// </remarks>
        private void EnterInclude(
            string resolved,
            string path,
            int depth,
            bool fromLibrary,
            IncludeSite site,
            SourceText? inlineInto = null)
        {
            if (_seen.Contains(resolved) && !_reincludable.Contains(resolved))
            {
                // The compiler ignores a repeat silently. Worth surfacing, not worth failing: real
                // .dme files hit this when DreamMaker's generated block re-adds a manual entry.
                Diagnostics.Add(new Diagnostic(
                    "DM0102",
                    DiagnosticSeverity.Information,
                    site.Span,
                    $"\"{site.Target}\" was already included; the compiler ignores the repeat"));

                return;
            }

            // `__MAIN__` is defined only while processing the .dme itself. Included files must not
            // see it, and it has to come back when we return to the .dme's remaining directives.
            bool wasMain = Macros.IsDefined(MainMacro);
            if (wasMain)
                Macros.Undefine(MainMacro);

            SourceText? outerInline = _inlineTarget;
            if (inlineInto is not null)
                _inlineTarget = inlineInto;

            try
            {
                Walk(resolved, path, depth + 1, fromLibrary);
            }
            finally
            {
                _inlineTarget = outerInline;
            }

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
