"""Summarize both native DTO processes without discarding blocks or pooling their medians."""

import csv
import json
from pathlib import Path
import sys

results = Path(__file__).resolve().parents[1] / "codec-design-results"
writer = csv.writer(sys.stdout)
writer.writerow(["Process", "Case", "Method", "MedianBlockUs", "MinBlockUs", "MaxBlockUs",
                 "BytesPerOperation", "Gen0", "Gen1", "Gen2"])
for process in (1, 2):
    for line in (results / f"native-dto-run{process}.jsonl").read_text().splitlines():
        row = json.loads(line)
        if "Case" in row:
            writer.writerow([process] + [row[key] for key in (
                "Case", "Method", "MedianBlockUs", "MinBlockUs", "MaxBlockUs",
                "BytesPerOperation", "Gen0", "Gen1", "Gen2")])
