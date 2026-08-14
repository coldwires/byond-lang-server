# Dm.Core, in process

The C# surface, for a host that references the assembly directly — the third shell beside the
C ABI (`INTEGRATION.txt`) and the LSP (`docs/lsp.md`).

> **Live document.** A public type appearing, disappearing or changing meaning belongs in this
> file in the same commit. Last updated: 2026-08-13.

**What this file is for.** The XML documentation ships with the assembly and carries every
member's contract — an undocumented public member fails the build, so IntelliSense is the
reference. This file is the map that documentation cannot be: what the surface is, how the pieces
compose, and which of them you actually call. What an *answer means* — the inference divergence,
the `.`/`:` split, what an empty list implies — is `INTEGRATION.txt` §4, once, for all three
shells, because they run the same services.

## The rule that shapes the surface

Public is what `Dm.Native` and `Dm.Lsp` consume — PLAN.md §3's sync rule made structural, so the
in-process surface cannot outgrow the other two shells. The lexer, parser, preprocessor and cache
layers are `internal`; `dmc` and the tests reach them via `InternalsVisibleTo`, a host does not.
Everything public is supported API and documented; decided 2026-08-13.

## Workspace, the root object

`Workspace` owns a project and everything derived from it. One workspace is used from one thread
at a time — a contract, not a suggestion.

```csharp
using Workspace ws = Workspace.Open("game.dme");          // or Open(dme, defines)
ws.SetDefines(new[] { "CBT" });                           // what the build passes to dm.exe -D
ws.SetBuffer(path, editorText);                           // unsaved text is authoritative
ObjectTree tree = ws.GetObjectTree();                     // built on demand, cached until a change
```

- **Open** `Open(dme)`, `Open(dme, defines)`, `OpenStandalone(root)` — the standalone form has no
  project: every file is a compilation unit of the builtins plus itself. `Dispose` releases it.
- **Buffers** `SetBuffer`, `CloseBuffer`, `HasBuffer` — a pushed buffer wins over disk until
  closed. `GetDocument` / `TryGetDocument` / `GetFileText` read through the same store.
- **The tree** `GetObjectTree`, `GetTreeFor(path)` (project tree, or builtins-plus-itself for a
  file outside it), `IsTreeBuilt` (the free readiness question), `GetProjectParses` (every file
  and its parse, compile-ordered — what project-wide queries walk).
- **Change** `SetDefines`, `Invalidate` (the disk moved: a checkout, a build — cheap, per-file
  caches revalidate by write time), `CompletionLimit`, `IconStateReader` (inject a `.dmi` reader;
  `Dm.Core` does not reference `Dm.Assets`).
- **Project questions** `IsFileInProject`, `HasEnvironmentFile`, `DmePath`, `RootDirectory`,
  `ResolvePath`, `LibraryRoot`, `Defines`.
- **`.dme` tickmarks** `IsFileTicked`, `TickFile`, `UntickFile` — an edit (`DmeEdit`) comes back
  rather than the file being written, because the `.dme` is usually open and dirty in the editor
  that asked.
- **Rename** `RenameAt` — provable edits plus the uncertain sites, orchestrated here because the
  parses, macros and per-file lex all live here.

## The services

Static classes over `(ObjectTree, Document, position…)`, each returning its own result type.
Positions are zero-based; every positional call takes a `PositionEncoding`; a `CancellationToken`
aborts at the next token check.

| Ask | Service → result |
|---|---|
| Colours for a span or lines | `ClassificationService` → `ClassifiedSpan` |
| What can be typed here | `CompletionService` → `CompletionResult` / `CompletionItem` |
| Where is this declared | `DefinitionService` → `DefinitionLocation` (a list — types reopen, procs override) |
| What is this | `HoverService` → `HoverResult` |
| Which call am I in | `SignatureHelpService` → `SignatureHelpResult` |
| Every use of a symbol | `ReferenceService` → `ReferenceListing` / `Reference` |
| Rename, best-effort | `RenameService` → `RenameResult` (or `Workspace.RenameAt`) |
| Semantic diagnostics | `Binder.Bind` → `Diagnostic` list (syntax half: `ParseResult.Diagnostics`) |
| Outline | `DocumentSymbolService` → `DocumentSymbol` |
| Search by name | `WorkspaceSymbolService` → `WorkspaceSymbol` |
| Tree panel / bulk queries | `TreeQueryService` → `TreeNode`, `TypeMembers`, `SubtypeListing` |
| Inlay hints | `InlayHintService` → `InlayHint` |
| Folding, links, colours | `FoldingService`, `DocumentLinkService`, `ColorService` |

The first call that needs the tree pays for building it; everything after answers from the cache
until a buffer, define or `Invalidate` changes it.

## Symbols, text, diagnostics

- `ObjectTree` — `Find` by `TypePath` or string (null when absent), `Root` (globals live there,
  and it is deliberately not in any inheritance chain), `InheritanceChain`, `Types`, `Count`.
  `TypeSymbol` carries `Path`, `Name`, `Children`, `IsBuiltin`, `IsDeclared`.
- `TypePath` — the value type for `/obj/item`-style paths: `Parse`, `FromSegments`, `Append`,
  ordinal ordering, `Text`.
- `SourceText` / `TextSpan` / `LinePosition` — offsets are UTF-16 code units, `End` exclusive;
  `GetLinePosition` and `GetOffset` convert in either encoding.
- `Diagnostic` — `Id` (the `DMxxxx` word, shared with `dm.exe`'s numbering where one exists),
  `Severity`, `Span`, `Message`, `File`.

## Opaque handles

`MacroTable` (`GetMacroTable`), `SemanticContext` (`GetSemanticContext`), `LexResult`
(`Document.Lex`), and `FileSyntax` / `SyntaxNode` (`ParseResult.Root`) are public so they can be
obtained from the workspace and handed to the services that want them. Their internals are not
API: a host shuttles them, the services read them. The AST behind `SyntaxNode` is `internal` on
purpose — an in-process consumer gets the same analysis surface the other shells get, not a
parser.

## When something looks wrong

`dmc` runs the same code path and is the arbiter, exactly as for the other shells — see the
bottom of `docs/lsp.md` for the recipe. CLI positions are 1-based; this API's are 0-based.
