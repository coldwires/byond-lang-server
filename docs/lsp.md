# The LSP server, for clients that are not VS Code

`Dm.Lsp` over stdio. Hand-rolled JSON-RPC, spec 3.17 subset, no dependencies.

> **Live document.** A method or a capability that changes belongs in this file in the same commit.
> Last updated: 2026-08-13.

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

**`environmentFile`** is the `.dme` to analyse. Without it the server takes **the first `.dme` in
the workspace root**, non-recursively — a `.dme` is the project and real ones sit at the top. A
project with several `.dme` files gets an arbitrary one of them, so send this field if you can.

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

With neither a root nor a `.dme`, analysis is off and stderr says so.

---

## Positions

**Zero-based line and character, UTF-16 code units**, as LSP requires.

The server declares `positionEncoding: "utf-16"` in its `initialize` result and **does not read the
client's `general.positionEncodings`**. Every conformant 3.17 client supports UTF-16, so this is
safe in practice; a client that offers only UTF-8 will be mis-served, silently and only on lines
containing non-ASCII text. If that is you, say so — the core takes the encoding as a parameter
throughout and the C ABI already exposes both, so the fix is small.

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
| Sync | `didOpen`, `didChange`, `didClose` (full) |
| Diagnostics | `publishDiagnostics` — syntax **and** the binder's semantic set |
| Read | `completion` + `completionItem/resolve`, `hover`, `signatureHelp`, `definition`, `typeDefinition`, `implementation`, `references`, `documentHighlight`, `documentSymbol`, `workspace/symbol` |
| Write | `rename` — **best-effort by design**: the `WorkspaceEdit` carries only sites *proven* to be the symbol, a refusal answers `null`, and both the refusal reason and the count of uncertain sites (`:` accesses, untyped receivers, string dispatch) arrive as a `window/showMessage` warning, since the standard response has no field for either. `dm/rename` below returns the full list |
| Editor | `semanticTokens/full`, `inlayHint`, `foldingRange`, `documentLink`, `documentColor`, `colorPresentation` |
| Server → client | `window/workDoneProgress/create` + `$/progress` |

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
| `dm/references` | `path`, `limit` | every use of a symbol, with `kind` and `inside` |
| `dm/fileInProject` | `textDocument` | `{ file, inProject, environmentFile }` |
| `dm/iconStates` | `uri` | every state in a `.dmi` |
| `dm/rename` | `textDocument`, `position`, `newName` | the full rename answer: `refusal` word, provable `edits`, and every `uncertain` site with a `reason` (`colonAccess`, `untypedReceiver`, `stringLiteral`). Same shape as `dm_rename_at` |
| `dm/tickFile` / `dm/untickFile` | `textDocument` | a `.dme` edit as `{ uri, text, refusal }` |

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
| `documentSymbol` | `owner` | The resolved path of whatever contains the symbol |

There is **no field for a completion context** on this surface. The ABI reports one — `Identifier`,
`Member`, `SubtypeMember`, `TypePath`, `InputType`, `ReturnValue` — and LSP has nowhere to put it,
so a client that wants to show nothing after a bare leading `.` cannot tell that case from an
unresolvable receiver here.

---

## Deliberately not served

| | |
|---|---|
| `textDocument/codeAction` | Near the front of the queue, unblocked by the diagnostics work |
| `textDocument/formatting` | Not started |
| `workspace/didChangeWatchedFiles` | Not wired. Send `didChange` for files that change outside the editor, or restart |
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
