# DM formatting spec

The rules the formatter applies, and the ones it deliberately does not.

> **Live document, and the source of truth for the formatter.** This file is written to be
> edited: change a rule here and the implementation follows it, rather than the other way round.
> Each rule says whether it is **decided** or **open**, and where its default came from.
> Last updated: 2026-08-16.

**Status: F1–F11 decided. F1, F2, F3, F4, F5 and F10 implemented in `FormattingService`; F6, F8,
F9 and F11 are not, and `textDocument/formatting` is not served yet.** The service is built on the **token stream**, which
is what makes the never-touch list below enforceable rather than aspirational — a regex over source
text cannot tell a `##` paste from an operator, and would eventually rewrite one.

---

## Where the defaults come from

Measured across ~48,000 lines of the author's own DM (`mlaas`, `madridspy`, `warklan`,
`dm-bench`, `world`) on 2026-08-16, with strings and comments stripped first. **`mlaas` is the
reference codebase** — the author's call, and it is also the project this analyzer is exact
against.

Two measurements are worth carrying because they contradict what a formatter would assume:

| | mlaas | |
|---|---|---|
| indentation | **tab**, 105 of 107 files | 274 of 282 files across all five projects; **not one** indents with spaces |
| `var/x = 1` | 98% spaced | |
| `x = a` | 93% spaced | |
| `if(` `while(` `for(` | 79% / 94% / 87% **tight** | no space before the paren |
| blank lines | 90% single | 2,501 single, 278 double, 109 triple-or-more |
| trailing whitespace | none | 1 line in 48,000 across every project |
| commas | 64% spaced | **no convention** — both spellings throughout |
| `+` `-` `*` | 34% / 48% / 46% spaced | **no convention** — roughly a coin flip |

**A measurement that was wrong, kept because it is the trap.** The first pass reported arithmetic
as 95% tight. It was counting `/` — and `/mob/pc` matches "word, slash, word", so the figure was
3,836 path separators. `/` is never a formatting decision in DM; it is mostly a path.

---

## What the formatter must never touch

This section is the load-bearing one. **DM is not C#: several kinds of whitespace change the
program.** Every item here is a finding this project already paid for; the section references are
`PLAN.md` §8 and `docs/dm-language-notes.md`.

| Never | Because |
|---|---|
| **Leading indentation** (v1) | Indentation is **semantic**. A `proc` block indented one level too far into a `var` block declares **nothing** and compiles with 0 errors (notes §18, our `DM0300`). Reindenting can silently add or remove procs. Deferred to v2 with a guard — see below |
| **Whitespace before a ternary `:`** | `1 ? b : c` is a conditional and `1 ? b:c` is a **compile error** — the tight colon is read as member access. The one place in DM where spacing changes a parse (notes §15). Removing that space breaks every ternary in the file |
| **Anything on a preprocessor line** | A `##` paste is whitespace-sensitive: `a##b` glues, `a ## b` does not. This project has already been bitten — a lost whitespace fact re-lexed `(a) ? u : v` as `(a)?u:v` and cost 32 invented diagnostics |
| **Inside `{" ... "}`, `@raw` strings, or any string** | A multiline string carries its newlines and indentation as **content**. Reflowing one changes program data |
| **After a `\` line continuation** | The continuation discards every whitespace character that follows, so what looks like indentation is string content (notes §10) |
| **Line endings** | Rewriting them touches every line of the file and destroys `git blame` for the rest of the team. Already this project's stated position — `INTEGRATION.txt` §5. The author's own code is mixed (mlaas 106/107 CRLF, warklan 21/31) and that is fine |
| **Final newline** | About **half** the author's files lack one. Adding it would touch ~140 files for no behavioural gain. Left alone unless a rule below turns it on |

---

## The rules

### Decided

| # | Rule | Default | Basis |
|---|---|---|---|
| F1 | Spaces around `=` | `var/x = 1`, `x = a` | measured, 93–98% |
| F2 | Space after a comma, none before | `f(a, b)`, `list(1, 2)` | **author's call**, against a 64/36 split. A list ending on a comma keeps its closer tight — `f(a, )` is legal DM and reads as an oversight rather than as formatting |
| F3 | Spaces around **binary** `+` `-` `*` `%` `%%` `**` | `a + b` | **author's call**, against a ~50/50 split. `/` is deliberately absent — in DM it is overwhelmingly a path separator. A **unary** operator is left alone: `-1` stays tight, and so do DM's pointer forms `*p` and `&x`, which are the binary operators at a tighter precedence |
| F4 | No space before a keyword's paren | `if(x)`, `while(x)`, `for(x)`, `switch(x)` | measured, 79–94%; `switch` see F10 |
| F5 | Trim trailing whitespace | | already true in the corpus; a no-op that costs nothing |
| F6 | Collapse runs of 3+ blank lines to one | | measured, 90% single |
| F7 | Indentation | **untouched in v1** | semantic; see above |
| F8 | Spaces around comparison and logical operators | `a == b`, `a && b`, `a < b` | measured, **69–98% spaced**; `==` alone is 98%. Was O1 |
| F9 | One space after `//` | `// comment` | measured, 74% — the **weakest** rule here, and purely cosmetic. Was O2. A run of `///////` drawn as a banner is left alone |
| F10 | `switch(` follows F4 | `switch(x)` | measured at only 57% tight, so this is consistency with `if`/`while`/`for` rather than a convention read off the code. ~30 sites. Was O4 |
| F11 | Exactly one blank line before a proc or verb declaration | | measured, 84%. **The only rule that INSERTS** rather than removing, so it is the most visible on a first run. Safe: a blank line carries no indent level, so it cannot change block structure. Was O3 |

**F2 and F3 are the two that will churn.** They are the author's decision made knowingly: the code
has no convention there, so the formatter is *establishing* one rather than conforming to one.
Expect a large first diff on `mlaas` and a very large one on `warklan`, which is 19% spaced on both.

### Deliberately not rules

- **No line-length limit and no wrapping.** Wrapping a DM expression means deciding where a
  continuation goes, and `\` continuations interact with strings and macros (above). Nothing in the
  corpus suggests a column limit is wanted.
- **No brace-style rule.** Braces are rare in the author's DM — 0 sites in `mlaas`, 39 in
  `madridspy` — and they are mostly macro-generated, where the formatter must not go anyway.
- **No import/include ordering.** `#include` order is **semantic** in DM: it decides override
  resolution, and swapping two lines can change what the program means (§4a). Sorting them is a
  program change wearing a tidy-up's clothes.

### Open

**None.** O1–O5 were closed on 2026-08-16 by measuring them the same way F1–F4 were measured;
they are now F8–F11, with one closed as moot:

- **O5, alignment of consecutive `=`, does not exist in this corpus.** Zero aligned `=` in `mlaas`,
  in `#define` blocks and in ordinary code alike. `warklan`'s `#define SPAWN_RATE\t\t200` looked
  like a counterexample and is not — those are object-like macros with no `=` at all. Doubly moot,
  since preprocessor lines are on the never-touch list regardless.

Add new questions here rather than editing a rule in place, so the reasoning behind an existing
default stays readable.

---

## Indentation, and what v2 needs

Indentation is the useful half of a formatter and the dangerous half of this one, so it is a
separate piece of work rather than a rule to switch on.

The rule the compiler applies, probed 2026-08-16 (§8): **a tab and a space each count as one
column, the first indented line under a top-level declaration sets that declaration's unit, and
every level is exactly one unit deeper.** Two declarations in one file may use different units — a
tab-indented proc beside a four-space one compiles.

So "normalise indentation" is not one operation. Before it can ship it needs:

1. **A probe matrix**, the way every other feature here got one: what survives reindentation, what
   a mixed-unit file does, what happens across a continuation and inside a brace block.
2. **The tree-diff guard.** Format, re-parse, compare the object tree, and **emit nothing if a
   single declaration moved**. This repo already proves two builds agree that way —
   `dmc bench --verify` compares 335,519 declarations on tgstation — so the machinery exists. A
   file the formatter refuses is itself a finding worth showing the author.

Until both exist, F7 stands: leading whitespace is not touched.

---

## Configuration

Read from `.editorconfig`, which is a standard the editors already understand and which the
largest DM codebase already ships:

```ini
[*.dm]
indent_style = tab
trim_trailing_whitespace = true
insert_final_newline = false
```

Where `.editorconfig` says nothing, the defaults in the table above apply. Where it disagrees with
this spec, **it wins** — a project should not have to argue with the formatter about its own house
style, and this is how `warklan` keeps its tight `=` without `mlaas` being imposed on it.

Anything not expressible in `.editorconfig` (F2, F3, F4) gets a `dm.format.*` setting only if
someone asks. The spec is the default; the file is the override.

---

## How this reaches an editor

`textDocument/formatting` returns the whole document's edits, which is what a client's
format-on-save calls. `rangeFormatting` and `onTypeFormatting` are not planned for v1.

Format-on-save is a **client** setting, not a server one — `editor.formatOnSave` in VS Code, the
equivalent in Rider and Neovim. Serving the method is all the server has to do.

Because v1 touches nothing that can change what a file declares (F7 holds indentation, and the
never-touch list holds the rest), **format-on-save is safe to leave on permanently**. That stops
being true the day indentation is included, which is the other reason it waits for the guard.
