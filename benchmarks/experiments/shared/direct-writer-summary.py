"""Validate and summarize paired processes; retain every block and never pool run medians."""

import csv
import json
from pathlib import Path
import statistics
import sys

results = Path(__file__).resolve().parents[1] / "codec-design-results"
confirmation = sys.argv[1:] == ["--confirmation"]
assert not sys.argv[1:] or confirmation
prefix = "direct-writer-confirm" if confirmation else "direct-writer"
case_count = 4 if confirmation else 22
writer = csv.writer(sys.stdout, lineterminator="\n")
writer.writerow([
    "Process", "Case", "DirectUs", "DtoFreshUs", "SavedUs", "DirectReductionPercent",
    "DirectBytes", "DtoFreshBytes", "InputBytes", "OutputBytes", "InputEntries", "OutputEntries",
    "DirectMinUs", "DirectMaxUs", "DtoMinUs", "DtoMaxUs",
])
for process in (1, 2):
    rows = [json.loads(line) for line in
            (results / f"{prefix}-run{process}.jsonl").read_text().splitlines()]
    assert rows[0]["Environment"] == "direct-writer"
    assert len(rows) == 1 + 2 * case_count
    cases = {}
    for row in rows[1:]:
        samples = row["Samples"]
        assert len(samples) == 8
        assert all(sample["Operations"] > 0 and sample["Bytes"] >= 0 for sample in samples)
        assert statistics.median(sample["Us"] for sample in samples) == row["MedianBlockUs"]
        assert sum(sample["Bytes"] for sample in samples) / sum(
            sample["Operations"] for sample in samples) == row["BytesPerOperation"]
        assert row["Method"] not in cases.setdefault(row["Case"], {})
        cases[row["Case"]][row["Method"]] = row
    assert len(cases) == case_count
    for case, methods in cases.items():
        direct, dto = methods["DirectReadWrite"], methods["DtoFreshReadWrite"]
        for key in ("InputBytes", "OutputBytes", "InputEntries", "OutputEntries"):
            assert direct[key] == dto[key]
        d, t = direct["MedianBlockUs"], dto["MedianBlockUs"]
        writer.writerow([
            process, case, d, t, t - d, 100 * (t - d) / t,
            direct["BytesPerOperation"], dto["BytesPerOperation"],
            *[direct[key] for key in ("InputBytes", "OutputBytes", "InputEntries", "OutputEntries")],
            direct["MinBlockUs"], direct["MaxBlockUs"], dto["MinBlockUs"], dto["MaxBlockUs"],
        ])
