"""Run the GC comparison, or summarize a saved twelve-launch result."""
import argparse
import json
import os
from pathlib import Path
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent
ARTIFACTS = ROOT.parents[1] / "artifacts" / "gc-replay"


def replay():
    ARTIFACTS.mkdir(parents=True, exist_ok=True)
    run_dir = Path(tempfile.mkdtemp(prefix="run-", dir=ARTIFACTS))
    publish = run_dir / "publish"
    subprocess.run(
        ["dotnet", "publish", "gc-replay.csproj", "-c", "Release",
         "--self-contained", "false", "-o", str(publish)],
        cwd=ROOT, check=True,
    )
    rows = []
    for pair in range(1, 7):
        for mode in ((0, 1) if pair % 2 else (1, 0)):
            env = dict(os.environ, DOTNET_PROCESSOR_COUNT="4", DOTNET_gcServer=str(mode))
            output = subprocess.check_output(
                ["dotnet", str(publish / "gc-replay.dll"), "3", "2"],
                env=env, cwd=ROOT, text=True,
            )
            row = json.loads(output)
            if row["serverGC"] != bool(mode) or row["processorCount"] != 4:
                raise ValueError(f"GC or processor override did not take effect in pair {pair}")
            rows.append(dict(row, pair=pair))
    result = run_dir / "decision.json"
    result.write_text(json.dumps(rows, indent=2), encoding="utf-8")
    print(f"Saved {result}")
    return rows


def summarize(rows):
    pairs = {(r["pair"], r["serverGC"]): r for r in rows}
    expected = {(p, m) for p in range(1, 7) for m in (False, True)}
    if len(rows) != 12 or pairs.keys() != expected:
        raise ValueError("Expected six complete workstation/server pairs")
    if any(r["failures"] or r["processorCount"] != 4 or r["batches"] <= 0 for r in rows):
        raise ValueError("Invalid workload, processor count or checksum")
    if len({r["runtime"] for r in rows}) != 1:
        raise ValueError("Paired launches require the same runtime")
    changes = {}
    for metric in ("batchesPerSecond", "p99BatchMs", "peakWorkingSet", "allocatedBytesPerBatch"):
        changes[metric] = [
            pairs[p, True][metric] / pairs[p, False][metric] - 1 for p in range(1, 7)
        ]
        print(f"{metric:24} {statistics.median(changes[metric]):+.2%} median paired change")
    wins = sum(x > 0.05 for x in changes["batchesPerSecond"])
    passed = (
        wins == 6
        and statistics.median(changes["p99BatchMs"]) <= 0.05
        and statistics.median(changes["peakWorkingSet"]) <= 0.10
        and statistics.median(changes["allocatedBytesPerBatch"]) <= 0.10
    )
    print(f"{wins}/6 pairs cleared +5% throughput; {sum(r['batches'] for r in rows):,} batches")
    print("Adopt server GC for this replay." if passed else "Retain workstation GC for this replay.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summarize", type=Path, metavar="DECISION_JSON")
    args = parser.parse_args()
    summarize(json.loads(args.summarize.read_text(encoding="utf-8-sig")) if args.summarize else replay())
