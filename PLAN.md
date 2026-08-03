# DM Analysis Library + Language Server — Design

> **Live document.** Updated as the project progresses. Milestone status, decisions, and
> open questions are kept current here. See `ROADMAP.txt` for the short version.
>
> Status: **M0–M3 complete (CI outstanding) · M4 declarations done** · Last updated: 2026-08-03

---

## 1. Context

BYOND has no general-purpose DM tooling. SpacemanDMM is the only mature option: Rust, oriented
toward SS13, and built as a language server rather than an embeddable library.

The immediate driver is a three-person team on one game codebase. Two are writing custom IDEs —
one in a proprietary FNA/C# window, one in Qt C++, a third in an undecided language. None can use
an existing editor's DM support.

The broader goal is to be the general DM language server BYOND lacks, with the team's IDEs as the
first consumers.

One analysis core, two shells:

- **C ABI** (`dm_core.dll`) — custom IDEs embed the analyzer in-process with direct object-tree access.
- **LSP server** (`Dm.Lsp`) — VS Code / Neovim / Helix users get DM support without adopting an IDE.

The core is roughly 85% of the work and is shared by both. The C ABI ships first because it
unblocks the team.

**Acceptance target:** `mob.` lists `/mob`'s procs and vars including inherited and BYOND builtins.
`var/mob/test/t` followed by `t.` lists `/mob/test`'s members. Plus syntax highlighting, document
and workspace symbols, browsable object tree, `.dmi` icon-state enumeration, syntax diagnostics.

---

## 2. Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Core language | C# | Primary author's language. NativeAOT gives native consumers a C ABI. |
| Native distribution | NativeAOT shared library | `dm_core.dll` / `.so`, consumable from any language with FFI. |
| Boundary style | Hybrid | Opaque handles on hot paths, serialized JSON for bulk queries. |
| Parser depth | Full AST | Unblocks diagnostics, references, and inference without a rewrite. |
| Shells | Both, C ABI first | LSP added at M10 once the core is proven. |
| Audience | Public | Team IDEs are first consumers, not the only ones. |
| Concurrency (v1) | Single-threaded per workspace handle | Documented contract. Background analysis deferred. |
| Path ambiguity | Match the compiler, then warn | See §4a. Disagreeing with `dm.exe` is worse than being unhelpful. |

---

## 3. Architecture

```
  Dm.Core  (managed C#, NativeAOT-clean)
      |                                    <-- FNA/C# IDE references this directly
      +-- Dm.Assets  (.dmi icon-state reader)
      |
      +-- Dm.Native  (NativeAOT, [UnmanagedCallersOnly])   [v1]
      |       |
      |       v  dm_core.dll / libdm_core.so  +  abi/dm_core.h
      |       +-- Qt C++ IDE      (extern "C", or dm_core.hpp RAII wrapper)
      |       +-- IDE #3          (any language with C FFI)
      |
      +-- Dm.Lsp  (stdio/TCP JSON-RPC server)              [M10]
              +-- VS Code (+ extension & TextMate grammar), Neovim, Helix
```

`Dm.Lsp` is a normal .NET app and carries no NativeAOT constraints, so it can use a
reflection-based library such as OmniSharp.Extensions.LanguageServer. Only `Dm.Native` is an AOT
target. `Dm.Core` is shared by both, so the AOT constraints bind the core.

Whether to AOT `Dm.Lsp` anyway for startup latency — which would force hand-rolled JSON-RPC over
`System.Text.Json` source generators — is an M10 decision.

**Sync rule:** any capability the C# IDE uses in-process must have an ABI equivalent and an LSP
equivalent (standard method or custom `dm/*`), tracked in `docs/capability-matrix.md`. Otherwise
the direct-reference path outgrows the other two shells.

### Pipeline

```
SourceText  ->  Lexer  ->  Preprocessor  ->  Parser   ->  TypeTreeBuilder  ->  Binder
(disk +        (tokens,   (expanded        (SyntaxTree   (merged            (SemanticModel,
 in-memory      indent/    tokens +         per file,     ObjectTree +       scope chains,
 overlay)       dedent)    source map)      full AST)     builtins)          type resolution)
      |             |                            |              |                 |
      |             v                            v              v                 v
      |        Classification              Document        Workspace          Completion
      |        (M2, needs only             symbols,        symbols,           Hover
      |         the lexer)                 syntax diags    object tree        Definition
```

Classification is deliberately fed straight off the lexer. Highlighting is the first thing visible
in an editor and it needs nothing downstream.

---

## 4. LSP-readiness constraints

Locked in at M0. These are the cost of deferring the LSP shell to M10; each is cheap now and
expensive to retrofit.

- **Position encoding.** LSP defaults to UTF-16 code units; the C ABI wants UTF-8 byte offsets.
  `SourceText` exposes both. Services take an explicit encoding parameter. Retrofitting touches
  every service signature.
- **URI normalization.** `file://` vs `file:///`, Windows drive-letter casing, percent-encoding.
  Normalize at the boundary to a canonical internal path type.
- **Cancellation.** Thread a cancellation token through every service entry point even while v1 is
  synchronous. LSP requires `$/cancelRequest`; clients without it hang their UI.
- **Value-shaped service results.** A service that returns a live `SyntaxNode` cannot be serialized
  over LSP. Services return value types; the C# IDE can reach past them into `Dm.Core` directly.
- **Pushed buffers are authoritative.** When a client has pushed text for a file via
  `dm_set_buffer` (or LSP `didOpen`/`didChange`), that text is the only source for that file until
  the client releases it. Never mix a pushed buffer with disk content for the same path. This is
  what makes editor-side line-ending normalization harmless — see §4b.

---

## 4a. DM path semantics

Derived from a compiler-tested writeup against BYOND 516.1666 (see §8). These rules drive the
lexer, the parser, and the binder, and several of them are not what a reader would guess.

`/` and `.` are used in three separate contexts with **different rules in each**. Most confusion is
one context's rules being applied in another.

### The one universal rule

Mid-path, `/` and `.` are the same token. These four produce identical values, comparing equal:

```dm
/obj/item/sword    /obj/item.sword    /obj.item/sword    /obj.item.sword
```

They can be mixed inside a single path.

**Folding happens in the parser, not the lexer.** The lexer emits `Slash` and `Dot` as distinct
tokens because it cannot tell the cases apart: `a.b` is member access, `/a.b` is a path, and
`a / b` is division. Only path context decides, and that context is the parser's.

### Context 1 — the static type tree

Separators are fully interchangeable. `proc` and `verb` are **ordinary names in the tree**, not
keywords with special punctuation, so `mob/proc/attack()` and `mob.proc.attack()` declare the same
proc. Nesting is by indentation and needs no leading separator.

The trap here is unrelated to separators: **`proc/` means declare-new, and omitting it means
override.** Declaring `mob/proc/operator<<(B)` and then `mob/client/proc/operator<<(B)` is a
duplicate-definition error; the override is `mob/client/operator<<(B)`. This is an M11 diagnostic.

Note `/mob/client` is a subtype of `/mob` that happens to be named `client`. It is unrelated to the
built-in `/client`. Name resolution must be path-keyed, never name-keyed.

### Context 2 — a var declaration

The slot after `var` is not a path slot. It accepts **three** separators: `/`, `.`, and a space.

```dm
var/list/L = new
var list.L = new
var.list.L = new
```

The space form is legal as a statement but rejected inside a `for` header, so the parser cannot
treat the two positions identically.

### Context 3 — an expression

The only context where a leading `/` and a leading `.` differ.

- Leading `/` is absolute from root.
- Leading `.` is a **search, not a traversal**: check the current type, then its parent, then up to
  and including root. First hit wins.
- **No leading separator means it is not a path at all** — `obj.item.sword` is ordinary member
  access and resolves as a var lookup.

### `.` versus `:` on a member access — neither is unchecked

Both are compile-checked; they differ in *what they check against*. Verified by compiling a property
declared only on a subtype of the receiver's declared type:

| Expression | Property lives on | Result |
|---|---|---|
| `M.prop` | the declared type | compiles |
| `M.prop` | a **subtype** of the declared type | **compile error** |
| `M:prop` | a **subtype** of the declared type | compiles |
| `M:prop` | an **unrelated** type | **compile error** |

So `.` checks the declared type only; `:` widens the check to the declared type *and its subtypes*.
Calling `:` "unchecked" is wrong — it is a wider check, not an absent one. This is what completion
after `:` must offer (M6).

**`.` degrades to `:` when the receiver's type cannot be inferred.** `L[1].prop` and
`make().prop` both compile against a property that exists only on an unrelated type, because a list
lookup and a proc call have no known type to check against. So the compile-time guarantee silently
disappears exactly where it would be most useful — which is why procs have `as` return types.

The search behaviour is proximity-sensitive, so adding a nearer type silently changes what an
untouched line means. Given `/obj/item/sword` and `/obj/item/sword/magic`, a `.sword` inside
`magic` resolves to `/obj/item/sword` — until someone adds `/obj/item/sword/magic/sword`, at which
point the same line resolves somewhere else. Worth an M11 lint.

### The context-2/context-3 collision

`var.thing.T = new` compiles or fails depending on whether `/thing` was already declared **at that
point in include order**. If the type exists, the parser reads `.thing` as a context-3 relative
path and takes it as the declaration's type, shifting every slot left and leaving `var` in the name
slot — hence the misleading error `var: invalid variable name: reserved word`. If the type does not
exist yet, that reading is unavailable, the dot stays a separator, and it parses as `var/thing/T`.

Swapping two `#include` lines flips the result on an otherwise identical program.

**Decision:** match the compiler. Resolve using the same single-pass, include-order-dependent rule
so our answers agree with `dm.exe`, and emit an M11 warning on any construct whose meaning depends
on it. Diverging here would mean reporting errors the compiler does not, or missing ones it does.

### What the reference says, and where it differs

The rules above come from compiler testing. The DM Reference describes the *leading-position*
behaviour differently, and both are true — position is what distinguishes them:

- **Leading `.` is an upward search** through the code tree (reference: "search **up** in the code
  tree"). Confirmed. The reference does not state that it reaches root or that first-hit-wins; those
  are our empirical refinements.
- **Leading `:` as a downward path search does not work in 516.1666.** The reference documents
  `mob = :player` as shorthand for `/mob/player`, but every form was rejected with
  `:player: undefined type path` — in a proc local, a typed var initialiser, a type-level var, and
  inside the `/mob` branch itself. An absolute-path control compiled in the same harness. Treat as
  removed; **do not implement it**. This is the clearest example of the reference documenting
  something that is no longer true.
- **Mid-path `.`/`/` interchangeability is not documented anywhere.** The reference only ever shows
  `.` in leading position, then switches to `/` (`.Village/Guard_Post`). Our finding stands on
  compiler evidence, but **do not unify `.` and `/` in the AST** — the leading form carries search
  semantics the mid form does not.
- **`.` silently degrades to `:`** whenever the compiler cannot infer a type — "if `.` follows a
  proc call, a list lookup, or a complex expression where the type can't be known, it will act like
  `:` instead." Same for `?.` → `?:`. This is why procs have `as` return types at all.

### Lexer edge cases

- `//` inside a path starts a comment. `var x = /obj//item` evaluates to `/obj`, because the rest
  of the line is commented out. Comment detection wins over path separation.
- Doubled and trailing separators collapse: `/obj./item`, `/obj/.item`, and `/obj/item/` all mean
  `/obj/item`.

---

## 4b. Line endings

DM files are commonly CRLF on Windows. Editors normalize aggressively — Qt's `QTextDocument` and
`QPlainTextEdit::toPlainText()` always yield `\n` regardless of what was on disk, and
`QIODevice::Text` translates on read.

**Our requirements:**

- `SourceText` treats `\r\n`, bare `\n`, and lone `\r` as line terminators.
- `\r` never appears inside token text.
- Line/column positions are unaffected by ending style, since `\r` is a terminator and never sits
  inside a line. This is why all positions crossing the ABI are line/column rather than offsets.
- Combined with the pushed-buffer rule in §4, a client that normalizes its buffer to LF is
  analysing exactly what it displays, and nothing drifts.

**Client guidance** (belongs in `docs/abi.md`): detect the dominant line ending on load, normalize
to LF internally, store the original, and re-apply on save. Round-tripping is the client's job; no
editor framework does it automatically. Failing to do so rewrites every line of a file on save,
which destroys `git blame` for the rest of the team. Note also that DM's `{" ... "}` multiline
strings carry their newlines as content, so converting endings inside one changes program data,
not just formatting.

---

## 4c. Operator precedence

From the DM Reference `/operator` index. Highest binding first; everything is left-to-right except
assignment, which is right-to-left.

| # | Operators | Notes |
|---|---|---|
| 1 | `()` `.` `:` `/` `::` | **path** operators here, plus grouping/call |
| 2 | `[]` `.` `:` | index; member access |
| 3 | `?[]` `?.` `?:` | null-conditional forms of row 2 |
| 4 | `~` `!` `-` `++` `--` `*` `&` | unary. `*` and `&` are **pointer** deref/reference (515+) |
| 5 | `**` | |
| 6 | `*` `/` `%` `%%` | |
| 7 | `+` `-` | |
| 8 | `<` `<=` `>` `>=` `<=>` | `<=>` sits with the relationals |
| 9 | `<<` `>>` | shift *and* output/input share this level |
| 10 | `==` `!=` `<>` `~=` `~!` | |
| 11 | `&` | |
| 12 | `^` | |
| 13 | `\|` | |
| 14 | `&&` | |
| 15 | `\|\|` | |
| 16 | `? :` | ternary |
| 17 | `=` `+=` `-=` `*=` `/=` `%=` `%%=` `&=` `\|=` `^=` `<<=` `>>=` `:=` `&&=` `\|\|=` | **right-to-left** |
| 18 | `in` | **lowest — below assignment** |

Two traps for anyone with C instincts:

- **`in` binds looser than everything, including `=`.** `has_thing = thing in src` parses as
  `(has_thing = thing) in src`, and `!A in L` parses as `(!A) in L`. The reference calls both out.
- **Unary `*` and `&` are pointer operators** at level 4, while binary `*` and `&` are at 6 and 11.

`A #= B` is shorthand for `A = A # B` **except** for `~=` and `:=`. Note `~=` is an equivalence
*test* at level 10, not a compound assignment — easy to mis-bucket from the `=` suffix.

### Overloadable operators

Declared as a proc named `operator` immediately followed by the glyph: `operator+`, `operator[]`,
`operator[]=`, `operator""`, `operator:=`, `operator_turn`, `operator<=>`, `operator%%`. The lexer
must accept all of these as a single proc *name* in declaration position.

Not overloadable: `=` `!` `&&` `||` `&&=` `||=` `?` `==` `!=` `.` `:` `?[]`.

---

## 5. Repository layout

```
byond-lang-server/
  PLAN.md          this document
  ROADMAP.txt      short-form status
  src/
    Dm.Core/
      Text/        SourceText, FileStore, LinePositions, TextSpan, SourceMap, DocumentUri
      Syntax/      Lexer, Preprocessor, Parser, SyntaxNode/SyntaxTree, TokenKind
      Symbols/     ObjectTree, TypeSymbol, ProcSymbol, VarSymbol, TypePath
      Binding/     Binder, SemanticModel, Scope, TypeResolver
      Services/    ClassificationService, CompletionService, SymbolService, HoverService,
                   DefinitionService, DiagnosticService
      Resources/   builtins.json  (BYOND stdlib type tree)
    Dm.Assets/     DmiReader (PNG zTXt -> icon states)
    Dm.Native/     Exports.cs, HandleTable.cs, marshal helpers -> dm_core.dll
    Dm.Lsp/        JSON-RPC server over Dm.Core                          [M10]
    Dm.Cli/        dev driver: dump-tokens / dump-ast / dump-tree / classify / complete / check
  abi/
    dm_core.h      hand-written C header, source of truth for the ABI
    dm_core.hpp    optional C++ RAII wrapper for the Qt client
    schema/        JSON schemas for bulk query requests/responses
  editors/
    vscode/        extension + TextMate grammar                          [M10]
  tools/
    builtins-gen/  builds builtins.json from stddef.dm + reference HTML
  tests/
    Dm.Core.Tests/    unit + snapshot tests
    Dm.Native.Tests/  handle table, marshalling
    corpus/           real .dme projects used as snapshot fixtures
    abi-smoke/        CMake C++ program that links dm_core
  docs/
    abi.md  api.md  lsp.md  capability-matrix.md  dm-language-notes.md
    internal/      working notes, gitignored
```

---

## 6. Milestones

Restructured 2026-08-02. Syntax highlighting moved from M9/M10 to M2: it needs only the lexer, and
it is the first thing a user sees. Document symbols moved to the parser milestone for the same
reason — a per-file outline needs the AST, not the object tree.

### M0 — Boundary and project setup ✅ *(CI outstanding)*

The ABI is the riskiest infrastructure. Proven before any compiler code.

- ✅ `Dm.Core` + `Dm.Native`, publishing `dm_core.dll` (1.02 MB, 6 exports) via NativeAOT.
- ✅ `tests/abi-smoke` — CMake C++ program, 14 checks. Reference integration for the Qt client.
- ✅ `Dm.Core.Tests` + `Dm.Native.Tests`, 38 tests at M0 and 350 today. Handle validation, UTF-8
  marshalling, snapshot helper.
- ✅ Local git repo, MIT license, `.gitattributes`.
- ⬜ CI matrix. NativeAOT produces per-RID binaries: `win-x64`, `linux-x64`, `linux-arm64`,
  `osx-x64`, `osx-arm64`. **Note the vswhere quirk** — the publish fails with a misleading
  MSB3073 linker error unless `vswhere.exe`'s directory is on PATH.

### M1 — Text layer and lexer ✅

- ✅ `SourceText`, content preserved exactly rather than normalised. Both UTF-8 and UTF-16 offsets
  exposed; all three terminator forms recognised per §4b. An offset inside a terminator clamps to
  the end of that line's content.
- ✅ Lexer emitting `Newline`/`Indent`/`Dedent`. Indentation is compared by **prefix**, not by
  counting columns, so no tab width is assumed — this sidesteps open question 7 for now. Blank and
  comment-only lines never change the level.
- ✅ Multiline `{" ... "}` strings, interpolation as a flat token run, `\` line continuation,
  nesting `/* */` comments, resource literals, `#` stringification, the full operator table.
- ✅ Never throws. Unrecognised input becomes an `Unknown` token plus a diagnostic, so a buffer
  mid-keystroke still lexes end to end.
- ✅ `Dm.Cli` (`dmc`) with `dump-tokens` and `scan`.

**Validation approach.** `dmc scan` reports `Unknown` tokens and diagnostics across a whole
codebase, which is how the lexer gets checked against reality — the DM Reference does not enumerate
every operator or edge case, so real code is the only reliable source.

Current status: **279 files, 303,015 tokens, 0 unknown, 0 diagnostics** across `stddef.dm`,
`dm-bench`, `madridspy`, `mlaas`, `warklan`, and the BYOND library path.

Five bugs were found this way, none of which any amount of synthetic testing would have produced:
preprocessor directives corrupting the indent stack, `@"..."` raw strings, string continuations
breaking on CRLF, `//` inside a block comment, and free-text `#warn` bodies.

**`scan` globs `*.dm` off disk; it does not follow the include graph.** Library code reached through
`#include <vendor/name>` is invisible to it — `saving.dm` pulls in `<deadron/characterhandling>`,
which went unscanned until the library path was passed explicitly. That gap closes at M3, when the
real graph can be walked.

Deferred to the parser, not the lexer:
- Folding `/` and `.` in path context (§4a) — the lexer cannot tell paths from member access.
- `operator` followed by an operator token as a single proc name. The lexer emits `operator` as an
  `Identifier` followed by the operator tokens; the parser reassembles. This is why `operator:=`
  needs no special lexer handling.

Still open: whether a brace block can contain indentation-structured sub-blocks (question 7).

### M2 — Lexical classification ✅ → first visible feature

Syntax highlighting for both custom IDEs.

- ✅ `ClassificationService` with `Classify` and `ClassifyLines`. Touching runs of the same kind are
  coalesced, so a three-token string is one span.
- ✅ `ClassificationKind` with explicit numeric values, including members reserved for M6 so client
  colour tables never need renumbering.
- ✅ `Document` (immutable, caches the lex) and the `Workspace` document store. A pushed buffer is
  authoritative; disk is never consulted for that path.
- ✅ ABI: `dm_set_buffer`, `dm_close_buffer`, `dm_classify_range`, `dm_classification_count`,
  `dm_classification_data`, `dm_classification_free`. ABI minor bumped to 2.
- ✅ `SourceText.GetUtf8Offset`, with per-line byte offsets computed once.

**Whole-file lex, then filter.** Classification never lexes only the visible range: a `{" ... "}`
string or a nested `/* */` can begin thousands of lines earlier and decides whether the range is
code or text. The lex is cached on the `Document`, so the cost is paid per edit, not per scroll.

**Packed output.** Spans cross the ABI as one contiguous block of `int32` triples rather than
through per-span accessors, because this runs on every scroll and keystroke.

**Encoding is explicit.** Qt's `QString` and .NET's `string` are both UTF-16; a client holding raw
bytes wants UTF-8. They agree for ASCII, so a mismatch survives testing and then misplaces every
span the first time someone types a non-ASCII character. The smoke test asserts the two diverge by
exactly the extra byte count.

**Known limits, refined at M6 and M11:** cannot distinguish a user type from a builtin, resolve
identifiers introduced by macros, or tell a proc name from a var name. That is what most editors
ship, and it looks correct.

### M3 — Preprocessor and include graph ✅

- ✅ `SourceFileReader` — encoding detection. BOM, then strict UTF-8, then Windows-1252 with the
  0x80–0x9F punctuation range mapped from a table. No encoding-provider package, so `Dm.Core` stays
  dependency-free and AOT-clean.
- ✅ `IncludeGraph` — walks a `.dme` in compile order, with all four include forms resolved and
  verified against `dm.exe`. Directives are extracted from the token stream, so one inside a comment
  is correctly not a directive.
- ✅ `dmc includes`, with `--tree` and `--orphans`.
- ✅ `DirectiveScanner` — all twelve directive kinds, payloads as token ranges, driven off tokens so
  a directive inside a comment is not one.
- ✅ `MacroDefinition` / `MacroTable` — object-like, function-like and variadic parsing, plus an
  order-sensitive state hash for M9.
- ✅ `ConditionalEvaluator` — `#if` / `#elif` over DM's actual grammar (§8).
- ✅ `ConditionalStack` and conditional-aware graph walking. `#pragma multiple`, cycle termination,
  unterminated-conditional and stray-`#endif` diagnostics, and correct `__MAIN__` scoping.
- ✅ `MacroExpander` — object-like, function-like and variadic substitution, `#`, `##`, `###`, and
  the source map. Every token carries its origin and, if expanded, the chain out to the invocation
  the author wrote.
- ✅ `Preprocessor.Run` — a `.dme` in, the whole project's code tokens out in compile order.
  `dmc preprocess` drives it.

**Expansion is interleaved with the directive walk**, not deferred per file. Each run of code is
expanded against the macro state that applied *to it*, so code above a redefinition sees the earlier
value. Deferring would use the file's final state throughout.

**Verified on real projects:** mlaas 102 files / 120,262 tokens / 555 from 114 distinct macros,
madridspy 96 files, warklan 38 files, all with no errors.

**The graph builder is a preprocessor pass**, not a separate phase. Includes cannot be collected
without evaluating conditionals, so macro state is threaded through the traversal in include
order — the same ordering that decides override resolution and the §4a path ambiguity.

**From the DM Reference — what the preprocessor must handle:**

- **`#pragma multiple`** opts a file *out* of include-once. `IncludeGraph` currently dedupes
  unconditionally and must honour this.
- **Library search order is system lib dir first, then the per-user lib dir.** We only check the
  user dir.
- **`###` is a repeat operator**, distinct from `##`: `#define SAYTWICE(t) 2###t` repeats the
  replacement N times. A greedy `##` match mis-tokenises it.
- **`#` and `##` are documented only as *parameter prefixes*** (`#v`, `##k`), not as C-style infix
  operators — yet `CAT(a,b) a##b` demonstrably works. The reference does not explain the
  glue-onto-preceding-token behaviour we observed.
- **Macro substitution reaches inside string literals via `[...]`.** `"This is BIG."` is untouched;
  `"This is [BIG]."` *is* expanded. A preprocessor that simply skips strings is wrong.
- **`#if` accepts `fexists()`** as well as `defined()`, so static conditional evaluation cannot
  always be exact.
- **`__TYPE__`, `__PROC__`, `__IMPLIED_TYPE__` are pseudo-macros** the preprocessor does not handle
  — they resolve at the parser layer with type/proc context. Only `DM_VERSION`, `DM_BUILD`,
  `__FILE__`, `__LINE__`, `__MAIN__` are real preprocessor macros.
- **`#pragma compatibility N` mutates `DM_VERSION`**, so it is not constant across a file.
- **`#pragma warn|ignore|error <names,...>`** takes comma-separated warning names, has
  `push`/`pop` state, and does **not** propagate into included libraries.
- **`#pragma syntax C for|switch`** changes the grammar mid-file — see M4.
- **`<stddef.dm>` is implicitly included before all source**, which confirms the M5 builtins plan.
- **`FILE_DIR` is cumulative**, behaving like a list append rather than a normal macro, and applies
  only to resource literal lookup.

**Include resolution — from a real 107-file project (`mlaas/spies.dme`):**

| Form | Resolves to | Notes |
|---|---|---|
| `#include "src\file.dm"` | relative to the `.dme` directory | Windows `\` is the norm in real `.dme` files. **Both `\` and `/` work** — verified. Must be normalised, or nothing loads on Linux. |
| `#include <vendor/name>` | `<BYOND user lib>/vendor/name/name.dm` | Angle brackets mean a BYOND library, not the project. On this machine the root is `~/Documents/BYOND/lib`, holding `dantom`, `deadron`, `ter13`. Confirm the exact filename rule when implementing. |
| `#include "…​.dmf"` | interface file | Present in the graph. Recognise and skip. |
| `#include "…​.dmm"` | map file | Same. Deferred to the `.dmm` work. |

- **A duplicate `#include` of the same file is silently ignored** — verified against `dm.exe`; the
  same file twice compiles clean with no duplicate-definition error. Dedupe by *resolved* path, not
  by the literal string, since the same file can be written two ways. Real `.dme` files hit this:
  `spies.dme` includes `src\_constants.dm` at both line 7 and line 9, because DreamMaker's
  auto-generated `// BEGIN_INCLUDE` block re-added a manual entry.
- **`.dm` files may contain their own `#include`.** The graph is a tree, not a flat list from the
  `.dme`.
- `#define FILE_DIR .` appears in the `.dme` preamble and governs resource lookup.
- DreamMaker rewrites the region between `// BEGIN_INCLUDE` and `// END_INCLUDE`. Anything we ever
  write back into a `.dme` must respect those markers.
- Directives: `#define` (object-like and function-like), `#undef`, `#if` / `#ifdef` / `#ifndef` /
  `#elif` / `#else` / `#endif`, `#include`, `#warn`, `#error`, `defined()`.
- Seed BYOND's predefined macros (`DM_VERSION`, `DM_BUILD`) and `__FILE__` / `__LINE__`.
- **Stringification (`#arg`) exists** and must be implemented. Confirmed by `stddef.dm`:
  `#define ASSERT(c) if(!(c)) {CRASH("[__FILE__]:[__LINE__]:Assertion Failed: [#c]"); }`.
  It appears *inside* a string interpolation, so the two features interact. Token-pasting (`##`)
  and the repeat operator (`###`) are implemented and covered by `MacroExpander`.
- **Source mapping is required.** Every expanded token carries its origin file, original span, and
  macro expansion chain. Without it, classification, completion, diagnostics, and go-to-definition
  all land on the wrong line in macro-heavy code, which is most real DM.
- Snapshot the preprocessor's exit state (define-table hash) at each file boundary. M9 depends on it.

### M4 — Parser, syntax diagnostics, document symbols *(declarations done)*

- ✅ `DeclarationParser` — types, vars and proc signatures, with line-oriented recovery. Handles the
  DM-specific shapes: `var`/`proc` as ordinary path segments, bare `var`/`proc`/`var/const` block
  headers, comma- and semicolon-separated names, bracket declarations `var/L[]`, and reassembling
  overloaded operator names from the tokens the lexer emits.
- ✅ `dmc outline`, per file or across a tree.
- ⬜ Expression parser over §4c's precedence table.
- ⬜ Statement parser, with `#pragma syntax` mode tracking.
- ⬜ Parse the preprocessed stream rather than raw per-file tokens.

Proc **bodies are skipped**, not parsed. Everything an outline and the object tree need lives in
declarations, so statement parsing slots in later without disturbing this.


- Declarations: type-path declarations, `var/` blocks with modifiers (`const`, `tmp`, `global`,
  `static`), `proc/` and `verb/` blocks, overrides, `set` statements, `parent_type`.
- Statements: `if`/`else`, all three `for` forms, `while`, `do while`, `switch` with `if(a to b)`
  range cases, `spawn`, `return`, `break`, `continue`, `del`, `try`/`catch`/`throw`.
  - C-style `for` clause separators, verified by compiling every combination:

    | Header | Default | `#pragma syntax C for` |
    |---|---|---|
    | `for(i=0, i<3, i++)` — comma clauses | accepted | **rejected**, "malformed for statement" |
    | `for(i=0; i<3; i++)` — semicolon clauses | accepted | accepted |
    | `for(i=0,j=0; i<3; i++,j+=1)` — comma *chaining* | **rejected**, "too many args" | accepted |

    Semicolons work in **both** modes, so the pragma is not what enables them. What it actually does
    is **swap what the comma means**: it removes comma-as-clause-separator and adds
    comma-as-statement-chainer. It is subtractive as well as additive, so turning it on breaks any
    existing comma-separated `for` in that file. Runtime-confirmed that chained clauses both
    execute: `for(i=0, j=100; i<3; i++, j+=10)` ends at `i=3 j=130`.
  - **`#pragma syntax C switch`** likewise swaps `if`/`else` for `case v:` / `default:`, and
    introduces C fall-through. Runtime-confirmed: a `case` without `break` falls into the next.
    Without the pragma, `case 1:` fails with "expected var or proc name after : operator", so it is
    a genuinely different grammar rather than an alias.
  - So the parser needs **three modes**, file-position-dependent, interacting with
    `#pragma push`/`pop`. Pragmas do not propagate into included libraries.
  - The other two forms are `for(var/x in list)` and `for(var/i = 1 to 10 step 2)`.
  - `for(var/client/P)` with **no `in` clause** is legal; the list defaults to the whole world.
  - 516 adds `for(var/key, value in assoc_list)`.
- `break Label` and `continue Label` take an optional loop label; labels are declared as
  `identifier:` on their own line. `del Object` and `throw Value` take a bare operand, no parens.
- **Modified-type initialisers**: `path {var = val; var2 = val2}` is legal anywhere a type value is.
  Braces are mandatory here even though they are optional elsewhere.
- **Bracket var declarations**: `var/L[]`, `var/M[10]`, `var/grid[10][5]`. Also the brace group form
  `var {cur; tot}`.
- `::` has four forms — `::A`, `::A()`, `A::B`, `A::B()` — and **`A::B()` is a proc *reference*, not
  a call**. A parser that treats it as a call expression is wrong.
- Expressions: full precedence table, `new /path(args)`, `locate()`, `input(...) as ...`,
  `list(a, b, c = d)` with associative entries, indexing, ternary, `..()` parent call, the bare `.`
  return-value variable, `src` / `usr` / `world` / `global.`. `.` and `:` member access are distinct
  AST nodes.
- Declaration forms confirmed from `stddef.dm` (§8), all needing coverage:
  - Comma-separated var lists: `var/a=1,b=0,c=0` and `var/x, y, size, offset`
  - Semicolon statement separators on one line: `x = 0; y = 0; z = 0`
  - Single-line proc bodies: `Multiply(m) return matrix(src, m, ...)`
  - Default parameter values: `MapColors(a, b, c, j=0, k=0, l=0)`
  - Bare inherited-var assignment with no `var/`: `_dm_interface = _DM_datum|_DM_sound`
  - Nested indentation building paths: `database` → `query` declares `/database/query`
  - Empty-bodied overrides
- Operator overloading: `operator+`, `operator-`, `operator*`, `operator/`, `operator+=`,
  `operator:=`, `operator_turn`, `operator<<`.
- **Error recovery is a hard requirement.** Editor buffers break on every keystroke. Use
  indentation-anchored resync: on error, discard to the next line at or below the enclosing block's
  indent level.
- **Syntax diagnostics** fall out of recovery; surface them through `DiagnosticService`.
- **Document symbols** — a per-file outline needs the AST only, not the object tree. Ship here.

### M5 — Object tree and builtins

- `TypePath` as an interned, comparable value type. The hottest key in the system.
- One `TypeSymbol` per path node, merging declarations across files in include order. Each type
  records: parent link (implicit by path or explicit via `parent_type`), declared vars, procs with
  override chains, and all declaration sites — a type is legitimately declared in N files.
- **Builtins.** `mob` has `Move()`, `Login()`, `loc`, `client`, `verbs`; none appear in user code.
  `Resources/builtins.json` is assembled from **two sources**, because neither is complete:

  | Source | Provides | Method |
  |---|---|---|
  | `stddef.dm` (generated by Dream Maker, see §8) | All `#define` constants and `var/const` globals; the wrapper datums `sound`, `icon`, `matrix`, `database`, `database/query`, `exception`, `regex`, `dm_filter`, `generator`, `particles`; the macros `ASSERT`, `EXCEPTION`, `REGEX_QUOTE` | Parse with our own parser — it is valid DM |
  | `help/ref/info.html` (DM Reference) | Everything compiled into `byondcore.dll`: `/datum`, `/atom`, `/atom/movable`, `/mob`, `/obj`, `/turf`, `/area`, `/client`, `/world`, `/list`, `/savefile`, `/image`, `/mutable_appearance`, and the global procs (`istype`, `locate`, `view`, `text2num`, …) | Scrape with `tools/builtins-gen` |

  Parsing `stddef.dm` with our own parser doubles as a self-test: it is real BYOND-authored DM that
  exercises brace blocks, operator overloads, comma var lists, and stringification.

  `stddef.dm` is version-stamped (the sample on hand reads `516.1666`) and is regenerated by
  creating a file named `stddef.dm` in a project and compiling. `builtins.json` records the BYOND
  version it was built from.

  Do not vendor `stddef.dm` into the repo — it is BYOND-generated output and this repo is public.
  `tools/builtins-gen` locates or regenerates it from the local install.

### M6 — Binder, semantic model, completion

- Scope chain: locals → proc parameters → `src` type members (walking the inheritance chain) → globals.
- `var/mob/test/t` → split path into type `/mob/test` and name `t`. Modifier keywords sit inside the
  path (`var/const/X`, `var/list/L`, `var/obj/item/I as obj`), and per §4a the separator may be `/`,
  `.`, or a space. Path splitting gets its own test file.
- With the M4 AST available, also infer through `new /path`, `as` casts, and assignment from a typed
  source. Return-type inference stays out of v1.
- Leading-`.` relative path resolution per §4a: upward search from the current type to root, first
  hit wins.
- `CompletionService.CompleteAt(file, line, col)` classifying context:
  - after `/` or `.` mid-path → type paths
  - after `.` on a value → members filtered by receiver type
  - after `:` → members of the declared type **and all its subtypes**. Not "everything": see §4a,
    `:` widens the check to the subtype tree rather than disabling it.
  - bare identifier → locals + params + `src` members + globals + macros
- **Semantic classification refinement** — with the object tree available, upgrade M2's lexical
  spans to distinguish user types from builtins, procs from vars, and macro-introduced identifiers.

### M7 — Workspace symbols, navigation, bulk queries → team v1

- Workspace symbol search, go-to-definition on type paths and proc names, hover rendering the
  declaration plus preceding `///` doc comment.
- Bulk `dm_query_json` operations: full object tree, subtypes of a path, all symbols in a file.
  These back the IDEs' tree-browser panels.
- Freeze JSON schemas in `abi/schema/`, versioned separately from the binary.

### M8 — `.dmi` icon states *(independent; schedule anytime)*

- Parse the PNG `zTXt` chunk. BYOND stores a plaintext metadata block enumerating every
  `state = "..."` with dirs and frame counts.
- No dependency on the compiler pipeline. Roughly an afternoon, and it parallelizes cleanly to
  another team member during M4.

### M9 — Incrementality and performance

- Reparse only the edited file. Preprocessor state flows sequentially through include order, so
  editing a file containing `#define`s invalidates everything downstream — mitigate via the M3
  boundary snapshots: re-run downstream files only if the exit-state hash changed.
- Cache the object tree per-file, rebuilding affected subtrees only.
- Target: warm completion under 30ms on the team's game. Complete before M10 — public users will
  arrive with larger codebases.

### M10 — LSP shell → community v1

- `Dm.Lsp` as a .NET console app referencing `Dm.Core`. Stdio primary, TCP as a small option.
- Spec 3.17. Honor `positionEncoding` negotiation. Implement `$/progress` and `$/cancelRequest`.
- Standard methods over existing services: completion, hover, definition, document/workspace
  symbols, publishDiagnostics, semanticTokens (backed by the M2/M6 classification service).
- Custom methods for what LSP cannot express: `dm/objectTree`, `dm/subtypesOf`, `dm/iconStates`,
  mirroring the bulk query schemas so both shells stay aligned.
- TextMate grammar + VS Code extension in `editors/vscode/`.

### M11 — Semantic analysis

Semantic diagnostics, find-references, rename. All unblocked by the M4 AST.

Diagnostics of note, drawn from §4a: `proc/` declare-vs-override duplicate definitions; constructs
whose meaning depends on include order; leading-`.` relative paths that a nearer type could
silently re-target.

Find-references and rename cannot be fully sound in DM because of `:` and string-based dispatch
(`call()`, `text2path()`). Decide whether rename is safe-subset-only or best-effort-with-warning.

### Deferred

`.dmm` map support, formatter, debug adapter. DAP requires auxtools-style injection into Dream
Daemon and is effectively a separate project.

---

## 7. ABI contract

`abi/dm_core.h` is the source of truth. Implemented so far: version, workspace open/close/root,
last error, free.

**Hot path — handles and accessors:**
```c
int32_t     dm_abi_version(void);
dm_status   dm_workspace_open(const char* dme_path, dm_workspace** out);
void        dm_workspace_close(dm_workspace*);
dm_status   dm_workspace_root(dm_workspace*, char** out_root);
dm_status   dm_set_buffer(dm_workspace*, const char* file, const char* utf8, int32_t len);
dm_status   dm_classify_range(dm_workspace*, const char* file, int32_t start_line,
                              int32_t end_line, dm_span_list** out);          /* M2 */
dm_status   dm_complete_at(dm_workspace*, const char* file, int32_t line, int32_t col,
                           dm_completion_list** out);                          /* M6 */
```

**Bulk path — serialized:**
```c
dm_status   dm_query_json(dm_workspace*, const char* request_utf8, char** out_response);
void        dm_free(void* ptr);
char*       dm_last_error(void);
```

### NativeAOT rules, enforced from M0

- **No exception crosses the boundary.** Every export catches and returns a `dm_status`; the message
  is retrievable via `dm_last_error`.
- **Handles are validated.** `(generation << 32) | (index + 1)` through a slot table with a free
  list. Index 0 is never issued, so null is always invalid. Generation increments on release, so a
  use-after-close returns `DM_ERR_INVALID_HANDLE` rather than resolving to a recycled object.
  Malformed handles are rejected in `Unpack` before any indexing.
- **Never return a pointer into managed memory.** Strings are copied to `NativeMemory.Alloc`'d UTF-8
  and freed by the caller via `dm_free`. Ownership is documented per function in the header.
- **`Dm.Core` stays AOT-clean:** no reflection, no `dynamic`, no runtime codegen, no reflection-based
  serialization. Enforced by `IsAotCompatible`. `Dm.Lsp` is exempt; `Dm.Core` is not, because both
  share it.
- `dm_abi_version()` is checked by every client at startup. Additive changes bump minor; breaking
  changes bump major and cost downstream consumers real work.

---

## 8. Environment findings

Recorded 2026-08-02 on the primary dev machine.

- .NET SDK 9.0.308, git 2.47.0, VS 2022 Community with MSVC 14.44.35207 and Windows SDK 10.0.26100.
  NativeAOT verified working.
- **NativeAOT publish requires `vswhere.exe` on PATH.** Without it the ILCompiler targets splice a
  `'vswhere.exe' is not recognized` error string into the link command and fail with MSB3073 exit
  123 — while reporting a `link.exe` failure, even though `link.exe` was found. Prepend
  `C:\Program Files (x86)\Microsoft Visual Studio\Installer`.
- CMake is not on PATH; VS bundles a copy under `Common7\IDE\CommonExtensions\Microsoft\CMake`.
- BYOND installed at `C:\Program Files (x86)\BYOND`. No `stddef.dm` ships in the install directory.
- **`stddef.dm` is generated on demand.** Creating a file named `stddef.dm` in a project and
  compiling causes Dream Maker to emit the code it auto-compiles at the start of every project.
  A generated copy is at `C:\Users\Anonymous\Desktop\world\stddef.dm` (529 lines, version 516.1666).
- **`stddef.dm` covers only part of the standard library** — constants and wrapper datums, not
  `/datum`, `/atom`, `/mob`, `/obj`, `/turf`, `/area`, `/client`, `/world`, `/list`, or the global
  procs. Those are compiled into `byondcore.dll`.
- `help/ref/info.html` (1.3 MB) is the DM Reference in structured HTML. It supplies the other half.
- `byondapi/` ships `byondapi.h` and `byondapi.lib` — relevant only to a future debug adapter.

### Language facts verified against `dm.exe` 516.1666

Established by compiling discriminating cases, not by reading documentation. Each test was built so
that the two candidate behaviours produce different compiler output.

| Behaviour | Evidence |
|---|---|
| **Block comments nest.** | `/*` `/*` `*/` + garbage → *"end of file reached inside of comment"*. Non-nesting would have made the garbage live and produced syntax errors instead. |
| **A `//` inside a block comment hides both `/*` and `*/` to end of line.** | `/*` then `// */` then garbage then `*/` → compiles clean. The delimiter on the `//` line was ignored. |
| **Quotes do not protect `*/` inside a block comment.** | `/*` then `"*/"` → *"unterminated text (expecting \")"*. The `*/` closed the comment and left a stray quote. |
| **A duplicate `#include` of the same file is ignored.** | The same file included twice compiles clean. Without a guard it would be a duplicate definition. |
| **`#include` accepts forward slashes as well as backslashes.** | `#include "sub/b.dm"` loads; the compiler then reports the file as `sub\b.dm`. |
| **`/mob` has built-in `x`, `y`, `z`.** | Declaring `var/x` on a `/mob` subtype → *"x: duplicate definition (conflicts with built-in variable)"*. Found by accident, and a reminder of why `builtins.json` (M5) is load-bearing. |
| **`#warn` and `#error` bodies are free text, not tokens.** | `#warn this won't work and "unbalanced` compiles with 0 errors and prints verbatim. Apostrophes and unbalanced quotes are legal there. |
| **`in` binds looser than assignment.** | `var/r = (has = 2 in L)` leaves `has == 2` and the whole expression `== 1`. It parses as `(has = 2) in L`, not `has = (2 in L)`. |
| **`..()` with empty parens forwards the current arguments.** | A child override calling `..()` with no args reached the parent with the original `'hello'` intact. It does not pass zero arguments. |
| **`%` truncates operands to integers; `%%` is fractional.** | `7.5 % 2` is `1`; `7.5 %% 2` is `1.5`. |
| **DM has pointers (515+).** | `var/p = &x` then `*p = 99` mutates `x`. `*p` is a valid assignment target. |
| **Modified-type initialisers work in `new`.** | `new /obj/thing{hp = 42; tag_name = "set"}` constructs with both vars set. Braces mandatory, `;` separates same-line entries. |
| **A bare `for` iterates world *contents*, not all instances.** | Of three objects created, the two with a map location were found by `for(var/obj/marker/M)`; the one with `loc = null` was not. Identical result to `for(... in world)`. |
| **Function-like macros need the `(` to touch the name.** | `#define A (x)` is object-like and expands to `(x)`; calling `A(1)` fails. `#define B(v)` is function-like and a bare `B` fails with "undefined var". Same rule as C. |
| **`#if` rejects undefined identifiers rather than treating them as 0.** | A bare undefined name reports `unexpected token`. This is the opposite of C, and it is why real DM guards with `#ifdef` rather than `#if NAME`. |
| **`#if` supports a narrow grammar.** | Accepted: numbers (floats, unary minus), defined macro names, `defined(X)`, `!`, `+ - * /`, comparisons, `&&`, `\|\|`, parens. Rejected: `%`, `<<`, `>>`, `&`, `\|`, string literals. |
| **`defined` requires parentheses.** | `defined FIVE` fails with "expected (". |
| **Names may contain `\` escapes.** | `\~Admin_Chat(T as text)` compiles, as do `D\~E` mid-name and `var/\~G`. `\a`, `\the` and `\1` prefixes are all accepted, so the rule is "backslash plus any one character", not a table of known macros. These control how a verb or var is presented to players. A bare `\~` in *expression* position is rejected — that distinction is the parser's to make. |
| **A `\` at the end of a `//` comment continues it onto the next line.** | A comment ending in `\` followed by a line of garbage compiles clean. Used in real code to wrap long explanations. |
| **Preprocessor directives carry no indentation of their own.** | Inside a one-tab proc body, `#ifdef` at column 0, at one tab, and at three tabs all compile clean. A directive between a header and its body therefore emits no `Indent`, and the parser must look past it for the one the next code line emits. |
| **A bare `;` at file scope is legal.** | A lone `;` at column 0 between two proc declarations compiles with 0 errors, with a `#warn` after it printing. |
| **Indentation depth is not a prefix comparison.** | Against a sibling at one tab, dm.exe accepts `" \t"`, `"\t "` and `" "` as the same level, but rejects `"    "` with its own *"inconsistent indentation"*. Modelled as: tab count decides depth, spaces count only when there are no tabs. |

The second one matters more than it looks. A line such as `//*see the article` inside a block
comment would otherwise nest and swallow the remainder of the file. Found in real code.

### Language facts confirmed from real codebases

- **`@"..."` raw strings exist**, with neither escape processing nor interpolation. Found as
  `new(@"[^\x01-\xFFŐ-š…]", "g")` — the `\x` sequences and the leading `[` both prove it,
  since either would otherwise be consumed. `@{"..."}` is supported defensively.
- **A backslash before a line break continues a string literal.** Used constantly for long
  description text. The terminator must be consumed whole; skipping a fixed two characters eats the
  CR of a CRLF pair and leaves the LF, which then reads as an unterminated string. Invisible on
  LF-only files.
- **Preprocessor directives are layout-neutral.** `#ifdef` and `#endif` are written at column 0
  regardless of the surrounding indentation and neither open nor close a block. Treating one as a
  normal line dedents to the root and loses every intermediate level.

### Language facts confirmed from `stddef.dm`

- `#` stringification exists in function-like macros, and appears inside string interpolation.
- `__FILE__` and `__LINE__` exist.
- Brace blocks `{ ... }` coexist with significant indentation.
- Operators are declarable as procs (`operator+`, `operator:=`, `operator_turn`, …).
- `;` separates statements on a single line.
- One `var/` can declare a comma-separated list of names, with or without initializers.
- A proc body can sit on the same line as its signature.
- Parameters take default values.
- Inherited vars are overridden by bare assignment at type level, with no `var/` keyword.
- Path literals appear as ordinary expression arguments: `istype(file, /list)`.
- `new/generator(...)` — `new` immediately followed by a path with no space.

### Grades of evidence

Two sources, and they are not equally reliable. Keep them distinguishable.

- **Compiler-verified** — established by compiling a case built so the two candidate behaviours
  produce different output. Highest confidence. Everything in the table above.
- **Reference-documented** — stated in `help/ref/info.html`. Reliable for *inventories* (the
  operator list, precedence, `as` types, `set` names) which cannot be discovered by testing, but
  **incomplete on behaviour**. Demonstrated gaps: `\~` in names appears nowhere in the reference;
  `//` inside a block comment is undocumented; a backslash continuing a `//` comment is undocumented
  and mildly contradicted; the "inconsistent indentation" error is never mentioned; hex and
  scientific number literals are never documented as syntax.

Where the two disagree, the compiler wins. The reference also contains outright errors — `-=` is
listed twice in the precedence table, and `A -= B` is mapped to `operator--(B)` instead of
`operator-=`.

### Path semantics source

`C:\Users\Anonymous\Desktop\world\dot-and-slash.md` — a work-in-progress writeup by the project
author, testing `/` and `.` behaviour against BYOND 516.1666 by compiling cases rather than
reasoning about them. Every claim in §4a comes from it.

The same directory holds the DM files those tests were run from (`proof.dm`, `check.dm`,
`routing.dm`, `two.dm`, `world.dm`, `anothertest.dm`). They are compiler-verified cases covering
exactly the constructs §4a describes, which makes them the natural first fixtures for
`tests/corpus`. Not copied in; ask first.

---

## 9. Verification

- **Unit and snapshot tests** — `dotnet test`. Separate fixture sets for lexer, classification,
  preprocessor (including macro-expansion source maps), parser error recovery, path splitting,
  object tree merging. Snapshot helper is `tests/Dm.Core.Tests/Snapshot.cs`;
  `DM_UPDATE_SNAPSHOTS=1` rewrites expected files.
- **Cross-codebase corpus.** `tests/corpus` must not contain only the team's game. Add open BYOND
  codebases including an SS13 fork such as /tg/station; it is the harshest available preprocessor
  stress test and free to obtain.
- **CLI driver** — `Dm.Cli dump-tokens|classify|dump-ast|dump-tree|complete|check game.dme`.
  Fastest debug loop, and the arbiter when an IDE reports a bug: if the CLI reproduces it, the bug
  is in the core.
- **ABI smoke test** — `tests/abi-smoke` builds via CMake, links `dm_core`, exercises the boundary.
  Runs in CI across the RID matrix.
- **LSP conformance (M10)** — drive `Dm.Lsp` from the VS Code extension and at least one
  non-VS-Code client to catch spec assumptions VS Code tolerates.
- **Acceptance** — point the CLI at the team's `.dme`: `mob.` lists custom and builtin members;
  `var/mob/test/t` then `t.` resolves; object tree dump matches DreamMaker; `.dmi` states enumerate.
- **Performance** — warm completion latency and cold full-parse time on both the team game and the
  large corpus project, tracked as a regression metric.

---

## 10. Open questions

| # | Question | Blocks | Status |
|---|---|---|---|
| 1 | License | — | **Resolved** — MIT |
| 2 | Preprocessor stringification | M3 | **Resolved** — `#arg` exists; `##` and `###` implemented in `MacroExpander`. |
| 3 | Where builtins come from | M5 | **Resolved** — `stddef.dm` + `info.html`. |
| 4 | MSVC tooling for NativeAOT | M0 | **Resolved** — present and verified. |
| 5 | Third IDE's language | Nothing; may justify a prebuilt binding package | Open |
| 6 | AOT `Dm.Lsp` for startup latency, or keep a reflection-based LSP library | M10 | Deferred |
| 7 | Can a brace block contain indented sub-blocks? | M4 | Open — needs a compiler experiment |
| 9 | DM's exact "inconsistent indentation" rule | M4 | **Partly resolved** — see §8. Our model matches every case dm.exe accepts; the one divergence is `"    "` against a tab, which DM rejects and we silently nest. Under-reporting is deliberate. |
| 10 | Source encoding: some old files are Windows-1252, not UTF-8 | M3 | **Resolved** — `SourceFileReader` detects it. |
| 11 | What does `#include` do inside a false `#ifdef`? | M3 | **Resolved** — not followed. `IncludeGraph` walks includes only while `ConditionalStack.IsActive`. |
| 8 | Access to the team's game codebase for M3 onward | M3, M5, M6 | Open — see §9 |

---

## Changelog

- **2026-08-02** — Initial design. Core language, ABI style, parser depth, and shell strategy
  settled. Environment surveyed.
- **2026-08-02** — Read a generated `stddef.dm` (516.1666). Resolved open questions 2 and 3. Added
  brace blocks, operator overloading, comma var lists, semicolon separators, single-line proc
  bodies, default parameters, and bare inherited-var assignment to the parser scope.
- **2026-08-02** — M0 boundary proven: `dm_core.dll` publishes with 6 exports, C++ smoke test
  passes 14 checks, 38 unit tests. Repo initialised locally, MIT licensed.
- **2026-08-02** — Added §4a from the author's compiler-tested path-semantics writeup. Corrected
  the C-style `for` separator from semicolons to commas.
- **2026-08-02** — **Milestones restructured.** Syntax highlighting moved from M9/M10 to M2; it
  needs only the lexer and is the first thing visible in an editor. Document symbols moved into the
  parser milestone. Everything from the old M2 onward shifted by one. Added §4b on line endings and
  the pushed-buffer rule in §4.
- **2026-08-02** — M1 landed: `SourceText`, lexer, `dmc scan` / `dump-tokens`. Five lexer bugs came
  out of scanning real codebases rather than out of tests.
- **2026-08-02** — M2 landed: `ClassificationService`, `Document`/`Workspace`, and the six
  classification exports. ABI minor bumped to 2.
- **2026-08-03** — M3 landed: encoding detection, include graph, directive scanner, macro table,
  conditional evaluation, macro expansion with source maps, `Preprocessor.Run`. Resolved open
  questions 2 and 11.
- **2026-08-03** — M4 declarations landed: `DeclarationParser` and `dmc outline`; proc bodies are
  skipped. Added §4c with the reference's precedence table. Expressions, statements and
  `#pragma syntax` mode tracking remain.
