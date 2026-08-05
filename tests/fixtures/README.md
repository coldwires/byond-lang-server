# Fixture suite

```
dotnet test                      # everything; BYOND-dependent cases skip if dm.exe is absent
pwsh tests/fixtures/run.ps1      # the same, plus the runtime run and (with -Probes) the corpus
```

**`dotnet test` is the entry point.** The fixtures are discovered from disk by
`tests/Dm.Core.Tests/Fixtures/FixtureTests.cs`, so **adding a case is adding a
file** — never a code change — and one command covers unit tests and fixtures
alike. `run.ps1` remains for the two things xunit is a poor fit for: running the
compiled world under DreamDaemon, and the 252-probe ratchet.

## When BYOND updates

This is what the suite is shaped for. A new version should be answerable by
running it and reading what moved.

1. `dotnet test`. `The_goldens_match_the_installed_compiler` fails first and by
   name, because every `.expected` in the tree is data captured from one
   compiler version and comparing it against another silently measures the wrong
   thing.
2. `pwsh tests/fixtures/run.ps1 -Probes` — the 252 mined messages. Anything that
   moved is a message the new version renamed, relocated, or stopped emitting.
3. Read each difference before touching anything. A changed golden is a *finding*
   about the new compiler; that is the output you wanted.
4. Update the `.expected` files and `BYOND_VERSION.txt` **together**, and
   re-baseline the corpus with `-Probes -UpdateBaseline`.

Nothing here is repaired by editing a version number. The failure is the report.

Real DM, compiled and run by the real compiler, and diffed against what we say
about it. Complements the unit tests rather than repeating them: those check a
function, these check the whole pipeline against `dm.exe`.

## Why this exists, in one fact

**`dm.exe` reports zero diagnostics on /tg/station's 1.5 million lines.** So a
corpus of correct code can only ever tell us one thing: *what we wrongly
reject*. It is structurally incapable of telling us what we wrongly **accept**,
because correct code does not contain the wrong thing.

Two bugs from 2026-08-05 make that concrete:

- `for(x in L)` with `x` already declared parsed as a **bare** `for` over a
  nonsense initializer, with **no diagnostic at all**. Millions of lines contain
  that construct and not one complained. It surfaced by accident.
- We accepted `var/in`, which `dm.exe` rejects. No quantity of correct DM
  contains `var/in`, so no corpus could ever have found it.

The corpus remains the only thing that finds what nobody thought of — six of
that day's eight findings came from it. This suite covers the two axes it
cannot reach: **must-fail** and **must-mean-this**.

## The three questions

| | question | where |
|---|---|---|
| 1 | Does DM do what we think? | `ok/` compiles clean **and runs**, every check passing |
| 2 | Does `dm.exe` reject what we think? | `errors/`, against the recorded diagnostics |
| 3 | Do we agree with it? | `diagdiff` over every fixture: **zero invented** |

Question 1 is the one that needs running rather than compiling. A construct that
compiles proves only that the parser allowed it — PLAN.md §8. `ok/` therefore
computes a value per case and compares it to a constant; a wrong parse shows up
as a wrong number instead of a clean build.

## Layout

```
ok/        _harness.dm   CHECK(), the world, the summary line
           semantics.dm  language behaviours from PLAN §8, verified at runtime
           parsing.dm    constructs we once rejected on code dm.exe accepts
           macros.dm     preprocessor shapes real codebases use
errors/    semantic.dm   undefined member, subtype through `.`, untyped receiver
           names/        one .dme per case - see the masking note below
run.ps1
```

## Two rules the layout encodes

**One expectation per compilation unit.** A file that must compile clean cannot
also hold cases that must fail, and — less obvious — **a syntax error stops
`dm.exe` before it runs the semantic checks**, so two failing cases in one file
mask each other. The first version of `errors/` reported one diagnostic out of
eight for exactly that reason. Hence `names/in`, `names/as` and `names/to` are
three separate `.dme` files.

**The harness must not use what it tests.** `_harness.dm` is deliberately boring
DM: no macros, no brace blocks, no contextual keywords. A harness written in the
constructs it is checking cannot report their failure.

## The mined probe corpus

`errors/probes/` is 252 single-message probes mined from the diagnostic lab
(`byondtest/lab/errors`) by `tools/mine_probes.py`. Each is recompiled during
mining rather than trusted from the lab's cache, so probes needing assets we do
not copy, or whose message has moved, drop out without a hand-written blocklist.
Messages a language server can never emit - map, skin, asset, codegen, internal
sanity checks - are filtered by message text. 109 probes that compile clean and
51 duplicate message sets are skipped.

It runs only with `-Probes`, since 252 compiles plus 252 diagdiffs takes a few
minutes.

**It is a ratchet, not a gate.** We agree on **38 of 252** today; asserting
"must agree on all" would fail 214 times and teach nothing. `BASELINE.txt`
records the number and the names, and the run fails only if agreement *drops*.
Raise it deliberately with `-Probes -UpdateBaseline`, after reading why it moved.

**`invented` is not the metric here, and that is the subtle part.** These files
are broken on purpose. dm.exe stops after the first error; we recover and carry
on, which is a *feature* - an editor buffer is malformed on every keystroke. So
extra diagnostics on a must-fail file are recovery working, not spurious output.
The zero-invented rule belongs to code that **compiles clean**, which is what
sections 1-3 cover.

That 38/252 is also the first honest denominator for M11: 214 known compiler
messages we say nothing about.

## Adding to it

**Every new finding gets a case here, in the same change.** That is the whole
point: this suite is the regression net for things learned the expensive way,
and a finding that does not land in it will be re-learned.

- a behaviour verified against `dm.exe` → `ok/semantics.dm`, plus
  `docs/dm-language-notes.md`
- a construct we rejected wrongly → `ok/parsing.dm` or `ok/macros.dm`
- something `dm.exe` rejects that we should too → `errors/`, with its diagnostic
  captured from the compiler rather than hand-written
- record the value, not just that it parses

## Expected output

```
[1] ok/ compiles and runs
  ok    ok/ compiles with 0 errors, 0 warnings
  ok    ok/ runtime, 38 checks
[2] errors/ is rejected as recorded
  ok    errors/semantic
  ok    errors/as
  ok    errors/in
  ok    errors/to
[3] diagdiff: zero invented
  ok    diagdiff errors/semantic.dme
  ...
passed 11   failed 0   skipped 0
```

BYOND is Windows-only and is not on CI runners. With no `dm.exe` the
compiler-side checks **skip** and ours still run; a skip is reported as a skip,
never as a pass.
