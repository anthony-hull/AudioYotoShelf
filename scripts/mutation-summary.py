"""Summarise a Stryker mutation-report.json: score per file (worst first) and the surviving mutants.

Stryker's own console table wraps into unreadable columns, so read the JSON report instead:

    python3 scripts/mutation-summary.py                      # newest StrykerOutput report
    python3 scripts/mutation-summary.py path/to/mutation-report.json --survivors
"""
import json
import pathlib
import sys
from collections import Counter, defaultdict

DETECTED = {"Killed", "Timeout"}
UNDETECTED = {"Survived", "NoCoverage"}


def mutation_score(counts):
    detected = sum(counts[status] for status in DETECTED)
    total = detected + sum(counts[status] for status in UNDETECTED)
    return 100.0 * detected / total if total else None


def newest_report():
    reports = sorted(pathlib.Path("StrykerOutput").glob("*/reports/mutation-report.json"))
    if not reports:
        sys.exit("No StrykerOutput/*/reports/mutation-report.json found. Run `dotnet stryker` first.")
    return reports[-1]


def main(path, show_survivors):
    report = json.load(open(path))
    per_file = defaultdict(Counter)
    survivors = []
    for file_path, data in report["files"].items():
        short = file_path.split("/src/")[-1]
        for mutant in data["mutants"]:
            per_file[short][mutant["status"]] += 1
            if mutant["status"] in UNDETECTED:
                survivors.append((short, mutant["location"]["start"]["line"], mutant["status"],
                                  mutant["mutatorName"], mutant.get("replacement", "")))

    total = Counter()
    rows = []
    for name, counts in per_file.items():
        total.update(counts)
        score = mutation_score(counts)
        if score is not None:
            rows.append((score, name, counts))

    print(f"{'score':>6}  {'kill':>4} {'tmo':>3} {'surv':>4} {'nocov':>5}  file")
    for score, name, c in sorted(rows):
        print(f"{score:6.1f}  {c['Killed']:4d} {c['Timeout']:3d} {c['Survived']:4d} {c['NoCoverage']:5d}  {name}")
    print(f"\nTOTAL {mutation_score(total):.2f}%  " + "  ".join(f"{k}={v}" for k, v in sorted(total.items())))

    if show_survivors:
        print("\nUNDETECTED MUTANTS (Survived = a test ran and passed; NoCoverage = no test reached it)")
        for name, line, status, mutator, replacement in sorted(survivors):
            print(f"  {status:10} {name}:{line}  {mutator}  -> {replacement[:70]!r}")


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    main(args[0] if args else newest_report(), "--survivors" in sys.argv)
