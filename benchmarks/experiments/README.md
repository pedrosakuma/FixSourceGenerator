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

## Native DTO + presence/dirty mask experiment

This follow-up implements and **runs** concrete owned DTOs; it does not promote them into the
generator/runtime or adopt a production API. Source: `NativeDtoExperiments.cs`, the benchmark-only
`Schema/NativeDto.xml`, and `shared/native-dto-summary.py`. It extends the same isolated branch
after `ff882370`; the prior read/edit measurements above were not rerun or replaced.

### Representation, scope and correctness

`NativeEntryDto` has two native `decimal` slots, one owned UTF8-decoded `string`, and separate
`ulong Presence` / `ulong Dirty` masks. Bits 0/1/2 are dense schema field ordinals, **not FIX tags**.
`List<NativeEntryDto>` stores concrete entries. Absent numbers still occupy their full native
slots. Materialization initializes `Dirty=0`; setters compare current presence/value and mark
only real changes. Present zero differs from absent, clearing absent and setting an equal value
are no-ops, and A→B→A remains dirty until the next explicit baseline. Dirty means “changed since
baseline”, not “unequal to the original value”.

There are deliberately two different workloads:

* **Projected Small / X1 / X10 / X50:** the existing real frames, generated group enumeration and
  new generated views select identifier + price + size (ClOrdID/Price/OrderQty or
  MDEntryID/MDEntryPx/MDEntrySize). DTOs own only these fields, **not the full message**.
  No X/W DTO serialization is claimed. These fixture identifiers are present and ASCII; digest
  string lengths and span byte lengths are equivalent only under that control. Optional missing
  identifiers, malformed UTF8, arbitrary schemas and nested DTO graphs are not covered.
* **Full UND1 / UND50:** a separate controlled FIX4.2 `UND` schema has required SenderCompID,
  TargetCompID, MsgSeqNum, SendingTime, and a required group of identifier plus two optional
  numbers. The complete message DTO owns both header identifiers, typed header scalars and the
  whole group. The adapter calls the **existing generated scoped writer**, including its header
  constructor and actual `Entries.Count`. It emits complete BodyLength/checksum frames using
  **presence, never dirty**. It is not compared against a partially serialized market frame.

Header mutation and group add/remove are explicitly **outside dirty tracking**; row setters
are the measured tracking scope. Correctness checks nevertheless add/remove rows (including an
empty group) and verify serialization uses the current count. The internal prototype permits
direct field access for materialization and does not provide a hardened public DTO API.
Writer scratch is fixed at 256 UTF8 bytes per identifier, with a 16 KiB output; arbitrary
lengths and production validation/escaping are not implemented.

`--native-dto-check` checks per-row generated-reader values/presence, all four reuse counts,
absence versus zero, clearing and repeated sets, A→B→A, and initial clean masks.
Full-message checks cover sparse/dense 1/10/50 entries, negative/zero/absent prices, growing and
shrinking identifiers, UTF8 `AÇÃO`, fresh/reused outputs, and equivalent final edit variants.
An independent string/UTF8 oracle constructs every emitted field, decimal text, BodyLength and
CheckSum; it compares **all produced bytes**, not just a digest. Checks also decode changed
values with the generated reader. This proves fidelity to this controlled canonical oracle,
not fidelity to arbitrary original wire formatting. Unknown tags, original order, decimal
spelling and raw bytes are **not retained** by the DTO.

### Measurement method and fair baselines

Two separate sequential Release processes on AMD EPYC 7763, Ubuntu 24.04.4, SDK 10.0.400,
.NET 10.0.11, x64 workstation GC. Each contains 150 method/case rows: 200 ms warmup per
method/case, then eight 100 ms blocks with rotated starting method and alternating direction.
Fixture construction/validation and the retained-graph probe are outside timing. No concurrent
local build or timed load was launched. These are **medians of block means**, not operation
percentiles, BDN means or confidence intervals. This follow-up has no additional BDN/heap-profiler
confirmation. Both processes and all blocks are retained, without selecting only the faster run.

Evidence: `codec-design-results/native-dto-run1.jsonl`, `native-dto-run2.jsonl` and
`native-dto-summary.csv`. JSONL includes runtime metadata, retention rows, every block's
operations/time/allocation/GC counts, medians and min/max. CSV is a generated summary of both
processes, not a second independent experiment. GC counters are process-wide interval counts;
per-thread allocation is divided by actual operations. Raw GC totals must not be compared
without accounting for differing throughput. The worst within-case max/min block ratio was
1.68; small percentage differences should not be treated as resolved effects on this shared host.

Read phases are separated:

* `acquire`: generated location/conversion, UTF8→owned string, list and entry acquisition.
  `ReuseObjects` reuses the list/rows but **still allocates new strings**.
* `combined`: full per-message acquisition/location plus N immediate uses **per row**, N=1/4/16/64.
  `GeneratedGetters` repeats conversions; `GeneratedCached` caches all three selected values in
  locals. `DtoFresh`/`DtoReuseObjects` re-read native slots/masks; the corresponding `*Cached`
  variants also cache DTO values in locals, avoiding an unfair “only the baseline may cache”
  comparison. The reader has a borrowed-span contract; DTO ownership is additional work.
* `steady`: DTOs already exist. `CachedValues` is a setup-created typed tuple array;
  `IndexedViewGetters` reuses setup-created entry ranges, constructs views, and repeats getters
  without re-enumerating groups. `DtoCached` caches per-row DTO values in locals. These exclude
  acquisition/storage preparation and **cannot establish amortization on their own**.

Acquisition results, process 1 / process 2 medians, microseconds:

| Projection | Fresh | Reused rows/list | Fresh B/op | Reused B/op |
|------------|------:|----------------:|-----------:|------------:|
| Small | 0.231 / 0.220 | 0.203 / 0.189 | 168 | 32 |
| X1 | 0.429 / 0.442 | 0.388 / 0.389 | 200 | 64 |
| X10 | 3.465 / 3.475 | 3.206 / 3.242 | 1,496 | 640 |
| X50 | 16.998 / 16.703 | 16.621 / 15.938 | 7,256 | 3,200 |

Selected **combined** results, microseconds (process 1 / process 2):

| Case | GeneratedCached | GeneratedGetters | DtoFreshCached | DtoReuseObjectsCached |
|------|----------------:|-----------------:|---------------:|----------------------:|
| Small, 1 use | 0.214 / 0.205 | 0.233 / 0.212 | 0.259 / 0.247 | 0.242 / 0.233 |
| Small, 4 uses | 0.287 / 0.293 | 0.578 / 0.509 | 0.343 / 0.345 | 0.323 / 0.319 |
| Small, 64 uses | 1.943 / 1.952 | 7.197 / 6.233 | 2.077 / 2.059 | 2.122 / 2.056 |
| X50, 1 use | 16.064 / 17.492 | 16.086 / 17.190 | 18.429 / 18.404 | 17.352 / 17.679 |
| X50, 4 uses | 20.686 / 21.301 | 34.382 / 32.855 | 22.933 / 22.787 | 21.746 / 22.015 |
| X50, 16 uses | 37.535 / 38.654 | 96.458 / 96.539 | 40.569 / 42.164 | 39.973 / 39.041 |
| X50, 64 uses | 107.873 / 107.480 | 353.106 / 353.857 | 109.089 / 114.196 | 109.237 / 115.173 |

The tested Small/X50 medians cross the **uncached getter** baseline by four uses, including
acquisition, but do not establish an exact crossover between one and four.
There is **no demonstrated win over ordinary cached locals** in these Small/X50 cases through
64 uses. At 64 uses the fully cached approaches approach one another; small gaps are noisy.
Without DTO-side caching, X50/64 takes 144.175 / 142.967 us fresh, showing why repeating mask
tests/native loads is not the same comparison as cached locals.

Steady X50/1 use is only 1.615 / 1.598 us for `DtoCached`, versus 1.488 / 1.499 for
`CachedValues`, 1.990 / 1.946 for uncached `Dto`, and 10.028 / 9.950 for `IndexedViewGetters`.
That appealing setup-excluded DTO number must not be presented as the full-message cost.

### Four logical edits and complete serialization

Each command changes the first entry's identifier and price, exercising grow/shrink,
present zero and clear. Replacement strings are pre-owned plans for **every** variant;
constructing new application edit strings is not timed. Every command has a real change.
`WriteEach` serializes after each command; `WriteOnce` coalesces application changes and
serializes the final full frame once. The final frame is identical, but the former additionally
produces three intermediate frames; this is an application batching choice, not a faster writer.

`AcqFresh` owns a new DTO graph; `AcqReuse` reloads original fields into reusable objects.
`Resident` excludes acquisition and includes the same first-row reset on both tracking variants.
Output/state/UTF8 scratch are reused except `FreshOutput`, which allocates the same 16 KiB
capacity per operation. Fresh output is one buffer per logical operation, reused for that
operation's serializations; writer state and scratch acquisition remain excluded.
The header parser currently allocates a temporary timestamp string (64 B) before DateTime
conversion; this prototype is not an optimized timestamp materializer.

| Full case / variant | Process 1 / 2, us | B/op |
|---------------------|-----------------:|-----:|
| UND1 AcqFresh_WriteOnce | 1.467 / 1.488 | 376 |
| UND1 AcqFresh_WriteEach | 3.176 / 3.145 | 376 |
| UND1 Resident_WriteOnce | 0.618 / 0.618 | 0 |
| UND1 AcqFresh_FreshOutput_WriteOnce | 2.451 / 2.706 | 16,784 |
| UND50 AcqFresh_WriteOnce | 18.698 / 18.359 | 6,256 |
| UND50 AcqReuse_WriteOnce | 18.213 / 17.469 | 2,144 |
| UND50 AcqFresh_WriteEach | 45.945 / 45.710 | 6,256 |
| UND50 Resident_WriteOnce | 9.811 / 9.413 | 0 |
| UND50 Resident_WriteEach | 37.142 / 38.798 | 0 |
| UND50 AcqFresh_FreshOutput_WriteOnce | 20.473 / 19.756 | 22,664 |

UND50 `Resident_NoDirty_WriteOnce` was 10.450 / 9.337 us, versus 9.811 / 9.413 with tracking.
That reversal **does not resolve a dirty-tracking penalty**. The control uses the exact same DTO
shape and presence checks, disabling only dirty-bit updates; it does not remove the dirty word
from the layout or measure a universal mask implementation. Fresh-output UND1 produced
294 Gen0 / 1 Gen1 collections across process 2's timed blocks, while resident variants reported
zero allocation/collections. This directly contradicts treating reused-output zero allocation
as the cost of acquiring a fresh message.

### Sparse slots, allocation versus retention, and conclusion

Shallow row size measured by allocation deltas over 10,000 retained objects is **72 B**:
32 B of native decimal payload, 16 B of masks, an 8 B identifier reference, and object overhead.
The string/list/array graph is additional. Acquisition deltas below keep 1,000 graphs alive;
the holder array is allocated before counting. Retained sizes are separately labelled **x64
layout estimates**, not heap census/RSS; they omit the transient 64 B timestamp string.

| Full DTO | Present numeric fields / allocated slots | Input bytes | Allocated graph B | Retained graph B estimate |
|----------|-----------------------------------------:|------------:|------------------:|-------------------------:|
| UND1 sparse | 0 / 2 | 99 | 376 | 312 |
| UND1 dense | 2 / 2 | 118 | 376 | 312 |
| UND50 sparse | 0 / 100 | 787 | 6,256 | 6,192 |
| UND50 dense | 100 / 100 | 1,738 | 6,256 | 6,192 |

Both processes produced the same allocation figures. Presence masks **do not shrink absent
native slots**: all 100 decimal slots still consume 1,600 B in sparse UND50. Graph estimates
include header/message/list/backing-array/rows/strings, but exclude original input, the 16,384 B
output capacity, 512 B UTF8 scratch, generated writer state and harness storage. They are not
total process retention or the cost of retaining every possible real X field.

Conclusion: owned native DTOs make lifecycle/ownership and edit semantics explicit and eliminate
repeated parsing once acquired. Their acquisition/strings/slots remain real costs. This evidence
supports an **opt-in application representation**, not replacement of borrowed readers, cached
locals, or byte-preserving editors. No production adoption, package publication or merge occurs.

Validation: Release build, all six existing CI fixture commands and the standalone native check
passed. Independent local review covered the implementation and the final caching/evidence
additions; all 300 summary rows were checked against the 2,400 retained measurement blocks.

Reproduce from the experimental worktree (existing dependencies, no new package/tool):

```bash
dotnet build benchmarks/FixSourceGenerator.Benchmarks -c Release --no-restore
DLL=benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll
dotnet "$DLL" --native-dto-check
dotnet "$DLL" --design-check  # includes the native checks; existing CI command covers them
dotnet "$DLL" --combined-check
set -o pipefail
dotnet "$DLL" --native-dto-load | grep '^{' > benchmarks/experiments/codec-design-results/native-dto-run1.jsonl
dotnet "$DLL" --native-dto-load | grep '^{' > benchmarks/experiments/codec-design-results/native-dto-run2.jsonl
python3 benchmarks/experiments/shared/native-dto-summary.py > benchmarks/experiments/codec-design-results/native-dto-summary.csv
```

## Direct borrowed reader → existing typed writer (follow-up round)

Source: `DirectWriterExperiments.cs`, after native DTO commit `55a0173`, on the same
experimental branch based on main `c0765c8`. **No generator/runtime/API changes or fused
scanner.** This round reuses the complete controlled `NativeDto.xml` UND dictionary,
`NativeRoundtripWorkload` fixture, its actual generated-writer DTO adapter, and independent
string-based byte oracle. It is not the partial X numeric projection from earlier rounds.

### Contract and fair input-start boundary

`DirectReadWrite` borrows the immutable input, parses header scalars using the existing
generated runtime (there is no generated header reader), enumerates existing generated UND
entry readers, caches nullable decimals in locals and passes identifier/header byte spans
straight to the existing scoped writer. It processes one entry at a time. The source array
stays alive until `Finish`; the owned destination is always a distinct array. There is no
raw passthrough, initial input copy in the timed path, or whole-message value/index cache:
**even None fully decodes/re-encodes known fields and finishes BodyLength/checksum.**

`DtoFreshReadWrite` starts at the identical input, uses the **same header scan and native
scalar parsers**, owns sender/target/entry strings and a fresh message/list/entry graph,
applies the same logical plan, then calls the previous round's actual typed writer adapter.
Both use the same generated factory, upfront group count, required entry ID, optional
price/size setters and `EndGroup(...).Finish()` path. The DTO retains its existing presence/
dirty semantics; the direct path needs nullable locals, not dirty masks. FourEdits measures
four native setter updates versus four local updates followed by one write, not an isolated
equal-cost setter microbenchmark. There is no setup-excluded resident DTO in this comparison.

The former DTO adapter's timestamp-string acquisition was **not** reused: both new paths
parse the timestamp natively. Consequently UND50 DTO acquisition/write allocates 6,192 B,
64 B less than the previous round's 6,256 B. Old timings are **not** comparison baselines.
The primary pair reuses the same 16,384-byte destination capacity. Both writer state arrays
are initialized in setup and safely reused; DTO UTF8 serialization uses the existing
512-byte scratch array. Input, expected bytes, fixture graph, replacement plans, state and
buffers are setup-resident and excluded from per-operation allocation, not claimed free
in total memory. FreshOutput separately allocates a new 16 KiB destination on every call.

All replacement strings **and** their UTF8 bytes are preallocated; the application plan is
assumed available in each representation. Converting a newly received .NET string into a
direct-path replacement buffer is not timed. DTO conversion of its final owned strings
to UTF8 is timed; unchanged direct identifiers never take UTF8→string→UTF8. Both include
every per-message header scan, group traversal and scalar parse; no entry offsets or
parsed scan cache are precomputed. This is a full transformation, not a selective-read test.

| Plan | Full output transformation |
|------|----------------------------|
| None | No logical change, full canonical re-encode |
| Scalar | First entry Price becomes present zero; Size becomes absent |
| Grow | First ID becomes `AÇÃO-IDENTIFIER-GROWN-0000000000000000000000` (UTF8 growth) |
| Shrink | First ID becomes `Z` |
| FourEdits | First ID: G → long ID → MID → FINAL-IDENTIFIER; Price: 102.5 → absent → zero → 102.5; write only once |
| FilterSome | Keep original entries whose input Price is present and ≥120; preserve input order |

Dense sources are exactly the preceding round's canonical fixtures: IDs `ENTRY-nnn`,
Price `101.25 + index`, Size 100. Mixed cycles `(Price, Size)` through `(absent, 0)`,
`(0, absent)`, `(101.25 + index, 100)`. Sparse correctness inputs omit both numbers.
FilterSome keeps **31/50 dense** or **10/50 mixed** rows; at 1/10 dense it keeps **zero**,
not “some.” FilterAll/FilterNone correctness cases explicitly check all/zero survivors.
No reordering, nested groups/components, unknown tags, duplicate tags, malformed input,
arbitrary text encodings or arbitrary wire formatting are supported by this experiment.
Known-field order and numeric/time formatting follow generated canonical output. Exact
byte identity is required only for this canonical source; no lossless general FIX relay
claim is made.

### Correctness and filter work

`--direct-writer-check` checks **1,152 outputs**: 0/1/10/50 entries × dense/sparse/mixed ×
eight plans × fresh/reused destinations × three state reuses × both methods. Expected
semantics come from fixture-native values, before either timed decoder runs; expected
filtering/edits deliberately do not call the timed predicate/transform helpers. Both outputs
must match the complete independent byte oracle, with additional independently calculated
BodyLength/checksum, group row count, source immutability/nonaliasing and unchanged canonical
byte checks. This covers clearing present zero, setting absent to zero, retaining absent
and present-zero optional values, Unicode growth, shrink and empty groups.
`--design-check` includes this command's checks, so existing CI covers the new experiment.

Direct filtering includes **two full group traversals**: count before `BeginNoEntries`,
then evaluate the same input predicate and emit. At 50 entries it invokes Price getters
100 times, versus 50 for DTO acquisition; Size getters are 31 dense / 10 mixed versus 50
for the DTO. Absent getters return null rather than parse a numeric payload. The DTO
materializes all entries, then stably compacts the list in one allocation-free linear
pass over owned values; it does not acquire only survivors. Work counts in JSON are
derived outside timing, not instrumented counters. Caching the first direct pass into
an array, or bypassing the count contract, would be a different experiment.

### Measurements: preserve noisy matrix, add bounded confirmation

SDK 10.0.400, .NET 10.0.11, Ubuntu 24.04.4 x64, AMD EPYC 7763 (16 exposed logical CPUs),
workstation GC. Two sequential independent processes, 44 method/case rows each, 200 ms
warmup per variant and eight rotated 100 ms blocks. Alternation reverses the pair each
round; each method reconstructs its own per-message input state. No concurrent timed
load/build was launched by this experiment; shared-host scheduling is uncontrolled.
Each block records operations, current-thread allocated bytes and process GC deltas.
The harness batches 32 calls and consumes returned frame lengths. Values are medians of
block means, **not operation percentiles, confidence intervals or BDN results**.

The original matrix was severely noisy: UND50 None direct ranged 28.560–118.370 µs in
process 1, versus 22.036–33.148 µs in process 2. **Do not treat its small latency deltas as
resolved wins** or compare absolute timings between processes. All 704 original blocks
remain available; none were discarded. Full raw data: `direct-writer-run1.jsonl` /
`direct-writer-run2.jsonl`; `direct-writer-summary.csv` includes absolute and percentage
deltas for **every** scenario, including 1/10 entries.

Original matrix, UND50, µs **process 1 / process 2**. Reduction is `(DTO − direct) / DTO`;
negative means direct was slower.

| Plan / density | DirectReadWrite µs | DtoFreshReadWrite µs | Direct reduction % | Direct / DTO B/op |
|----------------|-------------------|---------------------|--------------------|-------------------|
| None / dense | 65.086 / 26.634 | 67.880 / 27.121 | 4.1 / 1.8 | 0 / 6,192 |
| Scalar / dense | 90.596 / 23.091 | 107.357 / 27.247 | 15.6 / 15.3 | 0 / 6,192 |
| Grow / dense | 91.213 / 23.187 | 113.002 / 25.276 | 19.3 / 8.3 | 0 / 6,192 |
| Shrink / dense | 80.443 / 23.252 | 104.163 / 26.353 | 22.8 / 11.8 | 0 / 6,192 |
| FourEdits / dense | 84.179 / 24.185 | 102.169 / 27.300 | 17.6 / 11.4 | 0 / 6,192 |
| FilterSome / dense | 118.328 / 25.978 | 91.328 / 23.377 | −29.6 / −11.1 | 0 / 6,192 |
| None / mixed | 59.616 / 14.751 | 80.486 / 18.347 | 25.9 / 19.6 | 0 / 6,192 |
| Scalar / mixed | 59.614 / 14.680 | 75.795 / 19.288 | 21.3 / 23.9 | 0 / 6,192 |
| FilterSome / mixed | 66.388 / 13.957 | 51.904 / 11.953 | −27.9 / −16.8 | 0 / 6,192 |
| FourEdits / dense, fresh destination | 91.584 / 24.050 | 110.844 / 30.534 | 17.4 / 21.2 | 16,408 / 22,600 |

At UND1 / UND10, all reusable-output direct variants measured 0 B/op; fresh DTOs measured
312 / 1,392 B/op. Original dense None direct versus DTO was 4.975/6.692 µs and 1.203/1.530 µs
at UND1; 20.951/23.199 µs and 5.135/5.519 µs at UND10 (each pair is direct/DTO, first then
second process). Some small-case signs reversed, e.g. UND10 Shrink: −5.8% then +24.4%;
zero-survivor filtering at UND10: +6.3% then −12.0%. Allocation, not those timing signs,
is the robust observation for the small matrix.

Because of that variability, a **bounded follow-up selected after observing the matrix**
kept only UND50 dense None/FourEdits/FilterSome plus mixed FilterSome. Two further sequential
processes, eight method/case rows each, **1 s warmups + eight 1 s rotated blocks**. Same
workload methods and boundaries; only harness case selection/durations changed. This is
longer-block confirmation, not a full-matrix rerun or BenchmarkDotNet. All 128 additional
blocks and paired summaries are retained in `direct-writer-confirm-run1.jsonl`,
`direct-writer-confirm-run2.jsonl`, `direct-writer-confirm-summary.csv`.

| Confirmation at UND50 | Direct µs, P1 / P2 | Fresh DTO µs, P1 / P2 | Saved µs, P1 / P2 | Reduction %, P1 / P2 |
|-----------------------|---------------------|-----------------------|-------------------|---------------------|
| Dense None | 17.437 / 17.167 | 19.482 / 19.732 | 2.045 / 2.565 | 10.5 / 13.0 |
| Dense FourEdits | 16.843 / 17.237 | 20.568 / 19.940 | 3.724 / 2.703 | 18.1 / 13.6 |
| Dense FilterSome (31 survivors) | 20.068 / 19.903 | 17.234 / 17.224 | −2.834 / −2.679 | −16.4 / −15.6 |
| Mixed FilterSome (10 survivors) | 12.063 / 11.725 | 10.627 / 10.729 | −1.436 / −0.995 | −13.5 / −9.3 |

Every confirmation pair remains **0 / 6,192 B/op**. Dense None block ranges were direct
16.184–21.376 / 16.135–18.899 µs and DTO 18.520–21.999 / 19.208–21.968 µs: still shared-host
variation, not a universal speedup bound. GC counts across timed matrix blocks were
105/4/1 then 327/4/1 (Gen0/1/2); confirmation 730/11/0 then 746/11/1. Counts are process
events during blocks, not object retention or events exclusively caused by a particular
method, and longer/cheaper blocks perform different numbers of operations.

**Finding:** this existing borrowed API can already avoid the owned graph and unchanged
text conversions for complete known messages, without fusion or a new public API. In
longer-block full-copy/four-edit checks it also reduced elapsed time. Filtering reverses
the timing result despite zero allocation: the extra traversal/predicate parsing exceeds
the ownership savings for these fixtures. “Avoids allocations” does not imply “always
fastest”; a retained mutable DTO may suit other lifecycles, and the data do not establish
the best general architecture. Source lifetime, group count and controlled topology are
part of the result, not removable footnotes. No merge, adoption or package publication.

Validation: targeted Release build, standalone direct/native checks and all six existing
CI fixture commands passed. Independent local implementation review preceded measurement;
the retained raw/CSV samples and final reporting were also reviewed. Summary scripts
validate case counts, eight blocks, medians, allocation arithmetic and paired dimensions.

Reproduce from this worktree with existing restored dependencies (no new packages):

```bash
dotnet build benchmarks/FixSourceGenerator.Benchmarks -c Release --no-restore
DLL=benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll
dotnet "$DLL" --direct-writer-check
dotnet "$DLL" --design-check
set -o pipefail
dotnet "$DLL" --direct-writer-load | grep '^{' > benchmarks/experiments/codec-design-results/direct-writer-run1.jsonl
dotnet "$DLL" --direct-writer-load | grep '^{' > benchmarks/experiments/codec-design-results/direct-writer-run2.jsonl
python3 benchmarks/experiments/shared/direct-writer-summary.py > benchmarks/experiments/codec-design-results/direct-writer-summary.csv
# Optional bounded longer-block confirmation; run only after the matrix has finished.
dotnet "$DLL" --direct-writer-confirm | grep '^{' > benchmarks/experiments/codec-design-results/direct-writer-confirm-run1.jsonl
dotnet "$DLL" --direct-writer-confirm | grep '^{' > benchmarks/experiments/codec-design-results/direct-writer-confirm-run2.jsonl
python3 benchmarks/experiments/shared/direct-writer-summary.py --confirmation > benchmarks/experiments/codec-design-results/direct-writer-confirm-summary.csv
```

## Follow-up: actual generated eager enumeration from declaration + dictionary

This round answers the generation question directly, unlike the **handwritten** fused
scanners in the first round. `benchmarks/experiments/eager-projection/` contains a real,
isolated Roslyn analyzer project. The benchmark references it as an analyzer and supplies
the existing `Schema/NativeDto.xml` as an `AdditionalFile`. No production generator,
runtime, schema, or public API implementation changed. This is still research on draft
PR #41, not adoption, a proposed stable attribute API, or full FIX50 support.

### What is actually generated

The consumer declaration in `EagerProjections.cs` is:

```csharp
[ExperimentalEager("NativeDto.xml", "NativeEnvelope", "NoEntries",
    "FixSourceGenerator.Benchmarks.Generated.Fix.V42.Runtime")]
internal readonly ref partial struct UndEagerValues
{
    public partial ReadOnlySpan<byte> EntryID { get; }
    public partial decimal? Price { get; }
    public partial decimal? Size { get; }
}

// Consumer code contains no tokenizer, tag switch, or handwritten scanner.
foreach (var row in UndEagerValues.Enumerate(input))
{
    decimal? price = row.Price;
    ReadOnlySpan<byte> id = row.EntryID;
}
```

`EagerProjectionGenerator.Execute` resolves the annotated type and partial properties;
`ReadSchema` resolves dictionary names/tags, the delimiter, count, header and membership;
`Emit` produces the immutable native value, getters, presence flags and specialized
enumerator. Numeric backing is `decimal?` plus wire-presence flags, **not wire offsets**.
Text backing is a borrowed span. The constructor scans the header to the group count;
`MoveNext` combines entry delimiting/membership with immediate parsing of selected
numbers. It reads lookahead to establish the entry boundary before publishing `Current`.
There are no consumer-written per-tag handlers.

The second generated declaration, `UndPriceValues`, selects only Price and exposes it as
`Quote` using `[ExperimentalField("Price")]`. Unselected Size is scanned structurally but
not converted. Positive generator tests also change the counter/delimiter/numeric tags
to 1900–1903, rename the dictionary Price field, reorder metadata and property declarations,
and add an unused known field. Both variants emit, compile and **execute** real generated
code with the production generator providing the actual runtime. This is not a
hardcoded tag-270 fixture or a snapshot-only test.

The production schema parser/model was inspected. It resolves recursive components and
many types/topologies not implemented here and couples to the production diagnostics.
Instead of modifying it or pretending to support its full model, the experiment uses a
small, explicit XML front end and fails closed with `EAGER001`:

- One required direct group as the entire selected message body; no root scalars,
  sibling groups, nested groups, or component references. **No nested topology is
  supported.** Such dictionaries are compile-time errors, not silently flattened.
- Required STRING delimiter, then optional STRING/PRICE/QTY/AMT/FLOAT fields only.
  Selected strings use `ReadOnlySpan<byte>`; selected numbers use `decimal?`.
  No required non-delimiter fields, DATA/XMLDATA, enum conversion, arbitrary types,
  duplicate field definitions/members, unresolved references or overlapping
  header/count/entry/trailer tags.
- Scalar header including MsgType(35), CheckSum(10)-only trailer, exactly one matching
  dictionary filename. Namespace-level, non-generic readonly ref partial structs with
  public getter-only partial properties; unsupported declarations/member collisions
  produce diagnostics. The explicit runtime namespace is experimental configuration,
  not a generally inferred package contract.
- Unknown/out-of-scope input tags, malformed token structure, wrong/duplicate MsgType,
  negative/overflow/partially parsed counts, too few/many entries, and missing/trailing
  tokens are rejected with `FormatException`. `Current` is cleared before advancing,
  on completion and on failure; subsequent `MoveNext` after failure returns false.
  This is structural validation, **not a full FIX validator**: it does not verify all
  required header values, BodyLength, checksum contents or nonempty required strings.

The shared **generated** strict tokenizer checks digit-only positive tags, overflow and
SOH termination. This is deliberately stricter than the existing production tokenizer,
whose permissive behavior is not changed. Numeric conversion reuses the production
`FixSpanReader.TryParseDecimal`: it accepts a valid numeric prefix (`1junk` → `1`).
Malformed nonnumeric/overflow values remain present but nullable-null; absent values are
`HasPrice == false`, numeric zero is `HasPrice == true && Price == 0`. First occurrence
wins even if malformed/empty; a later valid duplicate cannot overwrite it. This matches
the existing **FixView** policy, whereas the **full reader uses the last occurrence**.
Tests explicitly assert both policies; timed inputs contain no duplicates. There is no
claim of equivalent malformed/duplicate-input acceptance across both APIs.

The tokenizer still scans numeric bytes to locate SOH, then the numeric parser consumes
them. Lookahead delimiter/trailer tokens can also be read again on the next advance.
“Fused” means structural traversal plus selected conversion, **not each byte read once**.

### Fair work and bounded measurement

`EagerReadWorkload.FullCached`, `LazyCached` (the actual generated `UndLazyValues`
FixView), and `Eager` select the same identifier/Price/Size and compute the same digest.
Every variant caches getters in locals, including at Uses4; this does not manufacture
a win against repeated numeric conversion. `PriceCached`/`PriceEager` separately select
only Price, including the once-used sparse scalar control.

The full pipeline compares existing `DirectWriterWorkload.DirectReadWrite` with
`EagerReadWrite`. Both start from the same immutable complete UND bytes, share the
existing header helper, edits, predicate, scoped writer, reusable writer state and
preallocated destination, then serialize a complete canonical frame. UND has all three
entry fields in the projection; this is **not partial X/W re-encoding**. Strings remain
borrowed until Finish and source/destination do not alias. Setup allocation, delegate
creation, input/oracle creation and replacement-string encoding are excluded; input
decoding, filtering/editing and complete output serialization are timed. No DTO
acquisition or historical timings enter this comparison.

Filtering has two explicitly named eager variants:

| Dense UND50, 31 survivors | Price conversions | Size conversions |
|---|---:|---:|
| `DirectReadWrite` | 100 | 31 |
| `EagerReadWrite` (all-fields projection both passes) | 100 | 100 |
| `EagerPriceCountReadWrite` (Price-only count, all-fields write pass) | 100 | 50 |

Those counts are for present valid dense fields; absent fields do not invoke a parser.
The narrow-count variant is **not** silently substituted for the all-fields variant.
Even it converts Size for rows rejected on the writing pass; the existing baseline
already avoids Size for rejected rows in both passes. Count prepasses and all their
work remain timed.

The initial matrix uses 63 cases / 153 method rows, 0.2 s warmup per method, eight
100 ms blocks: UND 1/10/50, dense/sparse/mixed, read-all/read-Price Uses1/4, complete
no-edit/four-edits/filter. It was followed by exactly two separate confirmation
processes, seven selected UND50 cases / 18 rows each, 1 s warmup and eight 1 s blocks.
Methods rotate within each case/block. No concurrent benchmark/build was launched by
this experiment, but this shared host cannot exclude unrelated load.

All **1,512 raw blocks** are retained, including the severely disturbed second
confirmation; no additional “good run” was substituted. Times are medians of eight
**block means**, not request percentiles, BDN results, confidence intervals or pooled
process medians. The current raw records contain .NET 10.0.11, Ubuntu 24.04.4, X64,
workstation GC, process IDs, timings, operations, thread allocation and GC counts.

**UND50 read confirmation, µs, process 1 / process 2:**

| Selected work | Cached full reader | Cached generated FixView | Generated eager |
|---|---:|---:|---:|
| Dense, all fields, Uses1 | 7.454 / 16.451 | 9.098 / 16.018 | 6.989 / 12.984 |
| Sparse, all fields, Uses1 | 2.254 / 4.573 | 3.023 / 4.897 | 1.979 / 3.566 |
| Sparse, Price only, Uses1 | 2.025 / 4.347 | not measured | 1.613 / 4.914 |

Methods are respectively `FullCached` / `LazyCached` / `Eager`, and
`PriceCached` / `PriceEager` in the last row. The dense all-field eager median is 6.2%
lower in process 1 and 21.1% lower in process 2 than the full cached reader, but the
second process is **not stable confirmation of that magnitude**. Its full-reader
dense blocks span 9.118–70.107 µs and eager 7.304–49.309 µs. The sparse scalar control
reverses direction across processes (20.4% lower, then 13.0% higher); do not claim a
universal win, including for sparse reads.

**UND50 complete pipeline confirmation, µs, process 1 / process 2:**

| Work | `DirectReadWrite` | `EagerReadWrite` | `EagerPriceCountReadWrite` |
|---|---:|---:|---:|
| Dense, no edit | 18.365 / 35.830 | 18.333 / 37.742 | not measured |
| Dense, four logical edits then one write | 19.226 / 28.217 | 19.243 / 27.967 | not measured |
| Dense, filter 50→31 | 22.323 / 40.533 | 22.431 / 48.406 | 20.872 / 38.656 |
| Mixed absent/zero, filter 50→10 | 13.120 / 21.461 | 14.535 / 20.335 | 12.248 / 19.202 |

Every measured variant in **all three processes** allocated **0 B/op** on the measured
thread and recorded zero Gen0/1/2 collections. This is steady-state allocation, not zero
setup memory or zero generated-code footprint.

**Interpretation:** actual automatic generation of native borrowed values and group
enumeration works within this bounded schema. Read-only results suggest potential,
including against cached getters rather than repeated conversions. But the complete
no-edit/four-edit pipeline shows essentially no consistent improvement: the common
writer and other work dominate. Eager conversion of unused/rejected Size can regress
filtering; selecting a narrower generated projection for the count pass helps these
medians, not a general optimality proof. Initial matrix and confirmation results also
change direction in some cases; all are available in the CSVs.

These comparisons combine elimination of the full root-reader scan, per-entry
rescanning/offset construction, generated membership, native backing and a different
strict tokenizer. They **do not isolate only numeric conversion or fusion** as the
causal source of a timing delta. No conclusion about a real nested X/W dictionary,
general API adoption, or replacing the existing reader follows.

### Correctness, footprint, review and reproduction

- 25 targeted xUnit cases: actual emitted C# compiles and executes for two dictionary
  layouts; 23 unsupported schema/declaration cases produce `EAGER001`, not a crashed
  generator or silently emitted scanner. These use the existing Roslyn/xUnit versions.
- `--eager-check` includes 1,152 independent complete writer-oracle comparisons
  (both eager variants, 0/1/10/50, dense/sparse/mixed, edits, all/some/zero survivors,
  UTF8, fresh/reused destinations, repeated writer-state reuse). The oracle computes
  full bytes, BodyLength/checksum independently of either read path.
- Independent literal tuples assert presence/zero/absence, empty and invalid first
  duplicates, prefix acceptance/overflow, ordering, first/last entry and empty groups.
  Invalid dangling tokens/lookahead assert no stale `Current`. The existing FixView
  and full-reader duplicate policies are tested separately, not hidden behind a digest.
- `--design-check` includes these new checks; CI also runs the isolated generator
  tests. Final targeted Release build, all generator tests, and all six existing CI
  fixture commands passed. Production tests were not broadly rerun because production
  code was not changed.
- Actual emitted UTF8 source: **4,108 B** for `UndEagerValues`, **3,263 B** for
  `UndPriceValues`, **1,462 B** shared attributes/strict tokenizer, **8,833 B total**.
  Source hashes/line counts are retained in `eager-generated-source-footprint.json`.
  This is source volume, **not native code size or entire application size**; no native
  code-size claim is made. Extra declarations replicate specialized enumerator code.
  A final diagnostics-only tightening produced byte-identical benchmark output hashes,
  so the measured generated code is the final emitted code.
- An independent local code reviewer inspected generator, runtime boundary behavior,
  tests and fairness before measurement and found no significant issues.
  A separate focused review of raw evidence, summaries and reporting, including the
  final diagnostic guards, also found no significant issues.

```bash
# First restore after adding the project references; use your normal/cache NuGet setup.
dotnet restore benchmarks/FixSourceGenerator.Benchmarks
dotnet restore benchmarks/experiments/eager-projection/tests/EagerProjection.Tests.csproj
dotnet test benchmarks/experiments/eager-projection/tests/EagerProjection.Tests.csproj \
  -c Release --no-restore
dotnet build benchmarks/FixSourceGenerator.Benchmarks -c Release --no-restore \
  -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=obj/eager-generated
DLL=benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll
dotnet "$DLL" --eager-check
dotnet "$DLL" --design-check
set -o pipefail
dotnet "$DLL" --eager-load | grep '^{' > benchmarks/experiments/codec-design-results/eager-generated-run1.jsonl
dotnet "$DLL" --eager-confirm | grep '^{' > benchmarks/experiments/codec-design-results/eager-generated-confirm-run1.jsonl
dotnet "$DLL" --eager-confirm | grep '^{' > benchmarks/experiments/codec-design-results/eager-generated-confirm-run2.jsonl
python3 benchmarks/experiments/shared/eager-generated-summary.py > benchmarks/experiments/codec-design-results/eager-generated-summary.csv
python3 benchmarks/experiments/shared/eager-generated-summary.py --confirmation > benchmarks/experiments/codec-design-results/eager-generated-confirm-summary.csv
python3 benchmarks/experiments/shared/eager-generated-summary.py --source-footprint > benchmarks/experiments/codec-design-results/eager-generated-source-footprint.json
```

The summary script verifies row/case counts, eight samples, raw medians/min/max,
allocation/GC arithmetic and paired input/output dimensions. The untouched previous
rounds above remain historical evidence, not comparison baselines for this round.

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
