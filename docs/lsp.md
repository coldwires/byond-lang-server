# The LSP server, for clients that are not VS Code

`Dm.Lsp` over stdio. Hand-rolled JSON-RPC, spec 3.17 subset, no dependencies.

> **Live document.** A method or a capability that changes belongs in this file in the same commit.
> Last updated: 2026-08-16.

**What this file is for.** `editors/vscode/README.md` is a recipe for one client and carries the
settings in that client's spelling; everything an integrator needs was reachable only through it.
This file is the protocol: how to launch the server, what it must be told at `initialize`, and what
it answers.

Three things live elsewhere, once each:

| For | Read |
|---|---|
| What an answer **means** — the inference divergence, the `.`/`:` split, what an empty list implies | `INTEGRATION.txt` §4. It is written for the C ABI and the semantics are identical, because both shells call the same services |
| Whether a capability exists on all three surfaces | `docs/capability-matrix.md` |
| The `dm/*` request and response shapes | `abi/schema/`, which they mirror field for field |

---

## Run it

```
dotnet build src/Dm.Lsp
dotnet run --project src/Dm.Lsp        # or the built dm-lsp binary
```

Speaks stdio and nothing else. **Command-line arguments are ignored** — the VS Code client passes
`--stdio` and the server never reads it, so a client that insists on passing a transport flag is
harmless. There is no TCP mode.

**One server holds one workspace, and in a multi-root window the first folder wins.** The root is
read from `rootUri`, then `rootPath`, then the first entry of `workspaceFolders`; the rest are
ignored. Two games in one window therefore analyse as one project and a pile of out-of-project
files — run one server per game instead, which is what every client here does per window.

Errors and status go to **stderr**, which is worth wiring into your client's log: the standalone
fallback below announces itself there and is otherwise invisible.

---

## `initializationOptions` — the part that decides every answer

Two fields, both optional, both consequential.

```json
{
  "initializationOptions": {
    "environmentFile": "tgstation.dme",
    "defines": ["CBT"]
  }
}
```

**`environmentFile`** is the `.dme` to analyse. Without it the server discovers one in two steps:
**the first `.dme` in the workspace root** (non-recursively) at `initialize`, then **proximity at
the first `didOpen`** — the nearest `.dme` walking up from that document wins, so a game nested
below the workspace root is found from its own files, which the root scan never could. One
workspace, so the first opened DM document decides for the session; an explicit `environmentFile`
is never second-guessed, and a file outside the workspace root neither names a project nor
consumes the decision. Discovery announces what it settled on through the `dm/environment`
notification (below). A workspace with several `.dme` files can still get one you did not mean,
so send this field if you can.

A discovered `.dme` cannot know what the build passes to `dm.exe -D`, so when discovery settles
with **no `defines` configured**, the server also sends one `window/showMessage` (Info) saying
which `.dme` it picked and that analysis without the build's defines describes a different
program. Configuring either option suppresses the note.

**`defines`** is what the project's build passes to `dm.exe -D`, in the compiler's own spelling:

```
"NAME"            defined EMPTY, not 1 — this matches dm.exe
"NAME=value"
"FN(x)=((x)*2)"   function-like
```

A workspace without them describes **a different program** from the one the build produces: code
behind a guard is invisible, or visible when it should not be. /tg/station builds with `-DCBT`.

**Both are read once, at `initialize`.** `workspace/didChangeConfiguration` is accepted and
ignored, so changing either at runtime does nothing until the server is restarted. Say so in your
client's UI rather than letting a user change a setting and watch nothing happen.

### With no `.dme`

The server does not refuse to start. It opens the workspace root in **standalone** mode, where
every file is its own compilation unit of the builtins plus itself, and prints one line to stderr:

```
dm-lsp: no .dme under <root>; analysing each file on its own.
Point dm.environmentFile at one for cross-file resolution.
```

Cross-file resolution is what is lost. A standalone file completes its own procs and vars and
BYOND's builtins, and nothing from the file beside it — which is what `dm.exe` compiling that file
alone would also resolve. `dm/fileInProject` reports every file as outside a project in this mode.

Standalone at `initialize` is not final: the first opened document still runs the proximity walk
above, so a `.dme` nested below the root is adopted the moment a file near it is opened.

With neither a root nor a `.dme`, the first opened file picks the nearest project above it; if
that walk finds nothing either, the file's own directory becomes a standalone workspace.

---

## Positions

**Zero-based line and character**, in the negotiated encoding's units.

The server reads the client's `general.positionEncodings` at `initialize` and answers with the
**first entry it speaks** — `utf-16` or `utf-8` — so the client's preference order is honoured. A
client that sends nothing gets UTF-16, the LSP default, and the chosen encoding is declared back
as `positionEncoding` in the result. Both directions use it: positions you send and every range
the server answers.

`dmc`, the CLI arbiter, is **1-based**. Add one when reproducing what your editor asked for.

---

## Document sync

`textDocumentSync: 1` — **full text only**. Send the whole document on every `didChange`.

Incremental sync is a deliberate omission rather than an oversight: a full-sync change costs one
string against a rebuild measured at ~10 ms on a real game. If you profile it and it hurts, that
number is what to argue against.

A pushed buffer is authoritative until `didClose`: once a file has been opened, disk is never
consulted for it again.

---

## Standard methods served

| | |
|---|---|
| Lifecycle | `initialize`, `initialized`, `shutdown`, `exit`, `$/setTrace`, `$/cancelRequest` |
| Sync | `didOpen`, `didChange`, `didClose` (full); `workspace/didChangeWatchedFiles` — send it when the disk changes outside the editor (a git checkout, a build step) and the server invalidates its caches and re-publishes diagnostics for open documents. Cheap: per-file caches revalidate by write time, so only what actually changed is re-read |
| Diagnostics | `publishDiagnostics` — syntax (the lexer's included since 2026-08-16: unterminated strings, `dm.exe`'s "inconsistent indentation"), the binder's semantic set, **and the include walk's own** since 2026-08-16: a `#warn` echo, an unterminated `#if`, a missing `#include`, an unknown pragma name. The last group belongs to the walk rather than to a file's syntax, so it is reported against the file that wrote it — one that cannot be attributed belongs to the `.dme`. These were the last set `dmc diagdiff` counted and no shell showed, which meant the zero-invented figure was measured over diagnostics no editor ever displayed |
| Read | `completion` + `completionItem/resolve`, `hover`, `signatureHelp`, `definition`, `typeDefinition`, `implementation`, `references`, `documentHighlight`, `documentSymbol`, `workspace/symbol` |
| Fix | `codeAction` — quick fixes, each carrying its `WorkspaceEdit` inline rather than behind a `Command`, so applying one needs no second round trip. One action today: **declare the type** on a member reached through an untyped local, which is the fix for the one place this analyzer knowingly disagrees with `dm.exe` (see `inferred` below). The edit is a zero-length insert immediately before the name, so `var/static/x` becomes `var/static/obj/item/x` with your modifiers left where they were. Offered only when it would actually make the access compile — a proc referenced without parentheses stays `undefined var` whatever type is written, so nothing is offered there. `context.only` is not honoured: everything served is a `quickfix` |
| Write | `rename` — **best-effort by design**: the `WorkspaceEdit` carries only sites *proven* to be the symbol, a refusal answers `null`, and both the refusal reason and the count of uncertain sites (`:` accesses, untyped receivers, string dispatch) arrive as a `window/showMessage` warning, since the standard response has no field for either. `dm/rename` below returns the full list |
| Editor | `semanticTokens/full`, `inlayHint`, `foldingRange`, `documentLink`, `documentColor`, `colorPresentation` |
| Server → client | `window/workDoneProgress/create` + `$/progress`; `dm/environment` (below); `window/showMessage` for rename's uncertainty and the auto-discovery defines note |

Trigger characters: `.` `:` `/` for completion, `(` `,` for signature help.

**`$/progress` announces the first tree build.** The one call that pays for the whole project
announces itself — create, begin, answer, end — so a cold query reads as indexing instead of a
frozen UI. A warm tree stays silent, so do not treat the absence of progress as an error.

**Cancellation is real.** A request cancelled while queued answers `-32800` without running; one
cancelled mid-flight aborts at the service's next token check. A reader thread intercepts cancels
ahead of the queue, because in-order delivery behind a work queue could never cancel anything.

**Diagnostics obey `#pragma ignore`.** Some carry `dm.exe`'s own warning names — `new_name`,
`no_parent` — and a project that silences one in source has silenced it here. Numeric ids work too,
`push`/`pop` scope it, and the level flows through include order. A diagnostic disappearing after
an edit to some *other* file may be a pragma rather than a bug.

---

## Custom `dm/*` methods

For what LSP cannot express. Responses mirror `abi/schema/` field for field; the deltas from the
ABI are listed below rather than the whole shape.

| Method | Params | Answers |
|---|---|---|
| `dm/objectTree` | `path` (default `/`), `depth`, `includeBuiltins` | one node and its children |
| `dm/subtypesOf` | `path`, `limit` | everything beneath a path, flat, with `truncated` |
| `dm/members` | `path`, `inherited` | a type's vars and procs, inheritance resolved |
| `dm/ancestorsOf` | `path` | the inheritance chain, nearest first, self excluded |
| `dm/overriddenProc` | `path`, `name` | what that type's definition of the proc overrides: `{ overrides, owner, builtin }`. `overrides: false` is an answer — a fresh declaration overrides nothing, which is what `no_parent` reports on. The builtin case is the type's own, so `/mob/Login()` answers `/mob` with `builtin: true` and there is nothing to open |
| `dm/dmeEntries` | — | every file DreamMaker's include block lists, in file order. The BLOCK, not the include graph: a file included from another `.dm` never appears, and an entry inside an `#if` is skipped |
| `dm/references` | `path`, `limit` | every use of a symbol, with `kind` and `inside` |
| `dm/fileInProject` | `textDocument` | `{ file, inProject, environmentFile }` |
| `dm/iconStates` | `uri` | every state in a `.dmi` |
| `dm/rename` | `textDocument`, `position`, `newName` | the full rename answer: `refusal` word, provable `edits`, and every `uncertain` site with a `reason` (`colonAccess`, `untypedReceiver`, `stringLiteral`). Same shape as `dm_rename_at` |
| `dm/tickFile` / `dm/untickFile` | `textDocument` | a `.dme` edit as `{ uri, text, refusal }` |

One custom **notification**, server → client:

| Method | Params | When |
|---|---|---|
| `dm/environment` | `{ environmentFile, autoDiscovered }` | auto-discovery settles, at the first `didOpen`. `environmentFile` is the absolute path being analysed, or null when none was found. Not sent when the client configured `environmentFile` — it already knows. Wire it into whatever shows the active project; the VS Code client's status bar consumes it |

**Deltas from the ABI shapes:**

- `dm/references` hits carry a **`uri` alongside `file`**, so a client can navigate without
  converting paths itself.
- The tickmark methods return an edit rather than writing the `.dme`, and the offsets index the
  text **this workspace currently sees**. If you hold unsaved changes, push the `.dme` through
  `didChange` first and apply the edit to that same text. `refusal` is always present as a word:
  `none`, `noChange`, `noBlock`, `conditional`.
- A path the tree does not hold is **`-32803`**, the LSP spelling of `DM_ERR_NOT_FOUND`. An empty
  array with a success response is an answer, not an error — a type with no subtypes.

**`dm/fileInProject` is the one to wire early.** A buffer for a file the `.dme` never includes
analyses fine per-file — outline, colours, syntax diagnostics — while its own declarations resolve
nowhere, *and* symbols from project files resolve normally in the same buffer. That asymmetry reads
exactly like a broken client. It cost one integrator a live debugging session.

---

## Nonstandard fields on standard responses

Spec-only clients ignore these. They are additive and will not break a strict parser.

| Response | Field | Why |
|---|---|---|
| `completion` items | `inferred` | The receiver's type was worked out rather than written, which per `INTEGRATION.txt` §4 is exactly what `dm.exe` refuses. Badge, rank down, or drop |
| | `typeFrom` | Which route produced the type: `initializer`, `assignment`, `as`, `none`. Sent only alongside `inferred`, so `written` never appears on this surface. `bareTypeName` left at ABI 0.26 with the fallback that produced it |
| | `type`, `value` | The item's own declared type and its initialiser as written |
| | `constant` | What the initialiser comes to, when it folds — `300` for `= 5 * 60`, and `35` for `= MAX_HP - 5` when `MAX_HP` is a `const` the owner can see (its inheritance chain, then the globals; the `/path::NAME` static form too). Absent when it does not fold, and absent for a bare literal. Rendered as DM renders a number: six significant digits, 32-bit floats |
| `hover` | a `= \`300\`` line | The same folded value, as markdown rather than a field, so it needs no client code |
| `documentSymbol` | `owner` | The resolved path of whatever contains the symbol |

There is **no field for a completion context** on this surface. The ABI reports one — `Identifier`,
`Member`, `SubtypeMember`, `TypePath`, `InputType`, `ReturnValue` — and LSP has nowhere to put it,
so a client that wants to show nothing after a bare leading `.` cannot tell that case from an
unresolvable receiver here.

---

## Deliberately not served

| | |
|---|---|
| `textDocument/formatting` | Not started |
| Incremental sync | See above |

---

## Client configuration

**Neither of these has been run by anyone.** The server is exercised by in-process protocol tests
and by the VS Code client; a config below that does not work is a bug worth reporting, and it is
more likely to be in the snippet than in the server.

Neovim:

```lua
vim.filetype.add({ extension = { dm = "dm", dme = "dm" } })

vim.api.nvim_create_autocmd("FileType", {
  pattern = "dm",
  callback = function(args)
    vim.lsp.start({
      name = "dm-lsp",
      cmd = { "dotnet", "run", "--project", "/path/to/src/Dm.Lsp" },
      root_dir = vim.fs.root(args.buf, function(name) return name:match("%.dme$") end),
      init_options = { environmentFile = "yourgame.dme", defines = { "CBT" } },
    })
  end,
})
```

Helix, in `languages.toml`:

```toml
[language-server.dm-lsp]
command = "dm-lsp"
config = { environmentFile = "yourgame.dme", defines = ["CBT"] }

[[language]]
name = "dm"
scope = "source.dm"
file-types = ["dm", "dme"]
roots = ["*.dme"]
language-servers = ["dm-lsp"]
```

Point `root_dir` / `roots` at the directory holding the `.dme`. The server resolves a relative
`environmentFile` against the workspace root it was given.

---

## When something looks wrong

`dmc` is the arbiter and runs the same code path:

```
dotnet run --project src/Dm.Cli -- hover <dme> <file> <line> <col>
dotnet run --project src/Dm.Cli -- complete|definition|signature|references|hints <...>
```

**CLI positions are 1-based, LSP's are 0-based.** If the CLI agrees with your editor, the bug is
ours — report it with the smallest `.dm` that shows it. If the CLI looks right and your client does
not, it is in the integration.

Every `dmc` command that reads a `.dme` takes `-DNAME`, so a query can be reproduced with the same
defines you sent at `initialize`.
