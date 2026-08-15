"""Diff dm.exe -o's RESOLVED object tree against dmc tree's.

`-o` prints what the compiler actually built, after every file has been merged
and every override resolved. That makes it the oracle for the object tree the
way -code_tree is the oracle for the parser: it answers "did this become a var
on this type", which no amount of clean compiling can.

Committed rather than rebuilt per session, which is the -code_tree lesson: this
one has four traps and each produces a confident wrong answer.

  1. `<verb>` IS ITS OWN ELEMENT. `dmc tree --procs` lists verbs, so counting
     only <proc> reports every verb as invented - exactly 138 phantom extras on
     mlaas, a plausible-looking gap that was entirely the harness.
  2. TYPE ELEMENTS NEST. An owner is built from the enclosing mob/obj/turf/
     area/object elements; val/list/item are value detail and never owners.
  3. THE DUMP OMITS WHOLE BUILTIN BRANCHES - anything not under the atom tree
     or the root. A /icon, /datum, /client or /particles subtype is absent even
     when it declares new members, so ours-only entries there are US BEING
     RIGHT. They are reported separately and never counted as differences;
     madridspy's `/icon ChangeOpacity` is the standing example.
  4. A DEPTH IS A TAB COUNT, not a tag match. A multiline string in a <val>
     spills raw newlines into the dump, so content lines are told from element
     lines by the `file="..."` marker - the same structural rule the
     -code_tree harness needed, learned there first.
  5. AN ASSOCIATIVE LIST'S ENTRIES ARE <var> ELEMENTS INSIDE THE <val>, marker
     and all. `var/tip_rarity_colors = list(1 = "#00FFFF", ...)` renders a
     <var>1</var> under its own value, so a reader that skips only the <val>
     TAG and not its CONTENTS invents one declaration per key - and hangs them
     on whatever type element it last saw, which is not even their own. Three
     phantom vars on mlaas, under a type twenty lines away. Everything inside a
     <val> is value detail; the fix is to skip the subtree, not the tag.

RUN IT AGAINST A KNOWN-EXACT PROJECT BEFORE TRUSTING IT ON A NEW ONE. mlaas is
documented exact both ways - 1153/1153 procs and 1493/1493 vars - and getting
those numbers back is the signal that the extractor works. That control is the
whole reason trap 1 was ever found.

`-o` still writes a .dmb, so this is a build either way.

Usage: python compare_object_tree.py <dme> [-DNAME ...]
"""

import os
import pathlib
import re
import subprocess
import sys

DM = os.environ.get("DM_BYOND_BIN", r"C:\Program Files (x86)\BYOND\bin")
DM = str(pathlib.Path(DM) / "dm.exe")
DMC = ["dotnet", "run", "--project",
       str(pathlib.Path(__file__).resolve().parents[3] / "src" / "Dm.Cli"), "--no-build", "--"]

# The `file="..."` marker is what separates an element from a line of spilled
# string content. Trap 4.
ELEMENT = re.compile(
    r"^(?P<tabs>\t*)<(?P<tag>mob|obj|turf|area|object|var|proc|verb)"
    r"\s+file=\"[^\"]*\">(?P<name>[^<]*)")

VALUE = re.compile(r"^\t*<val(\s|>)")

TYPES = ("mob", "obj", "turf", "area", "object")
MEMBERS = {"var": "vars", "proc": "procs", "verb": "procs"}

# The roots -o actually emits. Anything else is trap 3: absent from the dump
# however much it declares.
DUMPED_ROOTS = ("/mob", "/obj", "/turf", "/area")


def dump_side(dme, defines):
    """{'procs': {(owner, name)}, 'vars': {(owner, name)}} from dm.exe -o."""
    result = subprocess.run(
        [DM, "-o", *defines, str(dme.name)],
        capture_output=True, text=True, cwd=str(dme.parent), timeout=1800)

    found = {"procs": set(), "vars": set()}
    stack = []  # owner segments, indexed by depth
    value_depth = None  # inside a <val> subtree: everything there is a VALUE

    for raw in (result.stdout or "").splitlines():
        line = raw.rstrip("\n")
        depth = len(line) - len(line.lstrip("\t"))

        # Trap 5. Leave the subtree first, so a <val> closing at this depth
        # does not swallow the sibling declaration that follows it.
        if value_depth is not None and depth <= value_depth:
            value_depth = None

        opens_value = VALUE.match(line)

        if opens_value is not None and "</val>" not in line:
            value_depth = depth
            continue

        if value_depth is not None:
            continue

        match = ELEMENT.match(line)

        if match is None:
            continue
        tag = match.group("tag")
        name = match.group("name").strip()

        if not name:
            continue

        # An element at depth d sits at stack index d-1, so a MEMBER there is
        # owned by everything above it - stack[:d-1], not stack[:d]. The first
        # version used stack[:d] and hung every global proc on /mob, which the
        # services fixture caught in one run.
        if tag in TYPES:
            del stack[depth - 1:]
            stack.append(name)
            continue

        above = stack[:depth - 1]
        owner = "/" + "/".join(above) if above else "/"
        found[MEMBERS[tag]].add((owner, name))

    return found


def our_side(dme, defines):
    """The same pairs from dmc tree, which lists verbs among --procs."""
    found = {}

    for kind, flag in (("procs", "--procs"), ("vars", "--vars")):
        result = subprocess.run(
            [*DMC, "tree", str(dme), "--no-builtins", flag, *defines],
            capture_output=True, text=True, timeout=1800)

        pairs = set()

        for raw in (result.stdout or "").splitlines():
            parts = raw.rstrip().split(" ", 1)

            if len(parts) == 2 and parts[0].startswith("/"):
                pairs.add((parts[0], parts[1].strip()))

        found[kind] = pairs

    return found


def blind_spot(owner):
    """True when -o would never have printed this owner at all. Trap 3."""
    return owner != "/" and not owner.startswith(DUMPED_ROOTS)


def main():
    dme = pathlib.Path(sys.argv[1]).resolve()
    defines = [a for a in sys.argv[2:] if a.startswith("-D")]

    dump = dump_side(dme, defines)
    ours = our_side(dme, defines)

    failed = False

    for kind in ("procs", "vars"):
        missing = sorted(dump[kind] - ours[kind])
        extra = sorted(ours[kind] - dump[kind])
        hidden = [p for p in extra if blind_spot(p[0])]
        real = [p for p in extra if not blind_spot(p[0])]

        print(f"{kind:6} dump {len(dump[kind]):6}  ours {len(ours[kind]):6}  "
              f"missing {len(missing):5}  extra {len(real):5}  "
              f"(+{len(hidden)} in the dump's blind spot, ours being right)")

        failed = failed or bool(missing) or bool(real)

        for label, rows in (("MISSING (dump has, we do not)", missing),
                            ("EXTRA (we have, dump does not)", real)):
            if rows:
                print(f"\n  {label}, first 30 of {len(rows)}:")
                for owner, name in rows[:30]:
                    print(f"    {owner} {name}")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
