# Fixture suite

```
dotnet test                      # everything; BYOND-dependent cases skip if dm.exe is absent
pwsh tests/fixtures/run.ps1      # the same, plus the runtime run and (with -Probes) the corpus
```

**`dotnet test` is the entry point.** The fixtures are discovered from disk by
`tests/Dm.Core.Tests/Fixtures/FixtureTests.cs`, so **adding a case is adding a
file** — never a code change — and one command covers unit tests and fixtures
alike. `run.ps1` remains for the two things xunit is a poor fit for: running the
compiled world under DreamDaemon, and the 255-probe ratchet.

## When BYOND updates

This is what the suite is shaped for. A new version should be answerable by
running it and reading what moved.

1. `dotnet test`. `The_goldens_match_the_installed_compiler` fails first and by
   name, because every `.expected` in the tree is data captured from one
   compiler version and comparing it against another silently measures the wrong
   thing.
2. `pwsh tests/fixtures/run.ps1 -Probes` — the 255 mined messages. Anything that
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
           else_*.dm     the boundaries of the separator-run rule (notes §19)
           dup_*.dm      duplicate proc definitions, same type and subtype
           local_in.dm   the relational `in` as a local initializer
           names/        one .dme per case - see the masking note below
           pragma/       numeric pragma ids, unknown warning names
services/  tier 2: projects that compile clean AND carry //? marks
run.ps1
```

## Tier 2: the services answer end to end

`services/` closes the axis the rest of the suite cannot reach: everything else
checks the pipeline against `dm.exe`, and nothing checked that the SERVICES —
completion, definition, hover, signature help — answer correctly through a real
workspace. `ServiceFixtureTests` opens each `.dme` here the way an IDE does and
answers every mark in the source:

```
//? complete 7:4 => hp, weapon, !on_subtype    these present, that absent
//? complete 25:20 => (empty)                  the whole list must be empty
//? definition 7:4 => types.dm:8               the nearest hit
//? hover 7:4 => /mob/test/hp                  the resolved detail
//? signature 13:13 => heal @ 1                the proc and active parameter
//? references 7:4 => types.dm:12 write, code.dm:7 write
//                                             every use, as file:line kind -
//                                             exact set equality, so a missing
//                                             hit and a surplus one both fail
```

Positions are 1-based line:column, the CLI's convention, so a failing mark is
reproduced verbatim with `dmc complete|definition|hover|signature`. The
projects also compile clean under dm.exe and are swept by every gate above, so
the same files hold the zero-invented line. Adding a case is adding a comment
line — and the runner fails a fixture with no marks at all, because a probe
that cannot fail proves nothing.

It earned its keep on its first run: definition and hover returned nothing on
the first character of every name, a shipped off-by-one no unit test had
reached because every unit test builds its own tree.

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

**A `.expected` can pin the summary as well as the lines.** `line severity text`
rows say "dm.exe reports this"; they cannot say "and nothing else". A row
`total 0 errors, 1 warning` must appear verbatim in the compiler's own summary,
which is how a fixture asserts *silence* — `errors/const_fold` turns `init_proc`
on with a pragma, keeps one `list()` control that must warn, and the total is
what proves every const-derived line beside it stayed quiet. Spell the count as
dm.exe does: `1 warning`, `2 warnings`.

## The mined probe corpus

`errors/probes/` is 255 single-message probes mined from the diagnostic lab
(`byondtest/lab/errors`) by `tools/mine_probes.py`. Each is recompiled during
mining rather than trusted from the lab's cache, so probes needing assets we do
not copy, or whose message has moved, drop out without a hand-written blocklist.
Messages a language server can never emit - map, skin, asset, codegen, internal
sanity checks - are filtered by message text. 109 probes that compile clean and
51 duplicate message sets are skipped.

It runs only with `-Probes`, since 255 compiles plus 255 diagdiffs takes a few
minutes.

**It is a ratchet, not a gate.** `BASELINE.txt` records the current agreement
count and the names — asserting "must agree on all" would fail a couple of
hundred times and teach nothing — and the run fails only if agreement *drops*.
Raise it deliberately with `-Probes -UpdateBaseline`, after reading why it moved.

**`invented` is not the metric here, and that is the subtle part.** These files
are broken on purpose. dm.exe stops after the first error; we recover and carry
on, which is a *feature* - an editor buffer is malformed on every keystroke. So
extra diagnostics on a must-fail file are recovery working, not spurious output.
The zero-invented rule belongs to code that **compiles clean**, which is what
sections 1-3 cover.

The baseline's count against 255 is also the honest denominator for M11: every
probe outside it is a known compiler message we say nothing about.

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
[1] every fixture compiles as recorded
  ok    errors/else_orphan
  ok    errors/else_unseparated
  ok    names/in
  ...
  ok    ok/ok compiles clean
[2] ok/ runs, every check passing
  ok    ok/ runtime, N checks
[3] diagdiff: zero invented
  ok    diagdiff errors/semantic.dme
  ...
passed N   failed 0   skipped 0
```

The counts are written as `N` on purpose. Every one of them rises whenever a
case is added — which is most commits here — so a real number in this block is
wrong by the next one and says nothing when it is right. It read `109 checks`
and `passed 19` against a run of 135 and 97 until 2026-08-16. **What matters is
`failed 0   skipped 0`**: a skip is a compiler-backed tier that did not execute,
and it is reported as a skip rather than folded into the pass count.

The runtime step runs DreamDaemon with `-safe`, not `-trusted`: nothing in `ok/`
needs trusted mode, and a `-trusted` world waits on a GUI approval prompt when
nothing interactive can click it, which reads as a hang and "no log produced".

## Where it runs

**CI runs this suite against the real compiler**, in the `byond-fixtures` job:
BYOND is Windows-only and no runner ships it, so the job fetches the standalone
zip for the build in `ci.yml`'s `BYOND_BUILD`, points `DM_BYOND_BIN` at it, and
runs `dotnet test` plus `run.ps1 -Probes`. The build is pinned rather than
floating — a job that always took the newest would go red on BYOND's release
schedule rather than on a commit, and name no change of ours.

That job is the only place the compiler-backed assertions execute. The `test`
job runs on Linux, where every one of them takes its skip path, so before it
existed CI was checking that our parser still agreed with our parser.

**One tier does not run there: the runtime world.** A hosted runner cannot start
DreamDaemon at all — it dies in about five seconds with `0xC0000135`,
`STATUS_DLL_NOT_FOUND`, on an image lacking something the daemon links against,
while `dm.exe` from the same directory compiles everything perfectly. CI
therefore passes `-RuntimeOptional`, which downgrades that one step to a skip;
everything else still gates. **Never pass it on a machine that can run the
daemon.**

The consequence, stated rather than left to be discovered: `ok/` compiles in CI
but is not RUN there, so **the tier that catches a BYOND release changing what DM
*means* rather than what it accepts is a local gate.** `run.ps1` prints the
daemon's unresolved imports when this fires, so if that ever names an installable
component the step can be turned back on.

With no `dm.exe` the compiler-side checks still **skip** rather than fail, which
is what keeps a Linux `dotnet test` and a machine without BYOND usable; a skip
is reported as a skip, never as a pass.

**Anything that shells out to `dm.exe` must be told which one.** `dmc diagdiff`
reads `DM_BYOND_BIN` before falling back to the install location, and `run.ps1`
forwards its `-Byond` to every `diagdiff` call. Without that, a run against a
standalone build compiles the fixtures with the new compiler and then diffs the
diagnostics against the installed one — two compilers in one run, reported as
agreement.
