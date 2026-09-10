#!/usr/bin/env python3
"""Validate and summarize the bounded generated-eager experiment, without pooling processes."""
import csv
import hashlib
import json
import math
from pathlib import Path
import statistics
import sys

root = Path(__file__).resolve().parents[1]
if sys.argv[1:] == ["--source-footprint"]:
    generated = root.parent / "FixSourceGenerator.Benchmarks/obj/eager-generated"
    files = sorted(generated.glob("EagerProjection.Generator/EagerProjection.Experiment.EagerProjectionGenerator/*.cs"))
    assert len(files) == 3, "Build with EmitCompilerGeneratedFiles before inspecting actual output"
    records = [{
        "HintName": file.name, "Utf8Bytes": file.stat().st_size,
        "Lines": len(file.read_text().splitlines()),
        "Sha256": hashlib.sha256(file.read_bytes()).hexdigest(),
    } for file in files]
    print(json.dumps({
        "Scope": "Actual emitted source; attributes/shared strict tokenizer once plus two projections",
        "Not": "Native code size, resident instance size, allocation, or complete application size",
        "Files": records, "TotalUtf8Bytes": sum(row["Utf8Bytes"] for row in records),
    }, indent=2))
    sys.exit(0)

assert sys.argv[1:] in ([], ["--confirmation"])
confirmation = bool(sys.argv[1:])
pattern = "eager-generated-confirm-run*.jsonl" if confirmation else "eager-generated-run*.jsonl"
files = sorted((root / "codec-design-results").glob(pattern))
assert len(files) == (2 if confirmation else 1)
writer = csv.writer(sys.stdout, lineterminator="\n")
writer.writerow(["process", "case", "method", "median_block_us", "min_block_us", "max_block_us",
                 "baseline", "reduction_percent", "bytes_per_operation", "gen0", "gen1", "gen2"])
previous_keys = None
for file in files:
    data = [json.loads(line) for line in file.read_text().splitlines()]
    environment, rows = data[0], data[1:]
    assert environment["Confirmation"] == confirmation and environment["Rounds"] == 8
    assert environment["BlockSeconds"] == (1 if confirmation else .1)
    assert len(rows) == (18 if confirmation else 153)
    by_key = {(row["Case"], row["Method"]): row for row in rows}
    assert len(by_key) == len(rows)
    assert len({row["Case"] for row in rows}) == environment["Cases"] == (7 if confirmation else 63)
    if previous_keys is not None:
        assert previous_keys == set(by_key)
    previous_keys = set(by_key)
    for row in rows:
        samples = row["Samples"]
        assert len(samples) == 8
        assert all(s["Operations"] > 0 and s["Operations"] % 32 == 0 and s["Bytes"] >= 0 and s["Us"] > 0
                   for s in samples)
        times = [s["Us"] for s in samples]
        for name, value in [("MedianBlockUs", statistics.median(times)), ("MinBlockUs", min(times)),
                            ("MaxBlockUs", max(times)),
                            ("BytesPerOperation", sum(s["Bytes"] for s in samples) /
                             sum(s["Operations"] for s in samples))]:
            assert math.isclose(row[name], value, rel_tol=1e-12, abs_tol=1e-12)
        for generation in ("Gen0", "Gen1", "Gen2"):
            assert row[generation] == sum(s[generation] for s in samples)
        baseline = "PriceCached" if "/ReadPrice/" in row["Case"] else (
            "FullCached" if "/ReadAll/" in row["Case"] else "DirectReadWrite")
        reference = by_key[row["Case"], baseline]
        for dimension in ("InputBytes", "OutputBytes", "InputEntries", "OutputEntries"):
            assert row[dimension] == reference[dimension]
        reduction = 100 * (1 - row["MedianBlockUs"] / reference["MedianBlockUs"])
        writer.writerow([file.stem, row["Case"], row["Method"],
                         *(f"{row[key]:.6f}" for key in ("MedianBlockUs", "MinBlockUs", "MaxBlockUs")),
                         baseline, f"{reduction:.3f}", row["BytesPerOperation"],
                         row["Gen0"], row["Gen1"], row["Gen2"]])
