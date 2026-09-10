# Reproducing the reader experiments

These are experimental patches and probes, not active production optimizations.
The recorded prework baseline (`a0b1aab`) keeps binary group membership and the original
temporal reader parsers. The temporal fast path has since been integrated by #30; apply
the archived temporal patch only to a worktree at that prework baseline, not on top of
the integrated implementation. Results, caveats and BenchmarkDotNet commands are recorded in
`../FixSourceGenerator.Benchmarks/README.md`.

Use separate worktrees from the same baseline commit for every variant. Do not apply a
prototype patch to a dirty implementation branch or combine variants when measuring an
individual effect. Preserve a clean baseline worktree for comparison.

## Codec design experiments (after integration, no production API change)

Baseline: merged `c0765c8`. The `experiments/codec-design` branch adds benchmark-only
prototypes, a synthetic nested dictionary and fixture checks. It does **not** change the
generator, public runtime or adopt an editable message API.

### Scope and controls

`DesignReadBenchmarks` consumes just price/quantity: Price/OrderQty for mini FIX44 Small,
MDEntryPx/MDEntrySize for real FIX50SP2 X/W at 1/10/50 entries. This is deliberately a narrow
projection, **not** the earlier full selected-field/temporal or combined encode-decode workload.
Each value is consumed once or four times (`Uses`). The methods are:

| Method | Location and conversion |
|--------|-------------------------|
| GeneratedLazy | Existing group enumeration + generated projection; repeat getter conversions |
| GeneratedCached | Same generated API, but parse once into local variables |
| FusedLazy | Experimental boundary scan records selected offsets; getters still parse repeatedly |
| FusedTyped | Same fused scan, then a temporary typed value struct preserving nullable presence |
| Visitor | Same scanner converts/accumulates selected values as encountered |

The typed row contains two nullable numbers, no owned strings, retained message graph or
wire mutation. The visitor is an internal immediate-consumption prototype, not a public callback
API: it still shares the scanner's presence/offset bookkeeping and has a different accumulation
shape. It does not establish the cost of an optimized generic visitor or delegate dispatch.

The fused scanner borrows generated membership and nested-skip metadata (reflection only during
setup). Market fixtures have no nested repeating groups. A separate synthetic FIX4.3 dictionary
checks two nested levels with the same delimiter and price tags, missing versus zero values,
duplicate first-occurrence selection, unknown boundaries and malformed nested counts. Market
checks also cover all four existing field-order permutations. **This is not a general FixView
replacement**: production projections skip arbitrary nested scopes, while the prototype only
supports the topology exercised by these fixtures and the supplied generated skipper.

`DesignEditBenchmarks` keeps an immutable original and produces an owned output for every call:

| Method | Work included |
|--------|---------------|
| ShiftEach | Copy original, apply every edit by replacing/moving the tail, update offsets |
| RebuildIndexed | Use original field index, coalesce last edits and copy unchanged ranges once |
| ScanAndShift | Reindex the original on each call, then ShiftEach |
| ScanAndRebuild | Reindex the original on each call, then RebuildIndexed |

The original index, replacement byte arrays and bounded workspaces are allocated in setup.
The `ScanAnd*` pair includes tokenization/index construction but reuses its storage; it is not
a measurement of allocating a fresh object graph. The indexed pair excludes initial indexing.
All four finalize BodyLength/CheckSum identically, and compare exact output against an independent
ASCII-string reconstruction. Checks cross BodyLength 99/100 and 999/1000 digit boundaries.

Edits address field occurrences, not just tag numbers, preserving nested repeated tags and unknown
fields. Cases: none, equal width, grow, shrink; one/eight commands; first, last or spread-out
identifiers. Growth adds `32 * (commandIndex + 1)` bytes relative to the original value; shrink
clamps at one byte. Small fixtures may receive multiple edits to the same field; reported
`DistinctEditedFields` makes this explicit. These are research transforms on known text fields,
not a production validation/ownership API. No arbitrary original-buffer mutation is supported.
Even the no-edit case copies to owned output; borrowed pass-through is a separate contract and
is not timed here. Both editors recompute checksum; an equal-width delta-checksum specialization
has not been measured.

### Measurements

EPYC 7763, Ubuntu 24.04, SDK 10.0.400, .NET 10.0.11, Release, BDN 0.15.8. No concurrent
local builds/loads during timing. Primary rotated loads use eight 100 ms blocks per method/case,
after 200 ms warmup; supplementary edit placement uses eight 50 ms blocks after 100 ms warmup.
These report medians of **block means**, not operation p50/p99. Delegates and digest accumulation
add harness overhead, so do not compare absolute load times directly with BDN means.

BDN confirmation: two launches, three warmups, seven iterations per launch; means (SD), us:

Generated evidence is retained in [`codec-design-results/`](codec-design-results/): the complete
read/edit block samples and counters (`*-final.jsonl`), locality supplement (`edit-positions.jsonl`),
sampled tails (`tail.jsonl`), and BDN summaries (`read-bdn.csv`, `edit-bdn.csv`). Setup/check
console messages are omitted from JSONL; no measurement rows are filtered out.

| Read case | GeneratedCached | FusedTyped | Interpretation |
|-----------|----------------:|-----------:|----------------|
| Small, 1 use | 0.1641 (0.0033) | 0.2630 (0.0100) | Prototype 60% slower |
| Small, 4 uses | 0.2182 (0.0054) | 0.3122 (0.0039) | Prototype 43% slower |
| X/50, 1 use | 15.266 (0.622) | 12.297 (0.258) | About 19% lower mean |
| X/50, 4 uses | 18.172 (0.625) | 15.511 (0.781) | About 15% lower mean |
| W/50, 1 use | 12.376 (0.613) | 11.359 (0.293) | About 8% lower mean |
| W/50, 4 uses | 15.472 (0.651) | 14.364 (0.592) | About 7% lower mean |

Small has no outer group to fuse and the prototype scans the whole message rather than the
generated view's early exit. It is therefore a useful counterexample to a universal replacement,
not proof that fusion itself must hurt all small inputs.

With four uses, rotated-load X/50 medians were 31.815 us GeneratedLazy, 19.846 GeneratedCached,
27.080 FusedLazy, 14.193 FusedTyped, 16.465 Visitor. W/50 was
28.663 / 16.798 / 24.717 / 13.899 / 16.290 us. Saving values in ordinary locals already removes
substantial repeated parse work; an eager row API is not needed to get that particular benefit.
The visitor did not clearly outperform the fused typed consumer.

BDN edit confirmation, X/50, eight first-position commands; means (SD), us:

| Edit case | ShiftEach | RebuildIndexed | ScanAndShift | ScanAndRebuild |
|-----------|----------:|---------------:|-------------:|---------------:|
| Equal width | 4.257 (0.114) | 3.908 (0.152) | 10.232 (0.890) | 9.683 (0.144) |
| Grow | 8.533 (0.130) | 4.395 (0.311) | 15.094 (1.001) | 10.106 (0.204) |

Coalescing growing edits reduced the indexed mean about 48%; including indexing on **both**
sides reduced it about 33%. Comparing ScanAndRebuild against cached ShiftEach would misleadingly
make the rebuild appear slower. Initial indexing itself is a material cost.

Position matters. Supplementary X/50 Grow8 rotated medians for ShiftEach/RebuildIndexed were
4.900/4.123 us at the end, 6.640/4.163 us spread across the frame. The advantage shrinks greatly
near the end, particularly when indexing is included (10.436/10.105 us there). These short,
variable shared-host runs do not justify a universal editor winner.

The full matrices include no edits, one edit and shrink cases. For example, X/50 Shrink8
first-position load medians were 8.478/3.793 us indexed shifts/rebuild and 14.473/9.578 us
including indexing; W/50 Grow8 was 7.634/3.975 and 12.645/9.087 us respectively.

### Copies, retention, tails and code

For an 8,489-byte X/50 input growing to 9,641 bytes, eight first-position shifts requested
**75,607 logical copied bytes**, versus **9,648** for coalesced rebuilding. Moving those edits
to the end reduced the shift count to 14,352 bytes. Counters count bytes passed to span/array
copies, including initial copy and envelope updates; they exclude metadata writes and checksum
reads and are not hardware memory-traffic measurements. The counters are included in edit
timings; no claim of an uninstrumented optimal implementation is made.

The shared edit harness retained 39,060 payload bytes for X/50 Grow8 (original, 9,786-byte output
capacity, field index, all variant workspaces and replacement plans). Small/None retained
1,056 payload bytes for a 176-byte input. These totals exclude object headers and are **not**
minimum per-strategy retention. Zero warmed allocation does not mean zero retained memory.
Retaining a tiny view into a large rented backing buffer, owned string DTOs and long-lived
application state are not measured by this experiment.

Outer-scan diagnostics counted 655 tokens / 8,783 bytes for fused X/50 and 557 / 8,019 for W/50.
Lookahead at boundaries can be counted again; skipped nested ranges are reported separately.
This does not count tokenization inside generated skip helpers or measure the generated
baseline's actual scanner work. Do not infer a measured total-byte reduction from these counters.

Separate three-second closed-loop loads sample every 64th operation into an 8,192-element rolling
buffer. Example X/50 Grow8 p50/p95/p99, us: ShiftEach **8.1/12.9/22.0**, RebuildIndexed
**4.0/5.8/8.8**, ScanAndShift **14.1/21.1/34.8**, ScanAndRebuild **10.0/16.1/33.1**.
When the buffer wraps, percentiles describe trailing samples. Sampling can alias periodic work
and includes harness overhead; these are not network/SLA tails. Rotated loads and BDN reported
zero warmed managed allocation; the first generated-reader tail window recorded 128 bytes total
of unattributed overhead, while the other tail windows reported zero.

A separate FullOpts JIT run (tiering disabled, not timed) showed generated enumerator constructor/
MoveNext/Contains at 282/418/143 native bytes; prototype constructor/MoveNext at 313/481,
DesignMembership.Contains at 166 and Visit at 225. These are selected methods, not total code
sizes, and exclude generated projection constructors and other helpers. Fusion is not free code.

### Outcome and reproduction

No production strategy is adopted. The useful next candidate is a **generated fused group
projection**, preserving general nested topology and retaining ordinary readers/early-exit
views as alternatives. Local caching remains the simplest answer to repeated getters.
Explicit, batched transformation deserves a separate investigation if editing is a real use
case; it should not become a setter that silently rearranges an existing wire frame.

From an isolated worktree on the experimental branch:

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --design-check
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --design-load read
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --design-load edit
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --design-edit-positions
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --design-tail
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*DesignReadBenchmarks.GeneratedCached*X50*' '*DesignReadBenchmarks.FusedTyped*X50*' \
  --launchCount 2 --warmupCount 3 --iterationCount 7 --buildTimeout 600
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*DesignEditBenchmarks*X50*Grow8*First*' \
  --launchCount 2 --warmupCount 3 --iterationCount 7 --buildTimeout 600
```

`--design-check` is also wired into CI; timing runs remain manual.

## Temporal reader (#30)

From an experimental worktree rooted at prework commit `a0b1aab`:

```bash
git apply --check benchmarks/experiments/temporal-reader/fast-path.patch
git apply benchmarks/experiments/temporal-reader/fast-path.patch
dotnet run -c Release --project benchmarks/experiments/temporal-reader/TemporalReaderProbe.csproj
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --reader-process-check
```

The probe compares the parser contract on 75,293 inputs and exercises the X/W decode and
projection matrices. It also works against the unchanged baseline as a reference check;
passing it alone does not prove that the patch has been applied. It is .NET 10 evidence,
not a substitute for permanent tests on all supported consumer runtimes.

Run the process benchmarks from each worktree's own root so BenchmarkDotNet rebuilds the
correct generator. Use `--buildTimeout 300` for the full dictionary.

## Group membership (#33)

For the post-projection decision, use the integrated binary baseline `835941c` and bounded
bitmap policy `7db739c` (or `9249509`, which also updates the order/process fixtures). Build each benchmark project
in its own worktree, then run:

```bash
dotnet run -c Release --project benchmarks/experiments/paired-load/PairedLoad.csproj -- \
  --residual /absolute/binary/benchmark.dll /absolute/bitmap/benchmark.dll
```

This mode initializes and reports group metadata separately, then alternates full/projected
reader, slicing and combined codec workloads. It accepts both generated representations.
Only one membership array is retained per group by the bounded candidate; tags above the
8 KiB bitmap budget or too sparse for a memory win fall back to the sorted array.

The patches below are **archived pre-projection prototypes**, not the bounded implementation.
Do not apply them on top of the current integrated runtime:

Apply either `group-membership/hashset.patch` or `group-membership/bitmap.patch` to its own
clean worktree, then run:

```bash
dotnet run -c Release --project benchmarks/experiments/group-membership/MembershipProbe.csproj
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataReaderBenchmarks*' --buildTimeout 300
```

The membership probe requires one of the experimental metadata representations. Both
prototypes retain the original integer arrays as well; they do not represent a final
retained-memory policy. In particular, the bitmap prototype is for the bounded fixture
tag ranges, not an unbounded production policy for arbitrary custom tags.

The packaged membership variants also retain the internal sorted-constructor path used by
the newer process-decomposition benchmarks. That explicit path remains binary search;
use `MarketDataReaderBenchmarks` for the generated HashSet/bitmap comparison. This
compatibility addition changes the experimental runtime shape from the original measurement
snapshot, so remeasure these packaged variants rather than treating historical timings as
measurements of their exact code layout.

The baseline's source-shape assertion for the generated binary-constructor call intentionally
does not match the HashSet/bitmap constructor calls. Running the entire baseline test suite
unchanged on these variants therefore needs that assertion adapted; the membership probe
checks their runtime behavior and is not a claim that every unchanged baseline test passes.

## Interleaved load

After building the baseline and temporal variant benchmark projects in Release:

```bash
dotnet run -c Release --project benchmarks/experiments/paired-load/PairedLoad.csproj -- \
  /absolute/baseline/benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll \
  /absolute/variant/benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll
```

The runner loads the assemblies in separate load contexts, warms their workloads, and
alternates eight 250 ms blocks per variant/case on one thread. It reports samples,
medians and measured thread allocations. This supplements, not replaces, BenchmarkDotNet.

Probes are intentionally outside the main solution. Generated binaries, session logs and
machine-specific NuGet-cache paths must not be committed.
