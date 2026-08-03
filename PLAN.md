# DM Analysis Library + Language Server — Design

> **Live document.** Updated as the project progresses. Milestone status, decisions, and
> open questions are kept current here. See `ROADMAP.txt` for the short version.
>
> Status: **M0–M5 complete · M6 completion ships through ABI 0.4; inference remains** · Last updated: 2026-08-03

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
equivalent (standard method or custom `dm/*`). Otherwise the direct-reference path outgrows the
other two shells. Tracked in `docs/capability-matrix.md` from M10, when there is a second shell for
the first one to drift away from.

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

**The leading segments mean different things depending on whether a `var` introduced them**, and
the two readings put the variable on different types:

```dm
mob
    var
        atom/movable/locker    // var `locker` of type /atom/movable, ON /mob
/obj/item/hp = 3               // var `hp`, ON /obj/item — a bare override
```

Compiler-verified: assigning `locker` inside a proc on `/mob` compiles, and declaring
`/mob/atom/movable/unrelated` alongside it is not a duplicate — so the var block declared a
variable, not a type. Reading the first form as a path invents `/mob/atom/movable`, which is
exactly what the object tree did until it was fixed.

### Context 3 — an expression

The only context where a leading `/` and a leading `.` differ.

- Leading `/` is absolute from root.
- Leading `.` is a **search, not a traversal**. The exact rule is below; it is not what the earlier
  wording here ("check the current type, then its parent, first hit wins") implied.
- **No leading separator means it is not a path at all** — `obj.item.sword` is ordinary member
  access and resolves as a var lookup.

### The leading-`.` search rule, exactly

Compiler-verified against 516.1666 in every particular, because three plausible readings of "search
upward" give different answers and two of them are wrong.

- **The anchor is the enclosing type's path in the code tree — not its inheritance chain.** A type
  with `parent_type = /a/inh` does *not* see `/a/inh`'s children through a leading `.`; the search
  climbs `/b/thing` → `/b` → `/` regardless of what `parent_type` says. Resolving this with
  `ObjectTree.InheritanceChain` would be wrong; it needs `TypePath.Parent`.
- **The walk includes root**, so a root-level type is reachable from arbitrarily deep.
- **A global proc anchors at root**, and therefore reaches only root's own children.
- **The whole remaining path must resolve, and the search backtracks until it does.** Given
  `/x/sword/deep` and `/x/magic/sword`, a `.sword/deep` written inside `/x/magic` resolves — even
  though the *nearer* `sword` has no `deep` under it. So it is not "first matching first segment
  wins"; the nearer candidate is abandoned and the walk continues.
- **Trailing segments are validated.** `.sword/nonexistent` is *"undefined type path"* when no
  reachable `sword` has that child, so the backtracking above is a real search rather than the
  compiler ignoring what follows.
- **Among ancestors where the whole path resolves, the nearest wins.** With both `/x/sword` and
  `/x/magic/sword` complete, `.sword` inside `/x/magic` binds to `/x/magic/sword`. Verified through
  `parent_type = .sword` and then reaching a var declared on only one of the two.

Put together: *walk the enclosing type's path ancestors nearest-first, including root, and take the
first one under which the entire relative path resolves.*

It works in type-level var initialisers, inside proc bodies, and as a `parent_type` value.

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
point the same line resolves somewhere else. Worth an M11 lint, though the backtracking above
narrows it: a nearer type only steals the reference if the *whole* relative path resolves under it.

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
  tree"). Confirmed, and its phrase "code tree" turns out to be exact rather than loose — the search
  follows the path ancestry and ignores `parent_type` entirely. Everything else in the rule above is
  our own: that it reaches root, that it validates the whole path and backtracks, and that the
  nearest complete match wins.
- **"Otherwise, a path is relative"** (the `/` entry) is about the **code tree**, not about
  expressions. In declaration position it is plainly true — `mob/player` written at top level
  declares `/mob/player`. Read as a statement about expressions it is false, and that misreading is
  the whole of Context 3's trap: a bare `obj.item` in an expression is member access, not a relative
  path. The reference never distinguishes the two positions, which is why §4a does.
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

**Client guidance** (in `INTEGRATION.txt`): detect the dominant line ending on load, normalize
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
- **A conditional's `:` must have whitespace before it.** `1 ? b:c` reads `b:c` as member access and
  then fails for want of a separator. Compiler-verified across all four spacings, see §8. The table
  cannot express this, since it is a lexical distinction rather than a precedence one.

`A #= B` is shorthand for `A = A # B` **except** for `~=` and `:=`. Note `~=` is an equivalence
*test* at level 10, not a compound assignment — easy to mis-bucket from the `=` suffix.

### Overloadable operators

Declared as a proc named `operator` immediately followed by the glyph: `operator+`, `operator[]`,
`operator[]=`, `operator""`, `operator:=`, `operator_turn`, `operator<=>`, `operator%%`. The lexer
must accept all of these as a single proc *name* in declaration position.

Not overloadable: `=` `!` `&&` `||` `&&=` `||=` `?` `==` `!=` `.` `:` `?[]`.

---

## 5. Repository layout

The target layout. A milestone in brackets marks something that does not exist yet.

```
byond-lang-server/
  PLAN.md          this document
  ROADMAP.txt      short-form status
  INTEGRATION.txt  the client-facing ABI guide
  src/
    Dm.Core/
      Text/        SourceText, SourceFileReader, LinePosition, TextSpan
      Syntax/      Lexer, DeclarationParser, ExpressionParser, StatementParser, TokenKind
      Preprocessing/  Preprocessor, MacroTable, MacroExpander, ConditionalEvaluator
      Includes/    IncludeGraph, IncludeDirective
      Symbols/     ObjectTree, TypeSymbol, ProcSymbol, VarSymbol, TypePath, Builtins
      Binding/     Binder, SemanticModel, Scope, TypeResolver                [M6/M7]
      Services/    ClassificationService, CompletionService, DocumentSymbolService,
                   HoverService [M7], DefinitionService [M7], DiagnosticService [M11]
      Resources/   builtins.txt   (BYOND stdlib type tree)
    Dm.Assets/     DmiReader (PNG zTXt -> icon states)                       [M8]
    Dm.Native/     Exports.cs, HandleTable.cs, marshal helpers -> dm_core.dll
    Dm.Lsp/        JSON-RPC server over Dm.Core                              [M10]
    Dm.Cli/        dev driver: scan / dump-tokens / classify / includes / preprocess /
                   outline / symbols / tree / complete
  abi/
    dm_core.h      hand-written C header, source of truth for the ABI
    dm_core.hpp    optional C++ RAII wrapper for the Qt client               [M7]
    schema/        JSON schemas for bulk query requests/responses            [M7]
  editors/
    vscode/        extension + TextMate grammar                              [M10]
  tools/
    builtins-gen/  builds builtins.txt from stddef.dm + reference HTML
  tests/
    Dm.Core.Tests/    unit + snapshot tests
    Dm.Native.Tests/  handle table, marshalling
    corpus/           real .dme projects used as snapshot fixtures           [M9]
    abi-smoke/        CMake C++ program that links dm_core
  docs/
    dm-language-notes.md   compiler-verified DM edge cases
    api.md                 the in-process C# surface                         [M7]
    lsp.md                 LSP methods and custom dm/* extensions            [M10]
    capability-matrix.md   in-process vs ABI vs LSP parity                   [M10]
    internal/              working notes, gitignored
```

There is no `docs/abi.md`: `INTEGRATION.txt` is that document, and `abi/dm_core.h` is the contract
it describes. The capability matrix is worth writing only once a second shell exists to fall out of
sync with, so it lands with `Dm.Lsp`.

---

## 6. Milestones

Restructured 2026-08-02. Syntax highlighting moved from M9/M10 to M2: it needs only the lexer, and
it is the first thing a user sees. Document symbols moved to the parser milestone for the same
reason — a per-file outline needs the AST, not the object tree.

### M0 — Boundary and project setup ✅ *(CI outstanding)*

The ABI is the riskiest infrastructure. Proven before any compiler code.

- ✅ `Dm.Core` + `Dm.Native`, publishing `dm_core.dll` (1.49 MB, 14 exports) via NativeAOT.
- ✅ `tests/abi-smoke` — CMake C++ program, current with ABI 0.4. Reference integration for the
  Qt client, and the only thing that proves the published binary links and runs from C++ rather
  than merely that the managed side behaves.
- ✅ `Dm.Core.Tests` + `Dm.Native.Tests`, 38 tests at M0 and 523 today. Handle validation, UTF-8
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

### M4 — Parser, syntax diagnostics, document symbols ✅

- ✅ `DeclarationParser` — types, vars and proc signatures, with line-oriented recovery. Handles the
  DM-specific shapes: `var`/`proc` as ordinary path segments, bare `var`/`proc`/`var/const` block
  headers, comma- and semicolon-separated names, bracket declarations `var/L[]`, and reassembling
  overloaded operator names from the tokens the lexer emits.
- ✅ `dmc outline`, per file or across a tree.
- ✅ `ExpressionParser` over §4c's precedence table. Precedence climbing, with `in` at the bottom,
  the unary/binary split for `*` and `&`, right-associative assignment, and the DM primaries: path
  literals, `new` with and without a type, modified-type initialisers, `..()`, the bare `.`, the
  four `::` forms, associative arguments, interpolation holes, and `as` clauses. Wired into var
  initialisers; `VarDeclarationSyntax.Initializer` now carries the tree.
- ✅ `StatementParser` covering every form in this milestone, wired into proc bodies:
  `ProcDeclarationSyntax.Body` now carries them. Both `switch` grammars, all four `for` shapes,
  `try`/`catch`, `spawn`, labels, `set`, brace blocks, inline bodies, and the local `var` forms
  including the modifier-headed block and its nested groups.
- ✅ `#pragma syntax` mode tracking via `SyntaxModes`, shared between the declaration and statement
  parsers because the pragma sits at file level while the grammar it changes is used inside bodies.
  The mode is read where a statement **starts**, so a later `#pragma pop` cannot retroactively
  change how it parsed.
- ⬜ Parameter defaults still come from the range scan in `ReadParameter`, not the expression parser.
- ✅ Parse the preprocessed stream rather than raw per-file tokens, and it is now the default.
  `TokenSource` lets the parsers read tokens whose text and position come from different files,
  `PreprocessedSplitter` gathers the project stream back into per-file runs, and `dmc tree --raw`
  keeps the old path for comparison. **Exact on mlaas: 1493/1493 vars and 1153/1153 procs against
  `dm.exe -o`, with nothing invented.** §9 has the numbers and the three bugs it took to get there.
- ⬜ Accept injected defines. `dm.exe -DNAME`, `-DNAME=value` and `-DFN(x)=...` all work, and bare
  `-DNAME` defines it empty rather than `1` (§8). A project whose build passes `-D` flags compiles a
  different program from the one we analyse without them, so `Workspace.Open` needs an optional
  define set seeded into the `MacroTable` before the include walk, with a `dmc --define` flag and an
  ABI parameter to match. Belongs with the preprocessed-stream work above, since both change what
  the parser is fed.
- ✅ `DocumentSymbolService`, value-shaped and taking an explicit position encoding, with a
  selection range covering the name alone so an outline can navigate and a rename knows what to
  replace. `dmc symbols` drives it, and it ships through `dm_document_symbols` at ABI 0.3 with the
  file's syntax diagnostics in the same document.
- ✅ Two modelling fixes the outline exposed. A bare `var`/`proc` block header is marked
  `IsGroupHeader` and contributes no symbol of its own — it says what kind its children are, and
  left in it produced an entry called `var`. A bare assignment at type level (`world/maxx = 3`,
  stddef.dm's `_dm_interface = ...`) is a var override rather than a type, which would otherwise
  have put `maxx` in the object tree as a subtype of `/world`. Total declaration counts across the
  corpus are unchanged; 1018 nodes in mlaas moved from type to var.

`A::B()` is a proc **reference**, so the trailing parens are part of the member access rather than an
invocation — `MemberAccessExpressionSyntax.IsProcReference` records it. A conditional's `:` is
recognised only when whitespace precedes it, matching the compiler; both that rule and `**`
associativity were probed rather than assumed, and are recorded in §8.

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

### M5 — Object tree and builtins ✅

- ✅ `TypePath` — normalised, comparable, path-keyed so `/mob/client` never collides with the
  builtin `/client`. Holds the normalised string rather than an intern-table handle; interning is a
  performance change and belongs with M9 if profiling asks for it.
- ✅ `TypeSymbol` / `VarSymbol` / `ProcSymbol`, merging declarations across files in include order.
  Every declaration site is kept, proc override chains are ordered, and `DeclaringCount` records how
  many sites used `proc/` so M11 can diagnose a duplicate definition.
- ✅ `ObjectTree` with inheritance resolution: implicit by path, redirected by `parent_type`, and
  cycle-guarded because `parent_type` is an ordinary assignment that a project can point in a loop.
- ✅ `TypeTreeBuilder`, driven off the include graph so files arrive in compile order.
- ✅ `dmc tree`, with `--under` and `--members`.
- ✅ `tools/builtins-gen` and `Resources/builtins.txt`, embedded in `Dm.Core`. 732 entries for
  BYOND 516.1666: 630 scraped from the reference, 88 parsed out of `stddef.dm`, 14 inheritance
  links. `Builtins.Seed` puts them in a tree before the project's own files.
- ✅ The acceptance target works. `mob.` resolves through `/mob` → `/atom/movable` → `/atom` →
  `/datum`, so it offers `loc`, `Move()` and `MouseMove()` — none of which appear in any file.

**Written as `builtins.txt`, not `builtins.json`.** `Dm.Core` is a NativeAOT target, so a
reflection-based deserializer is out and JSON would mean source generators for a fixed, read-only
schema. The line format needs a dozen lines to read and diffs cleanly when a BYOND release moves
something.

**The reference documents only four of the inheritance links** (`/mob`, `/obj`, `/turf`, `/area`).
The rest were read off the compiler with `initial(T:parent_type)` and are listed in the generator
with that provenance. Nothing in a path encodes them: `/mob` is a child of the root by path, and
without the link `mob.` offers nothing from `/atom`.

**The root is not part of the inheritance chain.** Global procs live there, and global scope is not
a base type — `istype(x)` is a call but `mob.istype()` is not valid DM. A test caught the first
version walking into the root, which would have put every global proc in the language into the
completion list after `mob.`.


- `TypePath` as an interned, comparable value type. The hottest key in the system.
- One `TypeSymbol` per path node, merging declarations across files in include order. Each type
  records: parent link (implicit by path or explicit via `parent_type`), declared vars, procs with
  override chains, and all declaration sites — a type is legitimately declared in N files.
- **Builtins.** `mob` has `Move()`, `Login()`, `loc`, `client`, `verbs`; none appear in user code.
  `Resources/builtins.txt` is assembled from **two sources**, because neither is complete:

  | Source | Provides | Method |
  |---|---|---|
  | `stddef.dm` (generated by Dream Maker, see §8) | All `#define` constants and `var/const` globals; the wrapper datums `sound`, `icon`, `matrix`, `database`, `database/query`, `exception`, `regex`, `dm_filter`, `generator`, `particles`; the macros `ASSERT`, `EXCEPTION`, `REGEX_QUOTE` | Parse with our own parser — it is valid DM |
  | `help/ref/info.html` (DM Reference) | Everything compiled into `byondcore.dll`: `/datum`, `/atom`, `/atom/movable`, `/mob`, `/obj`, `/turf`, `/area`, `/client`, `/world`, `/list`, `/savefile`, `/image`, `/mutable_appearance`, and the global procs (`istype`, `locate`, `view`, `text2num`, …) | Scrape with `tools/builtins-gen` |

  Parsing `stddef.dm` with our own parser doubles as a self-test: it is real BYOND-authored DM that
  exercises brace blocks, operator overloads, comma var lists, and stringification.

  `stddef.dm` is version-stamped (the sample on hand reads `516.1666`) and is regenerated by
  creating a file named `stddef.dm` in a project and compiling. `builtins.txt` records the BYOND
  version it was built from.

  Do not vendor `stddef.dm` into the repo — it is BYOND-generated output and this repo is public.
  `tools/builtins-gen` locates or regenerates it from the local install.

### M6 — Binder, semantic model, completion *(completion works)*

- ✅ `CompletionService.CompleteAt`, with the scope chain: locals, then the enclosing proc's
  parameters, then the members of the type it is on including everything inherited, then globals.
- ✅ The `.` / `:` distinction. `.` offers the declared type and its ancestors; `:` also offers
  members declared on **subtypes**. Neither offers an unrelated type's members, because `:` widens
  the check rather than removing it.
- ✅ Receivers that carry a type without inference: `src`, a typed local, a typed parameter, and a
  written path. An unresolvable receiver returns an empty list rather than everything.
- ✅ Builtins marked in the list, so a client can style them differently.
- ✅ `dmc complete <dme> <file> <line> <col>`. On mlaas, `other.` for a `/mob/pc` local returns 361
  items mixing the project's procs with inherited builtins.
- ✅ Inference through `new /path`, `as` clauses, and assignment from a typed source, in
  `Binding/TypeInference`. A declared type always wins; inference only fills a slot the author left
  empty. Where a name is assigned more than once, the nearest assignment *before the cursor* wins,
  since that is what it holds at the position being asked about.
- ⬜ Leading-`.` relative path resolution (§4a).
- ⬜ Macros in the bare-identifier list.
- ⬜ Semantic classification refinement — M2's reserved kinds 12–15.
- ✅ `dm_complete_at`, ABI 0.4, verified from C++.
- ✅ `Workspace.GetObjectTree` — the include graph, the builtins and the pushed buffers, wired
  together at last. Invalidated whole on any buffer change, which is M9's problem to make cheap.

**Globals are offered for a bare identifier but never after `.`.** They live on the root, and the
root is deliberately outside the inheritance chain: `istype(x)` is a call, `mob.istype()` is not
valid DM.

**Inference is the one place we knowingly disagree with `dm.exe`.** The compiler does no local type
inference whatsoever — `var/x = new /obj/item` then `x.hp` is *"undefined var"*, for the correct
member of the type on the previous line. Only a written type is checked. See §8; the full matrix is
in `docs/dm-language-notes.md`.

That was measured after this milestone was written, and it makes the item above a product decision
rather than a compiler-matching one. Two options were on the table: return the empty list the
compiler implies, or infer anyway and offer members the build will reject. **Decision: infer
anyway**, on the reasoning that an untyped `var/x = new /obj/item` is almost always a declaration
mid-edit rather than a finished one, and the members offered are the ones the author is reaching
for. The cost is real and is stated plainly in `INTEGRATION.txt`: accepting one of these completions
can produce code that does not compile.

This is the exception to §2's "match the compiler, then warn", and the only one. It is confined to
completion — nothing in the object tree or the diagnostics pretends the variable has a type. The
warning half of that rule is still owed, and belongs with the other M11 diagnostics: *"`x` is
untyped, so `.` cannot compile here — write `var/obj/item/x`"*.


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

**Declarations the compiler discards without a word** are the highest-value diagnostics here,
because nothing else in a DM toolchain reports them. The parser has to model these to match
`dm.exe`, and once it does it knows something the build output never tells the author:

| Construct | What DM does | Warning to raise |
|---|---|---|
| `proc` block indented inside a `var` block (§8) | accepts it, declares nothing; calling it is a runtime error | *"this proc is discarded — the `proc` block is inside a `var` block"* |
| A var name colliding with a builtin (`x`/`y` on an atom) | duplicate-definition **error** | already fatal; surface it early |
| `proc/` declared twice on one type | duplicate-definition error | `ProcSymbol.DeclaringCount > 1` |
| A var whose declared type does not exist (§8) | accepts the declaration; every *use* is an error, reported on the use line | *"`slot` is declared as `/clothing`, which no file declares — every read or write of it will fail"*. High value: the build is clean until someone touches the var, and the error then points at the reader rather than at the declaration. We know at declaration time. |
| `.` on an untyped var (§8) | *"undefined var"*, for every member including the right one | *"`x` is untyped, so `.` cannot compile here — write `var/obj/item/x`"*. This is the warning half of the M6 completion trade, and the fix is a quick-edit rather than prose. |

The first one is worth ranking above the rest. It was found in a shipped game where four mission
procs were declared that way and one is called from another file — a runtime error sitting on a code
path, with a clean build. Our tree *saw* those four while the compiler reported nothing, so the
information exists the moment the parser is correct; it must become a warning rather than being
discarded to match.

**Diagnostics must join the compiler's warning vocabulary, not run beside it.** `#pragma
ignore|warn|error <names>` and `-ignore/-warn/-error` share one set of identifiers, and a project
that silences `init_proc` in source expects it to stay silenced. Reusing the compiler's name
wherever we report the same thing is what makes that work; see §8. `init_proc` and `frequent_call`
are both off by default in `dm.exe`, so implementing them here surfaces lints most projects have
never seen.

Find-references and rename cannot be fully sound in DM because of `:` and string-based dispatch
(`call()`, `text2path()`). Decide whether rename is safe-subset-only or best-effort-with-warning.

### Deferred

`.dmm` map support, formatter, debug adapter. DAP requires auxtools-style injection into Dream
Daemon and is effectively a separate project.

---

## 7. ABI contract

`abi/dm_core.h` is the source of truth. ABI 0.4, 14 exports: version, last error, free, workspace
open/close/root, buffer set/close, classify plus its three accessors, document symbols, and
completion. Everything listed below is implemented; `dm_query_json` is the one entry still ahead of
the code, and lands at M7.

**Hot path — handles and accessors:**
```c
int32_t     dm_abi_version(void);
dm_status   dm_workspace_open(const char* dme_path, dm_workspace** out);
void        dm_workspace_close(dm_workspace*);
dm_status   dm_workspace_root(dm_workspace*, char** out_root);
dm_status   dm_set_buffer(dm_workspace*, const char* file, const char* utf8, int32_t len);
dm_status   dm_classify_range(dm_workspace*, const char* file, int32_t start_line,
                              int32_t end_line, dm_span_list** out);          /* M2 */
dm_status   dm_document_symbols(dm_workspace*, const char* file, int32_t encoding,
                                char** out_json);                              /* M4 */
dm_status   dm_complete_at(dm_workspace*, const char* file, int32_t line, int32_t character,
                           int32_t encoding, char** out_json);                 /* M6 */
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
- **Windows Defender quarantines the freshly published `dm_core.dll`** as a false positive. The
  publish reports success and then the file is gone from both `publish/` and `native/`, so the
  CMake post-build copy fails with an MSB3073 wall that never mentions antivirus. Confirmed via
  `Get-MpThreatDetection`, which named our own build output twice in consecutive publishes. A
  repo-directory exclusion is the fix, and CI on Windows runners will need the same.
- BYOND installed at `C:\Program Files (x86)\BYOND`. No `stddef.dm` ships in the install directory.
- **`stddef.dm` is generated on demand.** Creating a file named `stddef.dm` in a project and
  compiling causes Dream Maker to emit the code it auto-compiles at the start of every project.
  A generated copy is at `C:\Users\Anonymous\Desktop\world\stddef.dm` (529 lines, version 516.1666).
- **`stddef.dm` covers only part of the standard library** — constants and wrapper datums, not
  `/datum`, `/atom`, `/mob`, `/obj`, `/turf`, `/area`, `/client`, `/world`, `/list`, or the global
  procs. Those are compiled into `byondcore.dll`.
- `help/ref/info.html` (1.3 MB) is the DM Reference in structured HTML. It supplies the other half.
- `byondapi/` ships `byondapi.h` and `byondapi.lib` — relevant only to a future debug adapter.

### `dm.exe` command-line flags, and what each is worth to us

The complete set for 516.1666. Three of these are **oracles** — they make the compiler answer a
question we also answer, so we can diff against it instead of hand-checking.

| Flag | Does | Worth to us |
|---|---|---|
| `-o` | dumps the **resolved** object tree as XML | Already our oracle for `ObjectTree`; three declaration bugs and the proc/var matching came out of it. Lists types, vars and constant values. It does **not** record a var's declared type, so it can confirm that `feet` is a var on `/mob` but not that it is a `/clothing`. A bare override appears as a second `<var>` entry rather than replacing the first. |
| `-l` | lists every source file the `.dme` reaches | **Oracle for `IncludeGraph`.** Prints a `Source Files:` block — the `.dme` first, then each file in compile order, relative to the `.dme` directory with `\` separators. `stddef.dm` is not listed. It still writes a `.dmb`, so it is a build with a report rather than a dry run. |
| `-code_tree` | dumps the **syntactic** tree, before resolution, **plus the builtin branches** | An oracle for the *parser*, not for the type tree — it shows `mob / var / clothing / feet` as literal nested nodes exactly as written, including nesting that resolution later discards. Noisy: it emits the whole of `stddef.dm` ahead of the project and renders each initialiser as an indented `=` node. |
| `-D<name>[=value]` | defines a macro | See §8's table. Bare `-DNAME` defines it empty, not `1`. |
| `-clean` | full rebuild, ignoring incremental state | Only relevant if we ever shell out to the compiler. |
| `-verbose` | verbose progress | — |
| `-max_errors N` | caps reported errors, default 10000, `0` unlimited | Worth matching in spirit: our own diagnostics need a cap for the same reason. |
| `-full_paths` | full paths in messages rather than relative | Recommend it to any IDE that parses build output — relative paths are ambiguous once a project has two files of the same name. |
| `-ignore` / `-warn` / `-error <names,...>` | sets a warning's level; `-error` promotes it to a hard failure | See below. |

There is no output-path flag; the `.dmb` and `.rsc` names always derive from the `.dme`.

### What `-code_tree` gives that the other two do not

It emits the builtin type list from *any* project, even a two-line one — `/datum`, `/atom`, `/mob`,
`/obj`, `/turf`, `/area`, `/client`, `/world`, `/list`, `/savefile`, `/image`, `/sound` and the
wrapper datums — with **no builtin members**: no `loc`, no `Move()`, no `Login()`. So it does not
replace the `info.html` scrape that `builtins.txt` needs.

What it does carry is inheritance, in two forms that **disagree**, and the difference matters:

| Form | Says about `/mob` | Trustworthy? |
|---|---|---|
| `= (parent_type) (/atom/movable)` | `/atom/movable` | yes — matches the runtime probe |
| `.child_type (/mob)` under `/atom` | `/atom` | **no**, not as inheritance |

`.child_type` lists more branches, which makes it tempting, but it is a different relation. Taking
it as the parent would put `/mob` directly under `/atom` and lose `/atom/movable` from every `mob.`
completion.

Used as a *candidate list* rather than an answer, it is still worth having: diffing its branches
against `AddVerifiedParentTypes` found three links we never had — `/particles`, `/dm_filter` and
`/generator`, all `/datum`, each then confirmed by the runtime `initial(T:parent_type)` probe that
established the original fourteen. Without them a `/particles` var offered nothing from `/datum`,
so `P.` gave no `type`, `tag`, `New()` or `Del()`.

The probe that found them also re-confirmed the two results §8 calls surprising: `/list` and
`/client` print an empty `parent_type`, so they genuinely have no parent.

### Warning names are a shared vocabulary, and that constrains M11

The names taken by `-ignore` / `-warn` / `-error` are **the same identifiers as
`#pragma ignore|warn|error`**, and the compiler prints the name inline when one fires:

```
lint.dm:2:warning (init_proc): stuff: var will be initialized in a hidden init proc; ...
lint.dm:3:warning (frequent_call): New: this proc will be called very frequently
```

So the vocabulary is discoverable from any build log, which is how to enumerate it — the reference
does not list them.

Two ship **off by default** and exist for linting rather than for correctness. `dm.exe -warn
init_proc,frequent_call game.dme` turns them on.

Both are narrower than their descriptions suggest, and the shape of the restriction is unusual
enough that two plausible readings of it are both wrong. Measured on 516.1666:

| Name | Fires on |
|---|---|
| `init_proc` | a var whose initialiser is not a compile-time constant |
| `frequent_call` | `New()` or `Del()` overridden |

**Both share one trigger set: `/datum`, `/atom` and `/turf` *exactly*, plus the whole `/turf`
subtree.** The full matrix, identical for the two warnings:

| Type | Warns |
|---|---|
| `/datum`, `/atom`, `/turf` — the exact types | yes |
| `/turf/sub`, `/turf/a/b/c` — any turf subtype, at any depth | **yes** |
| `/datum/sub`, `/atom/sub` — subtypes of the other two | **no** |
| `/atom/movable`, `/obj`, `/obj/sub`, `/mob`, `/area`, `/client`, `/image` | no |

Neither "the three exact types" nor "`/turf` and its subtree" describes it on its own; it is the
union. Inheritance is not the rule either — `/obj` descends from `/atom`, which is in the set, and
stays silent.

The union does match what the two warnings are *for*. A var or a `New()` on `/datum` or `/atom`
exactly is inherited by every object in the game, so it is hot wherever it sits; and the map creates
turfs per tile for every turf subtype, so the whole turf branch is hot. Anything else is
instantiated on demand.

**The practical consequence is that neither lint sees the code where this usually costs.** A
`New()` override or a list var on `/obj/item` or `/mob/living` — which is where a large codebase
actually spends the time — is silent. They are much weaker as a codebase-wide audit than the names
suggest, which is an argument for implementing our own version at M11 rather than shelling out.

The trigger for `init_proc` is **whether the initialiser needs runtime evaluation**, not whether it
is a list. `= list(1,2,3)`, `= list()`, `= new /obj` and `= newlist(/obj)` all warn; `= 5`,
`= "text"`, `= 1+2`, `= /obj` (a path literal), `= null`, no initialiser at all, and anything
`const` do not. A constant is folded into the type and needs no per-instance work.

`frequent_call` is specific to `New` and `Del`. Neither an ordinary `proc/whatever()` nor an
override of `Enter()` triggers it.

The reference names "`/turf` or `/atom` or `/datum`" for both, which reads as three literal types
and does not mention the turf subtree — accurate as far as it goes, and incomplete in the one place
that matters for a real codebase.

**The pragma beats the flag, in both directions.** `-warn init_proc` with `#pragma ignore init_proc`
in the file is silent; `-ignore init_proc` with `#pragma warn init_proc` warns. So the flag sets the
starting level and the pragma overrides it from that point onward, which is the only sane reading
for us to copy. `#pragma push` / `#pragma pop` scope it to a region: with a `push`/`ignore`/`pop`
around one declaration, that declaration is silent and the next one still warns. `-error` and
`#pragma error` both promote to a genuine error, reported as `error (name):` rather than
`warning (name):` — worth noting, since the two formats differ by more than the word.

**The constraint this puts on M11:** a project can already write `#pragma ignore init_proc` to
silence a diagnostic, and `#pragma push`/`pop` to scope it. If our diagnostics live in a private
`DM0200`-style ID space, none of that reaches them, and we will report things the author has
explicitly silenced in source — which reads as our bug, not as a setting. So where a diagnostic of
ours is the same diagnostic the compiler has a name for, **it should carry the compiler's name** and
obey the pragma; a private ID is for the things `dm.exe` has no name for, such as the discarded-proc
warning in the M11 table. Decide the exact scheme at M11, but the pragma plumbing belongs with the
preprocessor work, since that is where `push`/`pop` state already lives.

The lints are also worth implementing ourselves. They are off by default, so most projects never see
them, and we can surface them continuously instead of once per build.

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
| **`/mob` has built-in `x`, `y`, `z`.** | Declaring `var/x` on a `/mob` subtype → *"x: duplicate definition (conflicts with built-in variable)"*. Found by accident, and a reminder of why `builtins.txt` (M5) is load-bearing. |
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
| **A conditional's `:` is member access only when tight against a bare identifier.** | With `b:c` a valid member access, `1 ? b : c` and `1 ? b :c` compile, while `1 ? b:c` and `1 ? b: c` fail with *"expected ':'"*. Changing what precedes the tight colon instead: `1 ? "0":"1"`, `1 ? f():g()`, `1 ? L[1]:z`, `1 ? 1:2` and `1 ? (y):z` all compile. So both halves matter — the space before it, and whether a member access is possible there at all. The one place in DM where spacing changes a parse. |
| **`/client`, `/list` and `/savefile` have no parent type.** | Printing `initial(T:parent_type)` for each builtin gives `/datum` for `/sound`, `/icon`, `/image`, `/matrix`, `/regex`, `/database` and `/exception`, `/atom` for `/turf` and `/area`, `/atom/movable` for `/mob` and `/obj`, and `/image` for `/mutable_appearance` — but nothing at all for `/client`, `/list`, `/savefile` and `/datum` itself. Assuming everything descends from `/datum` is wrong. |
| **A `proc` block inside a `var` block declares nothing, silently.** | `/datum/x` / `var` / `kept = 1` / `proc` / `vanished()` compiles with 0 errors and 0 warnings. `vanished` is then not a proc, not a var and absent from `vars`; calling it is a runtime *"undefined proc or verb"*. Give it a body and the file stops compiling with *"missing ="*, because everything under the misplaced header is read as var declarations. Found in a shipped game, where one such proc is called from another file. |
| **A var's declared type is resolved at the use site, not at the declaration.** | `mob` / `var` / `clothing/slot` with no `/clothing` anywhere compiles with **0 errors**, and `-o` lists `slot` as a real var. The first line that reads or writes `slot` then fails with *"slot: undefined type: /clothing"*, reported on the **use** line rather than the declaration. Any number of type segments behaves the same. Same shape as the discarded-proc trap: a clean build hiding a declaration the compiler cannot honour, so it earns an M11 warning. |
| **Separators in a var declaration create no types at all.** | For `mob/var/clothing/feet`, the entire object tree is `<mob>mob<var>feet</var></mob>` — one var on `/mob`, nothing named `clothing`. All three candidates are rejected as paths: `/clothing`, `/clothing/feet` and `/mob/clothing` each give *"undefined type path"*. The one-line form and the indented `var` block form produce identical trees. Checked against `-o` rather than inferred from a clean compile, which matters — the declaration compiles either way. |
| **A typed var in a `var` block does not create a type.** | `mob` / `var` / `atom/movable/locker` declares `locker` on `/mob` with type `/atom/movable`. Verified by assigning `locker` in a proc on `/mob`, and by declaring `/mob/atom/movable/unrelated` in the same file without a duplicate error. Without a `var`, the same shape is a bare override and the leading segments *are* the owning type. |
| **`?[]` guards a null list, not the index.** | `L?[i]` is `isnull(L) ? null : L[i]`, so an out-of-range numeric index still raises *"list index out of bounds"* — it is **not** the `L?.len >= i ? L[i] : null` bounds check it is sometimes described as. Verified across in-range, out-of-range, zero, negative and null-list cases. `L[?i]`, with the `?` inside the bracket, does not compile at all. On an assoc list a missing key is already null, which is where the operator earns its keep. |
| **`1#INF` and `1#IND` are number literals.** | Found in ter13's HudLib as `showing.tick_lag<1#INF`. A lexer that stops the number at `#` produces a number, a directive and a name, and the parse then fails. Undocumented in the reference. |
| **A trailing `.` inside an interpolation hole is legal.** | `world << "chasing [who.]"` compiles with 0 errors, and is in shipped game code. It collapses like a trailing path separator. |
| **`**` is left-associative, and unary minus binds tighter than it.** | `2 ** 3 ** 2` is 64, not 512. `-2 ** 2` is 4, not -4, which matches §4c putting unary at level 4 and `**` at level 5. |
| **Preprocessor directives carry no indentation of their own.** | Inside a one-tab proc body, `#ifdef` at column 0, at one tab, and at three tabs all compile clean. A directive between a header and its body therefore emits no `Indent`, and the parser must look past it for the one the next code line emits. |
| **A bare `;` at file scope is legal.** | A lone `;` at column 0 between two proc declarations compiles with 0 errors, with a `#warn` after it printing. |
| **DM performs no local type inference.** | `var/x = new /obj/item` then `x.hp` is *"x.hp: undefined var"* — the correct member of the type written on the line above. Only a declared type is ever checked: `var/obj/item/x` compiles. Tested across `new /path`, `new /path()`, `new /path{...}`, a later `x = new /path`, initialising from an already-typed local, a parameter's `as` clause, and `input(...) as mob`. None of them types the variable. |
| **`.` and `:` on an untyped receiver are checked differently.** | With `var/x` and no type: `x.hp` fails and so does `x.nowhere_at_all`, but `x:hp` compiles while `x:nowhere_at_all` fails. `.` rejects everything; `:` asks whether the name exists as a member of *any* type in the program. Untyped is not unchecked, and the correct completion list after `x.` is empty while after `x:` it is every member name in the program. |
| **The degradation to `:` is a property of the expression, not of the unknown type.** | `mk().elsewhere` compiles, but `var/x = mk()` then `x.elsewhere` does not. Both have an equally unknowable type; putting a variable in the way flips the answer. |
| **`dm.exe -D` injects preprocessor defines.** | `-DNAME`, `-DNAME=value` and `-DFN(x)=((x)*2)` all compile. Bare `-DNAME` defines it **empty**, not `1`: `#if NAME == 1` then fails with *"unexpected token: =="*, matching `#define NAME` with no body. A project built with `-D` flags is a different program from the one we analyse without them. |
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

### A clean compile proves almost nothing

The standard for everything in the table above: **do not stop at "it compiled".** A construct being
accepted says only that the parser allowed it, not that it produced what you expected. Three cases
in this document compile with zero errors and mean something other than they look like — a `proc`
block inside a `var` block declares nothing, a var with an undefined declared type is fine until
used, and `var/x = new /obj/item` gives `x` no type at all.

So a test has to make the two candidate readings produce *different output*, and the reading has to
be checked at the point where it becomes observable:

- **Ask whether the thing exists**, rather than whether the file compiled. Reference the type as a
  path, assign to the var, call the proc. `mob/var/clothing/feet` compiles under every hypothesis;
  what separates them is that `/clothing/feet` is *"undefined type path"*.
- **Read the tree, do not infer it.** `-o` prints what the compiler actually built. That is how the
  `clothing` question was settled, and it is stronger than any number of probes.
- **Include a negative control.** A probe that cannot fail proves nothing. Every accuracy test here
  is paired with one — a var that exists nowhere, an absolute-path equivalent, or the same construct
  with the missing type supplied.
- **Watch for probes that collide.** An early version of the var test used the name `name`, which is
  a builtin on `/mob`, and reported a duplicate-definition error that had nothing to do with the
  question. `x`, `y` and `z` on a `/turf` or `/mob` are the same trap.
- **Check the error's line number.** It is evidence. The undefined-declared-type error lands on the
  use, not on the declaration, which is the entire finding.

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
- **Differential testing against `dm.exe`.** Three flags make the compiler answer a question we also
  answer, so the check is a diff rather than a judgement call: `-o` for the object tree, `-l` for the
  include graph, `-code_tree` for declarations. `-o` already found three declaration bugs. Wiring
  `dmc includes` against `-l` over the corpus is the cheapest one left and needs no new machinery —
  both produce an ordered file list. Worth doing before M9 makes the include walk incremental, since
  that is exactly the change most likely to reorder it silently.
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

### Bench: /tg/station, 2026-08-03

The heaviest input available, roughly 1.5 million lines, run against a known-good object tree for
the same checkout (`objtree.xml`, 52 MB, 45,337 types).

| | reference | matched | missing | invented | recall |
|---|---|---|---|---|---|
| vars | 224,145 | 211,525 | 12,620 | 776 | **94.37%** |
| procs | 64,870 | 62,566 | 2,304 | 494 | **96.45%** |

7,162 files in 37 seconds cold, 229 of them with problems. 42,131 type nodes built. Nothing crashed
or hung, which at this size is most of what the run was for.

The gap has one dominant, already-known cause: the parser reads raw per-file tokens, so a macro in
declaration position becomes a path segment. Both sides of the diff pay for it — `/atom/VAR_PRIVATE`
is invented, and the var that belonged on `/atom` is lost. The largest single missing cluster is
`/datum/controller/global_vars` at 1,067 vars, which is the `GLOBAL_VAR` macro family. It is not the
only cause: 246 of 776 invented vars are macro-shaped and none of the 494 invented procs are.

### Bench: mlaas, the one we can be exact on

/tg/station is the stress test; **mlaas is the correctness harness.** 100 files, a `dm.exe -o`
reference for the same checkout, and — once the three bugs below were fixed — a perfect match:

| | vars | procs |
|---|---|---|
| raw | 1493 / 1493 | 1153 / 1153 |
| preprocessed | 1493 / 1493 | 1153 / 1153 |

100.00% both ways, nothing invented. That exactness is what makes it the right place to debug: on
/tg/station a 1% deficit is indistinguishable from a legitimate difference, because their build
passes `-D` flags we never see and we are therefore analysing a different program. On mlaas any
deviation at all is our bug.

Finding the last one is the argument for the whole approach. It reproduced on mlaas at 128 procs and
113 vars lost, was bisected to a single line in one file, reduced to a ten-line fixture, and fixed —
none of which was practical against 1.5M lines.

### Same bench, `--preprocessed`

| | raw | preprocessed |
|---|---|---|
| var recall | 94.37% | **96.00%** |
| proc recall | 96.50% | **97.90%** |
| macro-shaped phantom types | 246 | **9** |
| files with parse problems | 229 | **102** |
| wall clock | 37s | **15s** |

The half it was built for works: expansion happens, `GLOBAL_VAR(my_thing)` lands on
`/datum/controller/global_vars`, `VAR_PRIVATE/hidden` lands on `/atom`, and the phantom types all
but disappear. Vars go up accordingly.

Parse problems are now **below** the raw path — 102 against 229 — after two causes were found and
fixed. Both were found by asking the compiler-style question, "what is the diagnostic", rather than
by reasoning about the count:

- **The conditional-`:` whitespace probe read a repositioned span.** `ExpressionParser` answered
  §4c's spacing rule by inspecting the character before the token's span. Reposition that span onto
  a macro invocation and the character before the *invocation* decides the parse of text inside it.
  This produced 5,064 spurious *"expected `:` to complete the conditional"* and dragged 6,044
  *"expected an expression"* along behind it. `TokenSource.HasWhitespaceBefore` now captures the
  fact against the token's real location, where it is still knowable. Fixing it took parse problems
  from 1,630 to 102.
- **`#pragma syntax` does not survive preprocessing.** Directive lines are consumed, so the
  statement parser never sees `C for` / `C switch` and parses those bodies under the default
  grammar. Confirmed on a fixture: both pragma forms parse clean raw and fail preprocessed. Barely
  visible in this bench — /tg/station has 31 pragmas and no `syntax` ones — but it is a real hole,
  and the state has to ride the stream as data.

**The reference is reproducible, and `dm.exe` handles /tg/station fine.** `dm.exe -o -DCBT
tgstation.dme` exits 0 with zero errors and produces a file byte-identical to the `objtree.xml` we
had been diffing against — same 54,626,955 bytes. So the baseline is trustworthy, it is already a
`-DCBT` build, and it can be regenerated on demand rather than treated as a fixed artefact. Any
remaining disagreement is ours.

**Injected defines did not close the gap, and the earlier guess that they would was wrong.** Passing
`-DCBT` grows our tree by roughly 940 var declaration *sites* and adds not one new owner/name pair;
recall is identical to the digit. `CBT` mostly steers `MAP_SWITCH`, which chooses between operands
rather than declaring different things. The `-D` support is still necessary — a project whose build
sets flags we ignore is a different program — it simply is not what this particular gap was made of.

What the gap is actually made of, as two clusters worth one look each:

| | Top owners |
|---|---|
| missing | `/datum/controller/subsystem/*` — shuttle 57, mapping 44, air 41, ticker 35, job 28 |
| invented | `/particles/*` — 16-17 each across many, and `/datum/admin_verb/cmd_admin_areatest` 21 |

The subsystem half was a **declaration-parser gap, not a preprocessor one**, and is now fixed.
`SUBSYSTEM_DEF(X)` expands, via `\` continuations, to a run of declarations on one logical line that
*ends* with a bare type path, and the indented block written under the invocation belongs to that
path. Reduced to the construct that actually breaks, with no macros involved at all:

```dm
var/glair;/datum/sub/air
	var/thing = 1
```

`dm.exe` declares the var **and** the type, with `thing` under the type. We declared only the var:
the `;` ended the name list, and the parser then consumed to the end of the line and swallowed the
indented block with it. A `;` that hands the rest of the line to a new declaration now suppresses
both. Verified separately that `var/a; b` still shares one `var/` and that a trailing `;` still ends
the line, since dm.exe accepts all three and they take different paths.

Worth noting how nearly this was misdiagnosed. Three earlier `printf` fixtures "reproduced" it with
a literal `
` in the source, which the lexer correctly read as a backslash name-escape (§8) — so
they failed for an unrelated reason and pointed at braces, which turned out to be fine. Only a
heredoc fixture with real continuations isolated it. The same class of error as the `grep` patterns
earlier: **the harness was wrong, and it produced a confident, plausible, wrong answer.**

| /tg/station | before | after |
|---|---|---|
| var recall | 95.42% | **96.00%** |
| proc recall | 96.04% | **97.90%** |

The subsystem cluster is gone from the top misses entirely; what remains is scattered at 5-9 per
owner.

### The oracle has a blind spot, and "invented" is overstated because of it

`/particles` turned out not to be our bug. `dm.exe -o` **omits whole builtin branches**: a
`/particles/pollen`, `/sound/x` or `/image/x` subtype is absent from the dump entirely, even when it
declares brand-new vars of its own.

```dm
/particles/withvar
	var/mine = 1        // absent from -o
/datum/plain
	var/mine = 1        // reported
```

It is not about builtin *members* — `/obj/box` overriding the builtin `name`, and `/mob/guy`
overriding the builtin `Login()`, are both reported. It is the branch that decides.

The reference contains **zero** entries under `/particles`, `/sound` and `/image`; our tree contains
474, correctly. So of 1,168 "invented" vars, 477 are in this blind spot and are right. The honest
remainder is 691 vars and 1,096 procs.

**Do not chase the invented column to zero.** Doing so would mean deleting types the compiler agrees
exist. Recall is unaffected — it is measured against the reference — but any precision figure has to
exclude these branches or it is measuring the oracle rather than us.

The third cause was **indentation across a skipped `#if` region**, found on mlaas rather than here:
the region takes its `Indent` tokens with it while the matching `Dedent`s survive in live code, so
the stream pops levels it never pushed and every later declaration reads one level too shallow.
Members land on the root. `IncludeGraph` now tracks the file's own depth across skipped regions and
emits the difference before each surviving token. On mlaas that was worth 128 procs and 113 vars —
the whole gap.

Both now beat the raw path outright, and mlaas stays exact on both.

Re-run this table after each attempt. It has now disagreed with the obvious expectation four times:
once by improving the thing that looked broken, once by leaving the headline unmoved after a fix
that removed 11,000 diagnostics, once by hiding a whole-project defect behind a metric that moved
less than a point, and once by not moving at all for the `-D` flags that were supposed to explain
it.

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
  skipped. Added §4c with the reference's precedence table.
- **2026-08-03** — M4 expressions landed: `ExpressionParser` over §4c, wired into var initialisers.
- **2026-08-03** — M4 statements landed: `StatementParser` with both `switch` grammars, all four
  `for` shapes, and `#pragma syntax` mode tracking through `SyntaxModes`. Proc bodies are parsed
  rather than skipped. Nine parser gaps and one lexer gap came out of running it over the corpus.
- **2026-08-03** — Pinned the leading-`.` search rule exactly (§4a): it follows path ancestry and
  ignores `parent_type`, validates the whole relative path with backtracking, and takes the nearest
  ancestor under which all of it resolves. The previous "first hit wins" wording would have been
  implemented against the inheritance chain, which is wrong. Also established that a var's declared
  type is resolved at the **use** site — a missing type compiles clean and fails wherever the var is
  read — and that separators in a var declaration create no types, checked against `-o` rather than
  inferred from a clean compile. Added the evidence standard behind all of it to §8, and corrected
  the lint descriptions: `init_proc` keys on a non-constant initialiser, and both fire on `/datum`,
  `/atom` and `/turf` exactly **plus the whole `/turf` subtree**. Two readings of that set —
  "`/turf` only" and "the three exact types, no subtree" — are each wrong in a different direction,
  so the matrix is recorded in full.
- **2026-08-03** — Recorded the full `dm.exe` flag set in §8, with `-l` and `-code_tree` identified
  as oracles alongside the `-o` we already use, and differential testing added to §9. Warning names
  turn out to be shared with `#pragma ignore|warn|error`, which constrains how M11 identifies its
  diagnostics — noted there and in §8.
- **2026-08-03** — M6 type inference landed: `Binding/TypeInference`, wired into
  `CompletionService`. Testing the milestone's own premise against `dm.exe` found that DM has **no
  local type inference at all** and that `.` on an untyped var rejects every member, which turned
  the item from compiler-matching into a deliberate divergence — recorded in §6 and §8, and stated
  for integrators in `INTEGRATION.txt`. Also verified `dm.exe -D` and added injected defines to the
  M4 remainder.
- **2026-08-03** — Doc accuracy pass across `PLAN.md`, `ROADMAP.txt` and `INTEGRATION.txt`. Export
  count, binary size, test count and ABI version brought to the numbers the tree actually produces;
  §5 relabelled as the target layout with the unbuilt parts marked; `builtins.json` corrected to
  `builtins.txt` throughout. `INTEGRATION.txt` had listed M5 and M6 twice and still described
  syntax diagnostics as unshipped.
