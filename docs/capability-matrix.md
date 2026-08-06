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
| `.dmi` icon states | ⬜ M8, unscheduled | ⬜ | ⬜ |

Positions: the ABI and CLI speak both encodings by parameter; LSP negotiates and uses UTF-16.
CLI lines/columns are 1-based, ABI and LSP 0-based.
