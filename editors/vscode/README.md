# DM Language Server for VS Code

Diagnostics, completion, hover, go-to-definition, and symbols for BYOND DM,
served by `Dm.Lsp` over stdio.

**Using a different editor?** `docs/lsp.md` is the protocol — launch,
`initializationOptions`, the custom `dm/*` methods, what is deliberately not
served, and Neovim and Helix configuration. The settings below are this
client's spelling of the same two options.

## Run it against your game, today

```
dotnet build src/Dm.Lsp          # from the repo root
cd editors/vscode
npm install                      # once, pulls vscode-languageclient
```

Open `editors/vscode` in VS Code and press **F5** — `.vscode/launch.json` in
this folder is what tells F5 to start an Extension Development Host, so open
this folder itself, not the repo root. In the new window that appears, open
your game's folder and open any `.dm` file; the server activates on the first
one.

The server picks the first `.dme` in the workspace root. If your project has
several, or your build passes `-D` flags, set them in the host window's
settings — analysing without your build's defines analyses a different
program:

```json
{
  "dm.environmentFile": "tgstation.dme",
  "dm.defines": ["CBT"]
}
```

## The object tree panel

**Explorer → DM Object Tree.** The project's type hierarchy, browsed lazily — each
expand fetches one level, because `/obj` alone can carry thousands of subtypes on
a real game. Every row shows its var and proc counts, and its tooltip names the
type it inherits from, which is not the same as its path parent: `/mob` is a child
of the root by path and inherits from `/atom/movable`.

Clicking a type opens its declaration. Builtins are deliberately not clickable —
nothing declares them, so there is nowhere to go.

The tree is built lazily by the server, so the panel is empty until something has
asked a question that needs it. It refreshes on save and from the toolbar button.

## Icon states

**DM: Browse Icon States** lists every state in a `.dmi` — the active file if one is open,
otherwise a file picker — with its direction and frame counts, and copies the name you pick.

Two things the list shows that a naive reader would get wrong. An empty name is the **default
state** and is completely ordinary, so it appears as `(default)`; and one name can appear **twice**,
once marked `movement`, because DM picks between the still and moving variants at runtime. A
dictionary keyed by name silently drops half of those.

A zero-byte `.dmi`, or a plain PNG saved under that extension, is reported as not an icon rather
than as an empty list. Both exist in shipped games.

## Which `.dme` is being analysed

The status bar shows it, right-hand side. Click it to pick another — the picker
lists every `.dme` in the workspace. This matters more than it looks: which `.dme`
is analysed decides every answer the server gives, and until now it was invisible
unless something resolved wrongly.

Changing it writes `dm.environmentFile` to workspace settings. The `.dme` is read
once at startup, so reload the window to apply it.

## What works

Everything the C ABI ships: squiggles (syntax and semantic, the same
diagnostics `dmc diagdiff` measures against dm.exe), completion with the
`.`/`:` distinction, hover with signatures and `///` docs, signature help
on `(` and `,`, go-to-definition returning every declaration, the outline,
and workspace symbol search.

Also: go-to-type-definition (`var/mob/test/M` → `/mob/test`, following only a
**written** type, never an inferred one), go-to-implementation (what overrides
this proc), folding ranges, clickable `#include` targets, and inlay hints showing
the inferred type of untyped locals.

Colour swatches sit beside every `"#rrggbb"` literal and `rgb()` call, and the
picker writes back in the form that is already there — pick a shade next to an
`rgb()` call and you get an `rgb()` call, not a hex string. A named colour like
`"red"` gets no swatch yet, and neither does an `rgb()` carrying a `space`
argument, because those are a different colour space and a swatch that guesses
is worse than none.

Workspace symbol search takes kind filters in DM's own spelling — `var/hp`,
`proc/heal`, `verb/say`, and `#` for a type. A bare `var/` lists every variable.

Syntax colouring is classifier-grade: the server publishes semantic tokens
— the same classification the C ABI ships, type and proc and macro names
included — over a small TextMate base that covers the instant before the
first response arrives. If colours look flat, check your theme has semantic
highlighting on (`"editor.semanticHighlighting.enabled": true`).

## Compile from VS Code

**Ctrl+Shift+B** runs `dm: compile <your>.dme`. The compiler's output lands in
the terminal, and every `file:line:error:` and `warning (name):` line becomes a
clickable entry in the Problems panel — the `$dm` problem matcher understands
all three of dm.exe's output shapes, including pragma-promoted warnings.

The task compiles with the same `dm.defines` the analysis uses, so the program
you build is the program you were reading. `dm.compilerPath` points at dm.exe
if yours is not in the default BYOND install location.

Our own squiggles and the compiler's entries sit side by side in Problems:
`source: dm` is us, live per keystroke; `source: dm.exe` is the real build.
If the two ever disagree, that is a bug worth reporting — the whole M11
harness exists to keep them identical.

**Terminal → Run Task → `dm: compile and run`** is DreamMaker's Run: compile,
and on a clean compile launch the `.dmb` in Dream Seeker, detached, so the
task finishes while the game runs. A failed compile stops at the Problems
panel and never launches. `dm.seekerPath` points at dreamseeker.exe if yours
moved.

Task labels are fixed — `dm: compile`, `dm: compile and run` — so a
keybinding works in every project regardless of the `.dme` name:

```json
{ "key": "ctrl+k", "command": "workbench.action.tasks.runTask",
  "args": "dm: compile", "when": "editorLangId == dm" },
{ "key": "ctrl+r", "command": "workbench.action.tasks.runTask",
  "args": "dm: compile and run", "when": "editorLangId == dm" }
```

The DreamMaker keys, with a cost stated plainly: while a DM file is focused,
a bare `ctrl+k` binding shadows VS Code's `Ctrl+K` chords (comment, zen mode,
the shortcuts editor). Everywhere else they keep working.

## When something looks wrong

`dmc` is the arbiter, and it uses the same code path:

```
dotnet run --project src/Dm.Cli -- hover <dme> <file> <line> <col>
```

CLI positions are 1-based; LSP's are 0-based. If the CLI agrees with the
editor, the bug is ours — report it with the smallest `.dm` that shows it.
