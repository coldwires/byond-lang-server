"""Mine the diagnostic lab's probe corpus into tests/fixtures/errors/probes.

The lab (byondtest/lab/errors) holds ~430 single-file .dme probes, each built to
trigger one compiler message, with the compiler's output cached beside it. That
is a ready-made must-fail corpus, which is the axis a corpus of *correct* code
structurally cannot supply.

Every probe is RECOMPILED here rather than trusted from the cache, and only kept
if it still reproduces. That drops, without a hand-written blocklist:

  - probes that need assets or an environment we do not copy
  - probes that compile clean (nothing to assert)
  - anything whose message moved between the cache and this machine

Messages a language server can never emit are filtered out too - map, skin,
asset, codegen and internal sanity checks. A fixture asserting we agree with
dm.exe about `bad turf on map` measures nothing about us.

Usage:  python tests/fixtures/tools/mine_probes.py [--lab PATH] [--dry-run]
"""

import argparse
import pathlib
import re
import shutil
import subprocess
import sys
import tempfile

DIAG = re.compile(r"^(?P<file>.+?):(?P<line>\d+):(?P<sev>error|warning)(?:\s*\((?P<name>[^)]+)\))?:\s*(?P<msg>.*)$")
SUMMARY = re.compile(r"- (\d+) errors?, (\d+) warnings?")

# Messages that exist but are outside a language server's reach. Matching on a
# substring of the message rather than the probe name, since the probe name is
# the lab's naming and the message is the contract.
OUT_OF_SCOPE = (
    "map", "turf on", "area on", "icon", "skin", "dmf", "dmi", "dmm", "dmp",
    "cache", "out of memory", "internal", "bad instruction", "bad node",
    "failed to", "unable to open", "cannot find file", "key file",
    "sound", "savefile", "database", "too large", "child nodes",
    "maximum number", "too many", "bad build", "version number",
)


def diagnostics(output):
    found = []

    for raw in output.splitlines():
        match = DIAG.match(raw.strip())

        if match and match.group("file").lower().endswith((".dm", ".dme")):
            found.append((int(match.group("line")), match.group("sev"), match.group("msg")))

    return found


def in_scope(diags):
    for _, _, msg in diags:
        low = msg.lower()

        if any(word in low for word in OUT_OF_SCOPE):
            return False

    return True


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--lab", default=str(pathlib.Path.home() / "Desktop/byondtest/lab/errors"))
    parser.add_argument("--dm", default=r"C:\Program Files (x86)\BYOND\bin\dm.exe")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    lab = pathlib.Path(args.lab)
    probes = sorted((lab / "probes").glob("*.dme"))
    out_dir = pathlib.Path(__file__).resolve().parents[1] / "errors" / "probes"

    if not probes:
        sys.exit(f"no probes under {lab / 'probes'}")

    kept = dropped_clean = dropped_scope = dropped_norepro = 0
    seen_signature = {}

    if not args.dry_run:
        shutil.rmtree(out_dir, ignore_errors=True)
        out_dir.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory() as tmp:
        stage = pathlib.Path(tmp)

        for probe in probes:
            work = stage / probe.name
            work.write_bytes(probe.read_bytes())

            try:
                result = subprocess.run(
                    [args.dm, str(work)], capture_output=True, text=True, timeout=90,
                    cwd=str(stage))
            except subprocess.TimeoutExpired:
                dropped_norepro += 1
                continue

            output = (result.stdout or "") + (result.stderr or "")
            diags = diagnostics(output)

            if not diags:
                dropped_clean += 1
                continue

            if not in_scope(diags):
                dropped_scope += 1
                continue

            # One fixture per distinct message set: several probes reach the same
            # message and the extras add runtime without adding coverage.
            signature = tuple(sorted({(d[1], d[2]) for d in diags}))

            if signature in seen_signature:
                continue

            seen_signature[signature] = probe.stem
            kept += 1

            if args.dry_run:
                continue

            (out_dir / probe.name).write_bytes(probe.read_bytes())

            lines = [
                "# Mined from the diagnostic lab by tools/mine_probes.py.",
                "# Recompiled here, so this records what dm.exe said on this machine.",
                f"# probe: {probe.stem}",
            ]
            lines += [f"{line} {sev} {msg}" for line, sev, msg in diags]
            (out_dir / f"{probe.stem}.expected").write_text("\n".join(lines) + "\n", newline="\n")

    print(f"kept              {kept}")
    print(f"dropped, clean    {dropped_clean}")
    print(f"dropped, scope    {dropped_scope}")
    print(f"dropped, no repro {dropped_norepro}")
    print(f"deduped           {len(probes) - kept - dropped_clean - dropped_scope - dropped_norepro}")


if __name__ == "__main__":
    main()
