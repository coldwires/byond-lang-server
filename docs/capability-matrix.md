# Capability matrix

The §3 sync rule made checkable: anything the C# IDE can reach in-process must have an ABI
equivalent and an LSP equivalent, or the direct-reference path outgrows the other two shells.
Live document — a new capability adds a row **in the same change**, and a row with a gap is the
work list.

| Capability | In-process (`Dm.Core`) | C ABI (`dm_core.dll`) | LSP (`Dm.Lsp`) |
|---|---|---|---|
| Classification (lexical + semantic colours) | `ClassificationService` | `dm_classify_range` (0.2) | `semanticTokens/full`, split per line, over a small TextMate base |
| Buffers (unsaved text is authoritative) | `Workspace.SetBuffer` | `dm_set_buffer` / `dm_close_buffer` | `didOpen`/`didChange`/`didClose`, full sync |
| Injected defines | `Workspace.SetDefines` | `dm_set_defines` (0.5) | `initializationOptions.defines` |
| Outline + syntax diagnostics | `DocumentSymbolService`, `ParseResult.Diagnostics` | `dm_document_symbols` (0.3, 0.10 severity) | `documentSymbol` + `publishDiagnostics` |
| Semantic diagnostics (binder) | `Binder.Bind` | `dm_diagnostics` (0.13) | `publishDiagnostics` carries them |
| Completion | `CompletionService` | `dm_complete_at` (0.4) | `completion` |
| Definition | `DefinitionService` | `dm_definition_at` (0.6) | `definition` |
| Hover | `HoverService` | `dm_hover_at` (0.7) | `hover` |
| Workspace symbols | `WorkspaceSymbolService` | `dm_workspace_symbols` (0.8) | `workspace/symbol` |
| Object-tree bulk queries | `TreeQueryService` | `dm_query_json` (0.11) | `dm/objectTree`, `dm/subtypesOf`, `dm/members` — same shapes as `abi/schema/` |
| Signature help | `SignatureHelpService` | `dm_signature_at` (0.12) | `signatureHelp`, triggers `(` and `,` |
| Reference index (uses, kinds, enclosing symbol) | `ReferenceService` over the binder's walk | `dm_query_json` `references` (0.14) | `references`, `documentHighlight`, `dm/references` |
| Ancestor chain in one call | `ObjectTree.InheritanceChain` | `dm_query_json` `ancestorsOf` (0.14) | `dm/ancestorsOf` |
| Disk-change invalidation | `Workspace.Invalidate` | `dm_invalidate` (0.14) | `didChangeWatchedFiles` — ⬜ not yet wired; clients can send didChange |
| Readiness + warm-at-open | `Workspace.IsTreeBuilt`, `GetObjectTree` | `dm_tree_ready`, `dm_build_tree` (0.15) | `$/progress` announces the build (push, the LSP idiom for the same fact) |
| Inlay hints (inferred local types) | `InlayHintService` | `dm_inlay_hints` (0.16) | `textDocument/inlayHint` |
| Lazy completion documentation | `CompleteBriefAt` + `ResolveDocumentation` | `dm_complete_brief`, `dm_complete_resolve` (0.17) | `resolveProvider` + `completionItem/resolve` |
| Completion ranking + opt-in cap | `CompletionResult.Truncated`, scope-distance order | `dm_set_completion_limit` (0.18), `truncated` | `isIncomplete` + `sortText` |
| Per-item declared type + initial value | `CompletionItem.DeclaredType`, `.InitialValue` | `dm_complete_at` `type` / `value` (0.21) | `completion`, nonstandard `type` / `value` |
| Which route typed a receiver | `CompletionItem.TypeSource` | `dm_complete_at` `typeFrom`, always (0.22) | `completion`, nonstandard `typeFrom` — sent only alongside `inferred`, so `written` never appears |
| `as` input-filter vocabulary | `CompletionContext.InputType` | `dm_complete_at` `context: "InputType"` | `completion` — the items, but no context word: LSP has no field for it |
| DM Reference links on builtins | `DefinitionLocation.Reference` | `dm_hover_at` `reference` | `hover` — a `[DM Reference](url)` line in the markdown, which needs no client code |
| Go-to-type-definition | `DefinitionService.TypeDefinitionAt` | `dm_type_definition_at` (0.19) | `typeDefinition` |
| What-overrides-this | `ReferenceService`, `kind: override` | `dm_query_json` `references` (0.14) | `implementation` |
| Folding ranges | `FoldingService` | `dm_folding_ranges` (0.19) | `foldingRange` |
| Document links (`#include`) | `DocumentLinkService` | `dm_document_links` (0.19) | `documentLink` |
| Is this file in the project | `Workspace.IsFileInProject` | `dm_file_in_project` (0.19) | `dm/fileInProject` |
| Standalone (no `.dme`) analysis | `Workspace.OpenStandalone`, `GetTreeFor` | `dm_workspace_open_standalone` (0.20) | falls back automatically when no `.dme` is found |
| `.dme` tickmarks (tick/untick) | `DmeIncludeBlock`, `Workspace.TickFile`/`UntickFile` | `dm_dme_is_ticked`, `dm_dme_tick`, `dm_dme_untick` (0.20) | `dm/tickFile`, `dm/untickFile` + the client's toggle command |
| Workspace-symbol kind filters | `WorkspaceSymbolService` (`var/`, `proc/`, `verb/`, `#`) | `dm_workspace_symbols` (0.8) — filters ride in the query string | `workspace/symbol` |
| Object-tree panel | `TreeQueryService` | `dm_query_json` (0.11) | `dm/objectTree` + the client's Explorer view |
| Colour swatches (`rgb()`, `"#rrggbb"`) | `ColorService` | `dm_document_colors` (0.23) | `documentColor` + `colorPresentation`, components as 0-1 floats |
| `.dmi` icon states | `Dm.Assets.DmiReader` | `dm_icon_states` (0.24) | `dm/iconStates` — served, and **no client asks yet**: the VS Code extension has no icon browser |

**No gaps remain on any surface** — M8 closed the last blank row on 2026-08-08. What the `.dmi`
row records instead is a different kind of gap and the one this project has been caught by before:
the server answers `dm/iconStates` and the VS Code client never asks, exactly as it never asked for
`dm/objectTree` for two milestones. A row is not parity until something calls it.

Before that, the three editor-shaped rows — type-definition, folding, document links — were briefly
blank on the reasoning that no in-process consumer had asked; the user asked, and they went in at
0.19 alongside `dm_file_in_project`.

Positions: the ABI and CLI speak both encodings by parameter; LSP negotiates and uses UTF-16.
CLI lines/columns are 1-based, ABI and LSP 0-based.
