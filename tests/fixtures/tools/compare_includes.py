"""Diff dm.exe -l's file list against dmc includes, as a SET and as a SEQUENCE.

Include order decides override resolution, so the same files in a different
order are a different program. Set equality is the weaker half and it is the
half a `sort | comm` gives you - which is why this compares positions too, and
why it perturbs one side first to prove the comparison can see a reordering at
all. A diff that cannot fail proves nothing by passing.

Committed rather than rebuilt per session. All three of the traps below fired
on the first attempt the day this diff was first run, and it reported "4
missing" with a straight face - four misses that were two files, in a graph
already documented as exact. The shape of a wrong answer is the tell.

  1. -l LISTS THE .dmf AND .dmm TWICE, once relative and once absolute, so
     `sort -u` keeps both spellings and reports misses that are one file each.
  2. dmc includes ANNOTATES those same entries - `  [interface]`, `  [map]` -
     so an extension-anchored regex drops them from OUR side instead, and the
     two counts then disagree in opposite directions while both look plausible.
  3. -l PUTS THEM AT THE END of its block while we list them where they are
     #included. That is reporting, not order: dm.exe loads both BEFORE any
     source - its own first lines say so - and neither declares anything.

  4. AN ERRORING BUILD EMITS NO `Source Files:` BLOCK AT ALL, so -l has
     nothing to say about a project that does not compile - warklan, whose one
     error is the numeric assoc key 516.1686 introduced and which we agree
     with. That is reported as NOT COMPARABLE and exits 2, because a harness
     that measured nothing and exited 0 is the shape this project keeps
     getting burned by: -code_tree hit the same wall and excluded madridspy.

  And one that is not a trap so much as a surprise: -l lists every file the
  build TOUCHES, resources included. On mlaas that is 234 entries against an
  include graph of 102. Filter to .dm/.dme before comparing anything.

Both are filtered to DM source here, which resolves 1-3 together: the pair that
is double-listed, annotated and relocated is exactly the pair that is not DM.

`-l` still writes a .dmb, so this is a build with a report rather than a dry run.

Usage: python compare_includes.py <dme> [-DNAME ...]
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

SOURCE = (".dm", ".dme")


def normalise(path):
    """One spelling for a path: forward slashes, lowercased, relative-ish."""
    return path.replace("\\", "/").strip().lower()


def dump_side(dme, defines):
    result = subprocess.run(
        [DM, "-l", *defines, str(dme.name)],
        capture_output=True, encoding="utf-8", errors="replace", cwd=str(dme.parent), timeout=1800)

    files, collecting, saw_block = [], False, False

    for raw in (result.stdout or "").splitlines():
        line = raw.strip()

        if line.startswith("Source Files:"):
            collecting = True
            saw_block = True
            continue

        if not collecting or not line:
            continue

        # The block ends at the compiler's own summary line.
        if line.endswith(("errors", "warnings")) or " - " in line and ".dmb" in line:
            break

        if normalise(line).endswith(SOURCE):
            files.append(normalise(line))

    return files, saw_block


def our_side(dme, defines):
    result = subprocess.run(
        [*DMC, "includes", str(dme), *defines],
        capture_output=True, encoding="utf-8", errors="replace", timeout=1800)

    files = []

    for raw in (result.stdout or "").splitlines():
        # Trap 2: strip the annotation before looking at the extension.
        line = re.sub(r"\s*\[[a-z]+\]\s*$", "", raw.rstrip())
        line = line.strip()

        if not line or line.startswith(("=", "-")) or "file(s)" in line:
            continue

        if normalise(line).endswith(SOURCE):
            files.append(normalise(line))

    return files


def compare(theirs, ours, label):
    """True when the two sequences agree. Prints the first disagreements."""
    if theirs == ours:
        print(f"  {label}: identical, {len(theirs)} files position for position")
        return True

    missing = [f for f in theirs if f not in ours]
    extra = [f for f in ours if f not in theirs]

    print(f"  {label}: DIFFER - theirs {len(theirs)}, ours {len(ours)}, "
          f"{len(missing)} missing, {len(extra)} extra")

    for name, rows in (("missing", missing), ("extra", extra)):
        for path in rows[:10]:
            print(f"    {name}: {path}")

    if not missing and not extra:
        for i, (a, b) in enumerate(zip(theirs, ours)):
            if a != b:
                print(f"    same set, ORDER differs first at {i}: "
                      f"theirs {a}, ours {b}")
                break

    return False


def main():
    dme = pathlib.Path(sys.argv[1]).resolve()
    defines = [a for a in sys.argv[2:] if a.startswith("-D")]

    theirs, saw_block = dump_side(dme, defines)
    ours = our_side(dme, defines)

    if not saw_block:
        print("NOT COMPARABLE: dm.exe printed no `Source Files:` block, which is what an "
              "ERRORING build does.\n  Fix the project's errors, or measure it with "
              "`dmc includes` alone - -l has nothing to say here. (Trap 4.)")
        return 2

    print(f"dm.exe -l     {len(theirs)} DM source files")
    print(f"dmc includes  {len(ours)} DM source files")

    ok = compare(theirs, ours, "sequence")

    # The control. Swapping two entries must be visible, or a pass above says
    # nothing about order - it would only have proven set equality.
    if len(theirs) > 1:
        swapped = list(theirs)
        swapped[0], swapped[1] = swapped[1], swapped[0]

        if compare(swapped, ours, "control (two lines swapped)"):
            print("  CONTROL FAILED: the comparison cannot see a reordering, "
                  "so its pass above proves nothing about order")
            return 1

    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
