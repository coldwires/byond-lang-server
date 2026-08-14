"""Diff dm.exe -code_tree's declaration NESTING against dmc outline's.

The dump is ONE MERGED TREE for the whole project: a reopened type is a single
node carrying its first declaration's line, and a reopened BUILTIN carries no
marker at all. So positions are not comparable and the unit is the NAME CHAIN -
`mob/pc/var/clothing/feet` - which is exactly the syntactic nesting `-o`
resolves away and therefore cannot check.

Rules, each earned on a probe or a first run rather than assumed:
  - marker-less dump nodes are stddef/builtin; marker line 0 is compiler-
    injected (parent_type assignments, `..` markers).
  - dump-only is judged against MARKED chains; ours-only is judged against ALL
    dump chains, so a project reopening `world` matches the unmarked builtin
    node it was merged into.
  - a proc's subtree is its body: pruned under `proc`/`verb` groups, and under
    OVERRIDES via the outline's own proc set, since an override has no group
    node in either tree.
  - a bare assignment override renders as `= / (name) / (value)`, the same
    shape as the injected parent_type assignments - lifted into a chain when
    its marker line is nonzero.
  - chains ending in a bare group keyword (var/proc/verb) are containers, not
    declarations, and merge unpredictably; dropped on both sides.

Usage: python compare.py <dme> [-DNAME ...]
"""

import pathlib
import re
import subprocess
import sys

import os

DM = os.environ.get("DM_BYOND_BIN", r"C:\Program Files (x86)\BYOND\bin")
DM = str(pathlib.Path(DM) / "dm.exe")
DMC = ["dotnet", "run", "--project",
       str(pathlib.Path(__file__).resolve().parents[3] / "src" / "Dm.Cli"), "--no-build", "--"]

NODE = re.compile(r"^(?P<tabs>\t*)(?P<text>.*?)(?:\s\[(?P<file>[^\]:]+):(?P<line>\d+)\])?$")
# `!` is a legal type-name segment - warklan's /obj/! quest marker, compiler-
# verified 2026-08-12 - and the dump renders it as a bare node like any name.
NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$|^!$")
TARGET = re.compile(r"^\((?P<name>[A-Za-z_][A-Za-z0-9_]*)\)$")
CLOSES = re.compile(r"\"\)\s\[[^\]]+:\d+\]\s*$")

# `%6d` then two spaces, then two spaces PER LEVEL - a greedy \s+ ate the
# indentation on the first attempt and flattened everything.
OUTLINE = re.compile(r"^ *(?P<line>\d+)  (?P<indent>(?:  )*)(?P<kind>\w+) (?P<path>\S+)")

GROUPS = ("var", "proc", "verb")


def dump_side(dme, defines, proc_chains):
    result = subprocess.run(
        [DM, "-code_tree", *defines, str(dme.name)],
        capture_output=True, text=True, cwd=str(dme.parent), timeout=900)

    marked, unmarked = set(), set()
    stack = []  # (depth, name or None)
    pending_assign = None  # (depth, prefix)
    in_string = False

    for raw in (result.stdout or "").splitlines():
        # A multi-line string literal spills raw newlines into the dump; its
        # content lines parse as bogus nodes that poison ancestry (mlaas's
        # help text hid all 55 global procs behind one). Three rules were
        # burned through before this one held. Quote-BALANCE toggling ate the
        # dump (expression nodes carry their own quotes); begins-with-quote
        # exit missed terminators that end `...prose") [file]` and swallowed
        # warklan from its first HTML string to EOF. What discriminates is
        # STRUCTURAL: a string is open when a line has odd unescaped quotes
        # AND no closing `") [file:line]` at its end; content lines never
        # carry markers; the terminator is the line that ends `") [file:line]`.
        if in_string:
            if CLOSES.search(raw):
                in_string = False
            continue

        if raw.replace('\\"', "").count('"') % 2 == 1 and not CLOSES.search(raw):
            in_string = True
            continue

        match = NODE.match(raw)
        if not match:
            continue

        depth = len(match.group("tabs"))
        text = match.group("text").strip()
        has_marker = match.group("file") is not None
        line = int(match.group("line")) if has_marker else -1

        while stack and stack[-1][0] >= depth:
            stack.pop()

        # An EMPTY node is an initialiser's container: `hp / <empty> / = / (10)`.
        # It poisons rather than vanishes, or the `=` beneath it is
        # indistinguishable from a type-level bare assignment and the lifting
        # turns `(null)` initialisers into declarations named null.
        if not text:
            stack.append((depth, None))
            continue

        names = [n for _, n in stack]
        clean = all(n is not None for n in names)
        prefix = "/".join(names) if clean else None

        if pending_assign is not None:
            adepth, aprefix = pending_assign
            if depth == adepth + 1 and (m := TARGET.match(text)):
                marked.add(f"{aprefix}/{m.group('name')}" if aprefix else m.group("name"))
            pending_assign = None

        if clean and prefix is not None:
            # Body pruning: below a grouped proc, or below a chain the outline
            # says is a proc (overrides have no group node).
            parts = prefix.split("/") if prefix else []
            if (len(parts) >= 2 and parts[-2] in ("proc", "verb")) or prefix in proc_chains:
                stack.append((depth, None))
                continue

            if text == "=" and has_marker and line > 0:
                pending_assign = (depth, prefix)

        is_name = NAME.match(text) is not None
        keep = clean and is_name and (not has_marker or line > 0)

        # Initialiser renderings inside var context: `= null` appears as a bare
        # `null` NAME node under the var, and `= new /x(...)` as a `New` node
        # with the type's segments beneath it. Both are values, not
        # declarations, so they poison their subtree.
        if keep and prefix is not None and "var" in prefix.split("/") and text in ("null", "New"):
            keep = False

        stack.append((depth, text if keep else None))

        if keep:
            chain = f"{prefix}/{text}" if prefix else text
            (marked if has_marker else unmarked).add(chain)

    return marked, unmarked


SIBLING = re.compile(r"\(\+\d+ more\)")
SYMBOL_VAR = re.compile(r"^ *(?P<line>\d+) +variable (?P<name>\w+)")


def sibling_names(dme, file):
    """The outline collapses `a; b, c` runs to `a (+2 more)`; the ABI outline
    names every sibling, so it supplies the rest, keyed by line."""
    result = subprocess.run(
        [*DMC, "symbols", str(dme.parent / file)],
        capture_output=True, text=True, timeout=600)

    by_line = {}

    for raw in (result.stdout or "").splitlines():
        if match := SYMBOL_VAR.match(raw):
            by_line.setdefault(int(match.group("line")), []).append(match.group("name"))

    return by_line


def outline_side(dme, files):
    chains, procs = set(), set()

    for file in sorted(files):
        result = subprocess.run(
            [*DMC, "outline", str(dme.parent / file)],
            capture_output=True, text=True, timeout=600)

        text = result.stdout or ""
        siblings = sibling_names(dme, file) if SIBLING.search(text) else {}
        stack = []  # (depth, prefix-list)

        for raw in text.splitlines():
            match = OUTLINE.match(raw)
            if not match:
                continue

            line = int(match.group("line"))
            depth = len(match.group("indent")) // 2
            kind = match.group("kind")
            segments = [s for s in match.group("path").split("(")[0].split("/") if s]

            while stack and stack[-1][0] >= depth:
                stack.pop()

            prefix = stack[-1][1] if stack else []
            full = prefix + segments

            for i in range(len(segments)):
                chains.add("/".join(prefix + segments[: i + 1]))

            if SIBLING.search(raw):
                for name in siblings.get(line, []):
                    chains.add("/".join(prefix + segments[:-1] + [name]))

            if kind in ("proc", "verb"):
                procs.add("/".join(full))

            stack.append((depth, full))

    return chains, procs


def project_files(dme, defines):
    """From `dmc includes` rather than `dm.exe -l`: the two are verified
    identical (PLAN §9), and -l emits no Source Files block when the build
    errors - madridspy no longer compiles on 516.1686 at all."""
    result = subprocess.run(
        [*DMC, "includes", str(dme), *defines],
        capture_output=True, text=True, timeout=900)

    files = []

    for raw in (result.stdout or "").splitlines():
        name = raw.strip().split("  ")[0].strip()

        if name.lower().endswith((".dm", ".dme")):
            files.append(name)

    return files


def main():
    dme = pathlib.Path(sys.argv[1]).resolve()
    defines = [a for a in sys.argv[2:] if a.startswith("-D")]

    files = project_files(dme, defines)
    print(f"files         {len(files)}")

    ours, procs = outline_side(dme, files)
    marked, unmarked = dump_side(dme, defines, procs)

    def declish(chains):
        return {c for c in chains if c.split("/")[-1] not in GROUPS}

    ours, marked_d = declish(ours), declish(marked)
    everything = marked_d | declish(unmarked)

    only_dump = sorted(marked_d - ours)
    only_ours = sorted(ours - everything)

    print(f"dump chains   {len(marked_d)} marked (+{len(unmarked)} builtin/stddef)")
    print(f"our chains    {len(ours)}")
    print(f"only in dump  {len(only_dump)}")
    print(f"only in ours  {len(only_ours)}")

    for label, rows in (("DUMP ONLY", only_dump), ("OURS ONLY", only_ours)):
        if rows:
            print(f"\n{label} (first 30):")
            for chain in rows[:30]:
                print(f"  {chain}")


if __name__ == "__main__":
    main()
