# FixSourceGenerator.Benchmarks

BenchmarkDotNet micro-benchmarks for the generated reader/writer's CPU and allocation hot paths,
checking the allocation-minimal premise from [`docs/CONTRACT.md`](../../docs/CONTRACT.md) §0/§2
against a real, mature reference implementation:
[QuickFIX/n](https://github.com/connamara/quickfixn) (generic/reflection-based, widely deployed —
included here purely as a baseline for comparison, never as a dependency of the generator itself).

## How it's wired

This project references `FixSourceGenerator.csproj` the same way a real consumer project would
(`docs/USAGE.md` §1: `ProjectReference` with `OutputItemType="Analyzer"`, `AdditionalFiles` for
the schema XML) — so the benchmarks exercise the actual generated code, not a hand-written stand-in.
The schema is `Schema/FIX44-mini.xml` (the same fixture used by `SchemaReaderTests`), producing a
`NewOrderSingle` with a component (`Instrument`), an enumerated `CHAR` field (`Side`), and a
repeating group (`NoPartyIDs`) — enough surface to exercise every reader/writer code path
(scalar/span/enum fields, component nesting, group iteration).
The writer-formatting benchmarks additionally consume the full `FIX50SP2.xml` test dictionary
to exercise generated X/W messages with 10/50 entries.

## Running

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --filter "*"
```

Always run with `-c Release` — BenchmarkDotNet refuses to run a Debug build. Use `--filter` to
narrow to specific benchmarks (e.g. `--filter "*Generated*"` to skip the QuickFIX/n baseline,
which is much slower and dominates total run time).

## Latest recorded numbers

### Integrated encoding, projected decoding and persistent-state load (#34)

This integration combines writer `e116023` and reader `f6a3e21` (local merge `589d29c`),
plus the 64-bit writer-generation correction described below. `CombinedCodecBenchmarks`
measures **encode -> decode -> consume** in one operation, rather than adding independent
microbenchmark means. Both paths reuse destination and typed writer metadata, initialized
once. Prices vary each operation; the consumed digest must match its exact expected delta.
Setup also compares fluent/in-place frames byte-for-byte and full/projected digests.

`FluentFull` uses fluent writing and full readers; `InPlaceProjected` uses optional-tail
setters and generated projections. Both consume the same selected fields. Small is the
FIX44 mini NewOrderSingle routing subset (ClOrdID length, Price and party roles); X/W use
the real full FIX50SP2 schema with 1/10/50 entries. W now also consumes its required
LastUpdateTime on both paths, so its workload differs from earlier reader-only numbers.
This is codec work, not network transport, session management or FIXT composition.

One launch, three warmups and five measured iterations per case, run serially without
concurrent local builds or loads; EPYC 7763, Ubuntu 24.04, SDK 10.0.400, .NET 10.0.11,
BDN 0.15.8, Release:

| Scenario | Fluent/full mean (SD), us | In-place/projected mean (SD), us | Mean reduction |
|----------|-------------------------:|--------------------------------:|---------------:|
| Small | 1.506 (0.0225) | 1.360 (0.0123) | 10% |
| W/1 | 3.166 (0.0143) | 2.500 (0.0466) | 21% |
| W/10 | 15.898 (0.1346) | 11.513 (0.5422) | 28% |
| W/50 | 70.334 (1.0591) | 50.780 (1.8006) | 28% |
| X/1 | 2.727 (0.0626) | 2.038 (0.0476) | 25% |
| X/10 | 18.901 (1.0400) | 13.447 (0.5733) | 29% |
| X/50 | 91.052 (2.3129) | 61.232 (1.1134) | 33% |

These are exploratory estimates with small iteration counts and BDN outlier removal.
For example, the X/50 99.9% confidence-interval half-widths are 14.946/7.195 us.
BDN reported 2 B/op for fluent/full X/50 and no allocation for the other cases; the source
of that isolated small allocation is not established. All standalone warmed load windows
below reported zero current-thread managed bytes. Do not claim universal zero allocation
or extrapolate these ratios to arbitrary projections.

#### Long-lived metadata exposed a real exhaustion bug

A sustained projected X/50 run with 32-bit generations failed after **2,626,151 measured
messages**, reaching generation **2,147,483,647**. Generation advances per field/transition,
not just per message. Both shared state and handle generations now use `long`; overflow
still fails closed, never resets/wraps to revive stale aliases. Boundary regressions cover
crossing the old limit and refusing 64-bit wrap. The generated metadata layout grows;
consumers must use typed state allocation rather than assume a fixed byte size.

The corrected, uninstrumented 240-second X/50 projected run completed **3,904,768 messages**
at **16,269.7 ops/s**, reaching generation **3,189,028,150**, beyond the old limit.
Sampled p50/p95/p99 were **55.9/87.8/124.7 us**, with 61,012 retained samples and zero
current-thread managed bytes during the measurement window.

Separate serial 10-second load windows:

| Scenario | Full ops/s | Projected ops/s | Full p50/p95/p99, us | Projected p50/p95/p99, us |
|----------|-----------:|----------------:|--------------------:|-------------------------:|
| Small | 649790 | 744419 | 1.4 / 2.2 / 2.7 | 1.3 / 1.8 / 2.3 |
| X/1 | 368465 | 498373 | 2.5 / 3.8 / 4.7 | 1.9 / 2.8 / 3.5 |
| X/10 | 56856 | 78657 | 16.2 / 24.9 / 43.8 | 11.8 / 17.7 / 33.7 |
| X/50 | 11432 | 16918 | 80.9 / 119.4 / 155.7 | 55.4 / 83.8 / 118.6 |
| W/1 | 319280 | 408674 | 3.0 / 4.3 / 5.3 | 2.3 / 3.2 / 4.0 |
| W/10 | 66918 | 84848 | 13.9 / 21.3 / 39.8 | 10.8 / 16.5 / 34.3 |
| W/50 | 14442 | 20656 | 63.8 / 98.6 / 141.0 | 44.5 / 70.4 / 104.2 |

The load is single-threaded, closed-loop, warmed for one second, sampling every 64th
operation into a bounded 65,536-sample rolling buffer. Percentiles use nearest rank and
describe the trailing sampled window when the buffer wraps. Fixed-interval sampling can
alias periodic behavior; these are not open-loop latency/SLA estimates. Allocation excludes
setup, warmup, sample storage and reporting. Prices cycle through 1,024 offsets.
The initial failing run had a separate 10-second managed sampled-thread-time trace;
none of the performance numbers above came from that traced run.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --combined-check
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*CombinedCodecBenchmarks*' \
  --launchCount 1 --warmupCount 3 --iterationCount 5 --buildTimeout 600
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --combined-load X50 240 projected
```

Load scenarios: `Small`, `X1`, `X10`, `X50`, `W1`, `W10`, `W50`; modes: `full`, `projected`;
duration: 1-600 seconds. `--combined-check` is also exercised in CI. The integrated suite
contains 262 passing cases, including full FIX44/FIX50SP2 generation and compilation under
a 2 GiB managed-heap cap; the ordinary-consumer net6/C#11 compatibility build also passes.

### Same-build writer pipeline investigation and in-place setters (#31)

`WriterPipelineBenchmarks` compares identical X/W frames in one binary:

- `Raw`: constant-prefix span encoding, capacity checks and envelope finalization.
- `StateAndCounts`: also initializes the same bounded metadata and enforces generation,
  poison, entry delimiters, expected counts and closure. It does **not** enforce all generated
  field-order/domain/required-scope rules, so it is a diagnostic lower bound, not a replacement API.
- `Scoped`: the generated fluent API with its complete schema checks and consuming handoffs.
- `InPlace`: the same generated API, using `Set{Field}` for optional-only scalar tails, while
  retaining required factories and consuming component/group transitions.

The initial 1/50-entry experiment showed severe variance for X/50 in-place (42.57 us mean,
15.26 us SD). It was not accepted as a speed estimate. A longer, serial 50-entry rerun used
**two launches, five warmups and ten measured iterations per launch**, with no concurrent builds:

| Message, 50 entries | Raw mean (SD), us | State/count mean (SD), us | Fluent mean (SD), us | In-place mean (SD), us |
|--------------------|------------------:|-------------------------:|--------------------:|----------------------:|
| W | 15.99 (0.204) | 20.76 (0.538) | 31.96 (0.899) | 23.37 (0.608) |
| X | 17.05 (0.334) | 22.24 (0.426) | 38.88 (0.414) | 26.96 (0.520) |

Host: EPYC 7763, Ubuntu 24.04, SDK 10.0.400, runtime 10.0.11, BDN 0.15.8, Release.
All four paths produce exactly equal bytes, including BodyLength and CheckSum, at 1/10/50
entries. No warmed managed allocation was reported. State/count safety adds about 30% to raw
encoding here; generated fluent handoffs add substantially more. In-place means are about
**27% lower for W and 31% lower for X** than fluent, while retaining generated validation.
This isolates API-layer overhead (calls/returns/copies), not a measured cost for each individual
machine instruction. In-place remains 46-58% above raw; raw omits important guarantees.

Small-message performance is not universally improved: the exploratory W/1 means were about
1.97 us fluent and 1.98 us in-place, where the fixed scope-transition cost dominates. Do not
extrapolate the 50-entry gains to every schema or frame size.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --writer-pipeline-check
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*WriterPipelineBenchmarks*50*' \
  --launchCount 2 --warmupCount 5 --iterationCount 10 --buildTimeout 600
```

The confirmation run temporarily selected only 50-entry parameters; the checked-in benchmark
also includes one-entry frames. The filter above selects the 50-entry cases.
The new setters preserve net6/C#11 and the complete FIX44/FIX50SP2 2 GiB managed-heap comparison.

### Before in-place setters: shared scoped writers (#31)

Component/group definitions now produce shared generic templates parameterized by ordinary
continuation marker structs. Marker-specific closure extensions preserve fluent parent returns
without using ref structs as generic arguments or raising the net6/C#11 floor. The real full
FIX44 and FIX50SP2 generation/compilation cases now both pass with the same **2 GiB managed
heap limit** that rejected the initial scoped candidate.

The full FIX50SP2 evidence contains 1,282 shared definition files, 1,948 shared writer ref-struct
types and 7,461,436 bytes of shared template source. The X/W principal generated files are
19,589/46,123 bytes; these small files must not be counted without the shared definitions and
continuation support they use. No dictionary reduction was used.

The runtime also shares cold validation/throw paths, consumes source handles by invalidating
their generation, and validates successful mutations once rather than in each preceding guard.
A mutation marks shared state failed and advances its generation **before** writing; only normal
return restores active status. Thus any exception still poisons all handles, without per-field
exception handlers blocking inlining. Invalid arguments on stale handles never poison the live owner.

Latest serial measurements, using the same setup and baseline described below:

| Encoding | Flat baseline, us | Initial scoped draft, us | Shared scopes mean (SD), us |
|----------|------------------:|-------------------------:|----------------------------:|
| Small NewOrderSingle, two parties | 0.453 | 1.017 | 0.967 (0.011) |
| X, 10 entries | 3.282 | 8.246 | 7.654 (0.035) |
| W, 10 entries | 3.096 | 7.508 | 6.909 (0.055) |
| X, 50 entries | 15.829 | 39.574 | 36.089 (0.143) |
| W, 50 entries | 15.037 | 33.724 | 30.083 (0.190) |

No warmed managed allocation was reported. The latest means are lower than the initial draft,
but short-run intervals overlap in some comparisons: this is not proof of a stable 5-11% gain.
Encoding remains approximately **2.0-2.33x the recorded flat baseline**, so #39 remains draft
pending the encoding-cost decision/optimization. The schema-memory blocker is resolved; the
throughput trade-off is not. Earlier baseline numbers were not rerun in this iteration.

Forcing inlining on generated optional-tail setters was also tried and rejected: its means
were 0.971/7.994/7.147/37.445/30.942 us in table order, with no observed improvement over the
retained version. Those extra generated annotations are not part of the candidate.

### Initial scoped writer candidate (#31): historical regressions

The initial candidate was **not ready for merge**. Its structural safety checks increased
complete encoding time, and full FIX50SP2 generation/compilation has a memory regression.
The memory finding below describes the initial draft, before shared templates.

Measured serially on AMD EPYC 7763 / Ubuntu 24.04, SDK 10.0.400, runtime 10.0.11,
BenchmarkDotNet 0.15.8, Release; one launch, two warmups and three measured iterations.
No implementation builds ran alongside timed workloads. The candidate includes caller-owned
metadata initialization, required inputs, optional transitions, entry/count closure and `Finish`.
X/W use the real, full FIX50SP2 dictionary, not a reduced synthetic schema.

| Encoding | Baseline mean (SD), us | Scoped mean (SD), us | Mean ratio |
|----------|----------------------:|--------------------:|-----------:|
| Small NewOrderSingle, two parties | 0.453 (0.002) | 1.017 (0.025) | 2.24x |
| X, 10 entries | 3.282 (0.023) | 8.246 (0.196) | 2.51x |
| W, 10 entries | 3.096 (0.012) | 7.508 (0.054) | 2.43x |
| X, 50 entries | 15.829 (0.147) | 39.574 (0.378) | 2.50x |
| W, 50 entries | 15.037 (0.013) | 33.724 (0.792) | 2.24x |

No managed allocation was reported in these warmed paths. These are short exploratory runs
with wide 99.9% confidence intervals, not precise production latency guarantees. The ratios
compare separately measured means; they are not paired BenchmarkDotNet baseline statistics.

The baseline is `68047d0`. For a fair W comparison, its fixture additionally calls
`writer.WriteLastUpdateTime(_expiry)` immediately after `writer.WriteSymbol("SYMBOL"u8)`.
The original flat fixture omitted that required field; the scoped API now requires it.
The table uses the rerun with this correction, not the original incomplete W workload.
Baseline and candidate X/W frames were compared byte-for-byte at 1/10/50 entries, including
BodyLength and CheckSum. Their lengths respectively are X: 214/1729/8489 and W: 234/1605/7725.

Run each version from an isolated worktree containing only one matching benchmark project;
BenchmarkDotNet's solution-root search can otherwise find other worktrees beneath `.git`.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*ReaderWriterBenchmarks.Encode_Generated' \
    '*MarketDataWriterBenchmarks.X_ScaledAndIntegral' \
    '*MarketDataWriterBenchmarks.W_ScaledAndIntegral' \
  --launchCount 1 --warmupCount 2 --iterationCount 3 --buildTimeout 600
```

Under a 2 GiB managed heap limit (`DOTNET_GCHeapHardLimit=0x80000000`), the existing full-schema
generation/compilation cases pass for both FIX44 and FIX50SP2 on the baseline. The revised
candidate passes FIX44 but exhausts memory during Roslyn parsing for FIX50SP2. Passing without
this limit does not close the resource regression. Optional-tail factoring alone is insufficient;
generated sub-scope duplication and runtime transition overhead remain blockers for #31.

### Historical small-message comparison

Captured on: AMD EPYC 7763 (WSL/Ubuntu 24.04), .NET 9.0.14, Release, `NewOrderSingle` with one
component + one 2-entry repeating group (see `ReaderWriterBenchmarks.cs` for the exact message
shape).

| Method             | Mean       | Allocated | vs. QuickFIX/n         |
|--------------------|-----------:|----------:|-------------------------|
| Decode (generated)  |   ~825 ns |      0 B | ~6.8x faster, zero-alloc |
| Decode (QuickFIX/n) | ~5,580 ns | ~10,440 B | baseline                 |
| Encode (generated)  |   ~750 ns |      0 B | ~11.4x faster, zero-alloc |
| Encode (QuickFIX/n) | ~8,570 ns | ~10,296 B | baseline                 |

Takeaways:
- The generated reader/writer allocate **zero managed bytes** in steady state for this message
  shape — confirms the allocation-minimal premise holds in practice, not just in the design intent.
- The gap vs. QuickFIX/n comes mainly from QuickFIX/n's generic, reflection/dictionary-driven
  field storage (`FieldMap`) vs. our schema-driven codegen (compile-time-known tags, no boxing).
- These numbers are indicative, not a formal SLA — re-run locally before relying on them for a
  specific capacity-planning decision, and prefer wider/varied message shapes (e.g. the full
  FIX50SP2 fixture) if optimizing for a specific real workload.

### Writer capacity contract and scoped inputs (issues #23 / #26)

Measured on 2026-09-08, AMD EPYC 7763, Ubuntu 24.04, .NET SDK 10.0.400 / runtime
10.0.11, Release. The existing `ReaderWriterBenchmarks.Encode_Generated` benchmark writes
the same NewOrderSingle frame with two group entries in both runs. Before: commit `41f62b0`;
after: the capacity checks, invalid-writer state, and scoped-input changes in this update.
No EventPipe diagnoser was enabled for this benchmark.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*ReaderWriterBenchmarks.Encode_Generated' \
  --warmupCount 3 --iterationCount 5 --launchCount 1
```

| Writer | Mean | Error (99.9% CI half-width) | StdDev | Managed allocation |
|--------|-----:|---------------------------:|-------:|-------------------:|
| Before | 659.9 ns | 39.27 ns | 10.20 ns | 0 B/op |
| Capacity contract + scoped inputs | 677.5 ns | 46.09 ns | 11.97 ns | 0 B/op |

The observed mean increased by 17.6 ns (~2.7%). Confidence intervals overlap, and these are
short runs on a shared host, so this does not establish a statistically significant regression
or a precise overhead bound. Successful writes remain allocation-free. This comparison
measures the existing whole-message workload, not isolated checks, exception costs, or X/W
market-data workloads; those require their own measurements.

### Numeric, temporal, and constant-prefix writers (issues #25 / #27 / #24)

`WriterFormattingBenchmarks.cs` adds isolated field benchmarks and complete generated
MarketDataIncrementalRefresh (X) / MarketDataSnapshotFullRefresh (W) frames, using the
**full FIX50SP2 dictionary** already checked into the test fixtures. This does not compose
FIXT transport headers: these are the generator's current FIX.5.0 envelopes, not a
claim of FIXT1.1 transport conformance or a replica of a B3 adapter.

Each frame has a message-level TradeDate and 10 or 50 entries, with an ID, price, quantity,
MDEntryDate, MDEntryTime, ExpireDate, ExpireTime (a timestamp), OrderID, and NumberOfOrders.
X additionally emits an update action and per-entry Symbol; W emits Symbol once.
Prices start at mantissa `123456700`, scale 4, and quantities at 1000, increasing per entry.
The decimal baseline constructs an exact scale-4 decimal (including conversion cost);
the new API takes mantissa/scale and integral quantity directly. Global setup compares
the entire decimal/scaled frames byte-for-byte, including BodyLength and checksum.

All measurements below: 2026-09-08, AMD EPYC 7763, Ubuntu 24.04, SDK 10.0.400 / runtime
10.0.11, Release, BenchmarkDotNet 0.15.8. **All paths measured 0 B/op.**
These are short, sequential experiments on a shared host, not capacity-planning guarantees.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*TemporalWriterBenchmarks*' '*NumericWriterBenchmarks*' '*MarketDataWriterBenchmarks*' \
  --warmupCount 3 --iterationCount 5 --launchCount 1
```

The comparison was staged so that each change has its own baseline:
A = capacity/scoped fixes + numeric overloads, original generic temporal formatter, integer tags;
B = A with direct ASCII temporal formatting;
C = B with constant prefixes and shared runtime value formatters.
The tables are measurements of those implementation stages, not simultaneous selectable modes.

**Numeric fields, stage B:** two fields per operation (PRICE and QTY).

| API | Mean | Error (99.9% CI half-width) | StdDev |
|-----|-----:|---------------------------:|-------:|
| Exact decimal conversion + decimal setters | 160.79 ns | 8.563 ns | 2.224 ns |
| Scaled mantissa + integral quantity | 47.46 ns | 1.838 ns | 0.477 ns |

The ~70% isolated reduction is not the whole-message improvement. With stage B's temporal
formatter, the numeric APIs reduce the X/W means by roughly 16-19% (table below).

**Temporal fields, A vs. B:** each operation includes a dynamic tag and one typed value.

| Field | Original mean | Direct ASCII mean | Original error | ASCII error |
|-------|--------------:|------------------:|---------------:|------------:|
| DateTime | 208.89 ns | 59.95 ns | 21.704 ns | 2.882 ns |
| DateOnly | 81.51 ns | 28.53 ns | 3.984 ns | 1.178 ns |
| TimeOnly | 176.18 ns | 33.10 ns | 4.419 ns | 1.395 ns |

**Full frames:** mean in microseconds. A and B use 3 warmups / 5 measurements; C below
uses the repeat with 5 warmups / 10 measurements, one launch each.

| Workload | Entries | A: original temporal | B: direct temporal | C: constant prefixes |
|----------|--------:|---------------------:|-------------------:|---------------------:|
| X decimal | 10 | 8.951 | 5.077 | 4.213 |
| X scaled/integral | 10 | 7.827 | 4.268 | 3.387 |
| W decimal | 10 | 8.946 | 4.703 | 3.966 |
| W scaled/integral | 10 | 7.943 | 3.952 | 3.088 |
| X decimal | 50 | 45.548 | 24.298 | 20.155 |
| X scaled/integral | 50 | 39.279 | 20.464 | 16.937 |
| W decimal | 50 | 41.777 | 23.054 | 19.356 |
| W scaled/integral | 50 | 39.045 | 18.792 | 14.971 |

Temporal changes reduce the means by roughly 43-52% when comparing the same numeric
API between A and B. This applies to this deliberately temporal-heavy message shape,
not to every dictionary/message.

**Prefix decision: adopted.** The first C run was noisy (e.g. X decimal/50 had
34.419 +/- 34.312 us); it was not used to decide. A repeat with
`--filter '*MarketDataWriterBenchmarks*' --warmupCount 5 --iterationCount 10 --launchCount 1`
produced the C means above. The relevant confidence intervals no longer overlap B:

| Workload | Entries | B error (us) | C error (us) |
|----------|--------:|-------------:|-------------:|
| X decimal | 10 | 0.3055 | 0.1110 |
| X scaled/integral | 10 | 0.1431 | 0.0498 |
| W decimal | 10 | 0.2186 | 0.0422 |
| W scaled/integral | 10 | 0.3417 | 0.0847 |
| X decimal | 50 | 1.3274 | 0.2180 |
| X scaled/integral | 50 | 1.4094 | 0.3683 |
| W decimal | 50 | 1.0084 | 0.3296 |
| W scaled/integral | 50 | 1.3055 | 0.3084 |

The observed incremental reduction is ~16-22%, with no allocation change. This supports
adoption for the measured multi-entry workloads, at a real code-size cost:

| Size metric | B: integer prefixes | C: ASCII prefixes | Change |
|-------------|--------------------:|------------------:|-------:|
| Full FIX44 generated C# (bytes) | 5,087,528 | 5,187,292 | +2.0% |
| Full FIX50SP2 generated C# (bytes) | 14,906,494 | 15,327,049 | +2.8% |
| JIT WriteX loop (bytes) | 3,085 | 4,292 | +39.1% |
| JIT WriteW loop (bytes) | 2,928 | 4,034 | +37.8% |

Generated size sums dictionary-specific `.g.cs` files (readers, writers, components, enums,
runtime), not the shared attribute definitions. Full FIX44 is built by
`tests/FixSourceGenerator.Compatibility`; FIX50SP2 by this benchmark project.
Use `-p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=<absolute-path>`
when building to inspect those outputs.

Native sizes are **individual hot loop bodies**, not the native size of the entire dictionary
or transitive callees. They were collected separately with tiering disabled, so they do not
claim to be the tiered-PGO code sizes used during timing. BenchmarkDotNet's disassembly
diagnoser returned no disassembly on this host; the runtime's JIT listing was used instead:

```bash
DOTNET_TieredCompilation=0 \
DOTNET_JitDisasm='*WriteX* *WriteW* *WriteField* *WriteValue* *WriteTagPrefix*' \
DOTNET_JitStdOutFile=/tmp/fix-writer-jit.txt \
dotnet benchmarks/FixSourceGenerator.Benchmarks/bin/Release/net10.0/FixSourceGenerator.Benchmarks.dll \
  --writer-codegen
```

The X/W bodies previously called `WriteTagPrefix(int)` repeatedly; C's listings have no
such calls in those bodies. Dynamic tags remain supported by the runtime and are still used
for the automatic envelope. Constant prefix copying and shared value formatting increase
inlining/code size; the ~16-22% measured frame improvement was judged worth that tradeoff.
No BodyLength backpatch, checksum, schema typing, or market-specific rule was changed.

### `[FixView]` selective projection vs. the full reader (issue #13)

`FixViewBenchmarks.cs` compares the full `NewOrderSingleReader` against a `[FixView]`-annotated
`OrderRoutingView` (`ClOrdID` + `Price` only) on the same wire message, both reading only those
same two fields — isolating the win from the view's early-exit scanning constructor (it stops
scanning once every requested tag has been found) rather than from "reading fewer fields."

| Method                      | Mean     | Allocated | Ratio vs. full reader |
|------------------------------|---------:|----------:|-----------------------:|
| Decode_FullReader_TwoFields  | 169.3 ns |      0 B | 1.00 (baseline)        |
| Decode_FixView_TwoFields     | 116.1 ns |      0 B | ~0.69 (~31% faster)    |

Takeaways:
- `[FixView]` is measurably faster than the full reader even on this modest 7-field message —
  the early-exit stops the scan as soon as `ClOrdID` (tag 11) and `Price` (tag 44) are both found,
  instead of scanning through `NoPartyIDs`'s repeating group to the end of the buffer.
- Both remain zero-allocation — `[FixView]` doesn't trade allocations for speed, it's a strict
  improvement for this access pattern.
- The gap should widen further on larger messages (e.g. FIX50SP2) where a view requests only a
  handful of fields out of dozens; this benchmark's `FIX44-mini` fixture is a conservative
  lower-bound demonstration, not the best case.

### `[FixView]` with a group property exposed (issue #17)

Issue #17 lets a `[FixView]` property expose a whole repeating group via the same
`{Group}GroupReader` the full reader already generates, deliberately *outside* the early-exit
scan (the group reader does its own lazy scan on access, same as the full reader's group
property). `Decode_*_PlusGroup` re-runs the same 2-field comparison but also iterates
`NoPartyIDs`, to confirm the group's presence doesn't erase the view's early-exit advantage.

| Method                                 | Mean     | Allocated | Ratio vs. matching full reader |
|-----------------------------------------|---------:|----------:|--------------------------------:|
| Decode_FullReader_TwoFields_PlusGroup   | 510.2 ns |      0 B | 1.00 (baseline)                |
| Decode_FixView_TwoFields_PlusGroup      | 429.2 ns |      0 B | ~0.84 (~16% faster)             |

Takeaways:
- The view is still faster with the group exposed (~16%), though the gap is narrower than the
  scalar-only case (~31%) — the group's own lazy scan (shared cost in both variants) now dominates
  more of the total time, diluting the relative weight of the early-exit's savings on the 2 scalar
  fields.
- Both remain zero-allocation. Exposing a group via `[FixView]` is a "free" convenience — it never
  makes the view slower than reading the same group off the full reader, since both use the exact
  same `{Group}GroupReader` and neither one locates the group eagerly.

## CPU hotspot attribution for the `PlusGroup` paths

To find out *where* the time in `Decode_FullReader_TwoFields_PlusGroup` /
`Decode_FixView_TwoFields_PlusGroup` actually goes (not just the aggregate ns/op above), both
methods were instrumented with the
[`dotnet-diagnostics-benchmarkdotnet`](https://github.com/pedrosakuma/dotnet-diagnostics/blob/main/src/DotnetDiagnostics.BenchmarkDotNet/README.md)
`IDiagnoser` (`[DotnetDiagnosticsDiagnoser]` + `[DiagnosticKind(BenchmarkDiagnosticKind.Cpu,
DurationSeconds = 8)]`), which attaches an EventPipe CPU sampler to the benchmark's child process
and reports per-stack-frame exclusive/inclusive sample counts.

> **Note:** this package targets `net10.0` only, which is why this project now targets net10.0
> (see "How it's wired" above) even though `[FixView]` itself only requires net9+ as a floor. The
> package isn't published to nuget.org/GitHub Packages — see `nuget.config` and the CI workflow's
> "Fetch dotnet-diagnostics packages" step for how it's resolved from GitHub Releases. Per the
> tool's own guidance, treat timings from a `[DotnetDiagnosticsDiagnoser]`-enabled run as
> diagnostic-only (EventPipe collection adds overhead) — the numbers above, from clean
> `MemoryDiagnoser`/`SimpleJob` runs, remain the canonical ones.

Findings (8s CPU sample of each method, ~6,100-6,300 samples captured):

| Frame (exclusive samples)                          | FullReader_PlusGroup | FixView_PlusGroup |
|-----------------------------------------------------|----------------------:|--------------------:|
| `Decimal.DecCalc.DecAddSub` (parsing `Price`)        | 397 (6.5%)            | 453 (7.2%)           |
| `Number.TryNumberToDecimal`                          | 58 (0.9%)              | 79 (1.3%)            |
| `Utf8Parser.TryParseInt32D`                           | 42 (0.7%)              | 58 (0.9%)            |
| `SpanHelpers.Memmove`                                 | 23 (0.4%)              | 27 (0.4%)            |

The rest (~90%) of samples land inline in the benchmark method itself — the JIT inlines the
tag/value scanning and group iteration into the benchmark body, so the sampler can't separate
"group iteration cost" from "field scanning cost" at the frame level; both are fused into one leaf
frame in both variants.

Takeaway: the dominant *identifiable* cost in both paths is decimal parsing of the `Price` field
(`DecCalc.DecAddSub`), not the group iteration — and it's present in near-identical proportion in
both the full reader and the view. This confirms the doc comment on `Decode_FixView_TwoFields_PlusGroup`:
exposing a group via `[FixView]` doesn't add its own distinguishable overhead relative to the full
reader's group property, since both share the same `{Group}GroupReader` lazy-scan implementation.

## Investigated and rejected: `IndexOf`-based (SIMD) field scanning

An attempt was made to replace `FixSpanReader.TryReadField`'s manual byte-by-byte scan (for the
`'='` and SOH delimiters) with `ReadOnlySpan<byte>.IndexOf` (vectorized) + `Utf8Parser.TryParse`
for the tag number. Measured **~50% *slower*** for this message shape (825 ns → 1,273 ns decode) —
the fixed setup cost of `IndexOf`/`Utf8Parser` outweighs the SIMD win for the short tag/value spans
typical of FIX messages. Kept the manual scalar loop. If revisiting this, benchmark against a
message shape with many long-valued fields (where per-field span lengths are large enough for
vectorization to pay off) before trying again.

## Full FIX50SP2 reader: group membership lookup

`MarketDataReaderBenchmarks` uses preencoded X/W frames from the writer fixtures, with 10/50
entries. `SliceX`/`SliceW` traverse the generated group enumerators without constructing entry
readers. `DecodeX`/`DecodeW` also read the selected identifiers, enums, prices, quantities, dates,
times, and symbols, consuming values into an aggregate. Frame encoding and setup allocations
are outside the measurements. This is synthetic decoding, not transport or malformed-frame
validation, and does not exercise every optional dictionary field.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataReaderBenchmarks*' '*ReaderWriterBenchmarks.Decode_Generated' \
  --warmupCount 3 --iterationCount 5 --launchCount 1
```

The original runtime linearly searched the flattened membership tags for every field encountered
while bounding an entry. The full dictionary contains 4,033 possible tags for X's `MDIncGrp`
entries versus 138 for W's `MDFullGrp`; these are schema possibilities, not fields present in
each encoded entry.

Generated membership arrays are now sorted at generation time. The generated-only runtime path
uses binary search above 16 tags, retaining linear lookup for small sets. The public four-argument
`FixGroupEnumerator` constructor continues to support arbitrary, unsorted tag lists. Delimiters
are still derived from schema order before sorting, and membership remains specific to each group.

Before/after means on .NET 10.0.11, SDK 10.0.400, Linux x64, AMD EPYC 7763, BenchmarkDotNet
0.15.8; one launch, three warmups, five measured iterations, without EventPipe CPU profiling:

| Operation | Entries | Linear membership | Generated sorted membership |
|---|---:|---:|---:|
| Slice X | 10 | 113.845 us | 2.467 us |
| Slice W | 10 | 3.256 us | 1.755 us |
| Decode X | 10 | 243.915 us | 18.320 us |
| Decode W | 10 | 17.952 us | 15.641 us |
| Slice X | 50 | 570.236 us | 11.963 us |
| Slice W | 50 | 15.203 us | 7.680 us |
| Decode X | 50 | 1,203.577 us | 87.587 us |
| Decode W | 50 | 85.685 us | 76.204 us |
| Small NewOrderSingle decode | - | 530.3 ns | 520.3 ns |

All measured paths reported zero managed allocations per operation. X decoding with 50 entries
improved about 13.7x; the small-message difference is within measurement variability. These are
end-to-end comparisons of the change, not additive stage accounting: JIT inlining/code shape can
change across paths. Readers still rescan entry/component spans, and property accesses still
parse located values on demand.

### Follow-up: binary search vs. HashSet vs. bitmap

Compared the actual generated readers in isolated source copies, not a standalone lookup loop
or a hand-written decoder. The benchmark methods, frames, field access, scanner, delimiter rules,
and small-set linear fallback (up to 16 tags) were unchanged. Only the generated membership
metadata and runtime membership path differed:

- Binary search: the implementation above.
- HashSet: one static `HashSet<int>` per generated group type, built from its tags at type
  initialization, then queried with `Contains`. No set construction per message/entry.
- Bitmap: generated constant `ulong[]` words, queried with a bounds check and
  `(bits[tag >> 6] & (1UL << (tag & 63))) != 0`.

Metadata initialization and fixture encoding were outside the timed region. Both experimental
copies retained the original integer arrays as well; this experiment does not measure startup
allocation or total retained metadata memory. The variants passed the X/W fixture correctness
matrix for both numeric writer APIs at 1/10/50 entries, plus membership comparison against the
original tag sets including holes and out-of-range tags.

First pass used the same 1-launch/3-warmup/5-iteration settings as above:

| Operation | Entries | Binary search | HashSet | Bitmap |
|---|---:|---:|---:|---:|
| Slice X | 10 | 2.467 us | 1.908 us | 1.446 us |
| Slice W | 10 | 1.755 us | 1.761 us | 1.154 us |
| Decode X | 10 | 18.320 us | 20.215 us | 17.273 us |
| Decode W | 10 | 15.641 us | 16.800 us | 15.055 us |
| Slice X | 50 | 11.963 us | 8.336 us | 6.177 us |
| Slice W | 50 | 7.680 us | 7.678 us | 5.756 us |
| Decode X | 50 | 87.587 us | 88.901 us | 85.098 us |
| Decode W | 50 | 76.204 us | 74.380 us | 70.303 us |
| Small NewOrderSingle decode | - | 520.3 ns | 553.8 ns | 568.8 ns |

Repeated all three implementations at 50 entries, sequentially on the same host/runtime, with
two process launches, five warmups and ten measured iterations per launch:

```bash
# Run from each source copy's root so BenchmarkDotNet builds that copy's generator.
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataReaderBenchmarks*50*' \
  --warmupCount 5 --iterationCount 10 --launchCount 2
```

| Operation, 50 entries | Binary search mean (SD) | HashSet mean (SD) | Bitmap mean (SD) |
|---|---:|---:|---:|
| Slice X | 14.21 (1.75) us | 8.20 (0.23) us | 6.29 (0.31) us |
| Slice W | 10.26 (0.64) us | 7.28 (0.86) us | 5.31 (0.11) us |
| Decode X | 94.67 (5.34) us | 105.98 (18.21) us | 83.71 (1.76) us |
| Decode W | 76.82 (2.55) us | 70.19 (1.86) us | 71.89 (1.77) us |

All paths reported zero managed allocations per operation. This is a shared host, not a
controlled performance lab: BenchmarkDotNet flagged multimodal distributions for binary Slice X /
Decode X and HashSet Decode X in the repeat. Do not interpret the unstable HashSet Decode X mean
as a proven regression, or small differences as a universal ranking. The small-message control
also varied between runs.

HashSet consistently reduced X delimitation time versus binary search, but did not demonstrate
a consistent full-X decoding improvement. Bitmap had the lowest delimitation means for both
groups in both passes. Full decoding improvements were much smaller, with other work remaining
in scanning, reader construction and value conversion; bitmap did not beat HashSet on every
full-decoding measurement. The production implementation remains binary search pending an
adoption decision; the alternatives were measured only in isolated experimental copies.

A bitmap's payload alone is 5,392 bytes for X and 376 bytes for W, versus 16,132/552 bytes for
the respective integer arrays, excluding object overhead. These are per-group-type sizes, not
per-message costs. A production bitmap implementation would need a bounded-memory fallback for
custom schemas with very high sparse tag numbers instead of allocating up to every maximum tag.

## Field order and the reading process

`MarketDataOrderBenchmarks` keeps the production binary-search membership metadata unchanged
and varies the order of fields inside each X/W entry. The entry delimiter stays first, entries
stay in their original order, and values, byte lengths and checksum are preserved. These
synthetic permutations are parser experiments, not a claim that arbitrary group field order is
canonical FIX or accepted by other engines. Ascending tag number is not schema order.

The four cases are the writer's current order, ascending numeric tags after the delimiter,
one fixed shuffled order repeated across entries, and a different shuffled order for each entry.
Shuffles are seeded and reproducible; the same prebuilt frame is reused across measured calls.
Permutation construction is outside the timed region: results do not include sorting at runtime.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --reader-order-check
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataOrderBenchmarks*' \
  --warmupCount 3 --iterationCount 5 --launchCount 1
```

In addition to slicing and the existing generated decoding workload, a fixture-specific
`ProjectSinglePass` prototype traverses the frame once, dispatching on tags and parsing values
immediately. It consumes the same selected values into an equivalent aggregate and checks the
entry count, using the same runtime value parsers. It does not build reusable message/entry/
component readers or perform dictionary membership lookup. It is **not** a general replacement:
it does not implement arbitrary nested groups, the generated API's field-presence semantics,
duplicate-field behavior, or schema validation. Its narrower projection contract is intentional.
It also does not assume ascending or known field order; this is not an ordered-only parser.

Setup compares both reading paths to the original generated-reader aggregate. The check command
exercises all permutations at 1/10/50 entries and checks group boundaries/counts and checksum.
The measured cases use 50 entries. Same host/runtime as above, one launch, three warmups and
five measured iterations; mean microseconds per frame:

| Entry field order | Slice X | Slice W | Generated X | Generated W | Single-pass X | Single-pass W |
|---|---:|---:|---:|---:|---:|---:|
| Writer | 12.656 | 7.567 | 92.952 | 74.405 | 48.008 | 47.676 |
| Tag ascending | 11.924 | 7.674 | 82.252 | 70.904 | 48.340 | 46.163 |
| Fixed shuffle | 10.830 | 7.279 | 83.761 | 69.562 | 49.787 | 46.422 |
| Per-entry shuffle | 14.854 | 9.594 | 91.575 | 74.571 | 50.588 | 48.190 |

All cases reported zero managed allocations per operation. Ordering affected the existing
reader, but increasing numeric order was not uniformly best: fixed shuffle was close for full
decoding and faster in slicing. Per-entry shuffle versus fixed shuffle increased generated
decoding means by about 9% for X and 7% for W, and slicing by about 37%/32%. Stable order matters
in these fixtures independently of numeric sorting. Branch/cache effects are plausible, but no
hardware counters were collected to attribute the differences; shared-host noise still applies.

The specialized single-pass projection had a larger difference than reordering alone: about
48/48 us versus 93/74 us for generated X/W in writer order. This points to the process
(repeated scans, reader initialization, deferred parsing and API contract), not just membership
data structures, as useful optimization territory. It does not establish how much of that
difference a general-purpose reader can recover while preserving its complete behavior.

## Process decomposition: entry scopes and value conversion

`MarketDataProcessBenchmarks` separates additional parts of the selected-field workload:

- `DecodeGenerated`: unchanged generated message/component/entry readers.
- `DecodeGeneratedNoTemporal`: the same reader types and non-temporal field access, without
  accessing date/time properties. Temporal fields are still present in the scanned frame.
- `ProjectGroupScoped`: retains generated top-level readers, group counters and the exact
  runtime group enumerator with the group's sorted metadata, then projects each bounded entry
  in one pass instead of constructing the full entry/component readers.
- `ProjectGroupScopedNoTemporal`: the same bounded-entry projection without date/time parsing.
- `ProjectFrameSinglePass` / `ProjectFrameNoTemporal`: the narrower frame-wide projection from
  the ordering experiment, with and without temporal conversion.
- `ParseTemporalLocated` / `ParseNonTemporalLocated`: parse value spans located during setup.
  These omit field scanning and grouping, but still include loop/dispatch/aggregation overhead.

The per-entry projection keeps the existing boundary algorithm, unlike the frame-wide prototype.
It is still fixture-specific and does not implement the complete generated field-access API,
arbitrary nested-group semantics, field-presence behavior, or duplicate-field behavior.

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --reader-process-check
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataProcessBenchmarks*' \
  --warmupCount 3 --iterationCount 5 --launchCount 1 --buildTimeout 300
```

The check command compares projections and aggregate decomposition at 1/10/50 entries, for
both X/W and all four field-order permutations. Measurements use writer order and 50 entries,
with 201 temporal conversions per frame: one TradeDate plus two dates, one time and one
timestamp per entry. Setup and located-value arrays are outside the measurements.

Initial means, same host/runtime as the prior experiments (microseconds per frame):

| Path | X | W |
|---|---:|---:|
| Generated | 104.719 | 78.798 |
| Generated, no temporal parsing | 72.278 | 35.935 |
| Group-scoped projection | 71.177 | 75.472 |
| Group-scoped projection, no temporal parsing | 34.513 | 33.803 |
| Frame-wide projection | 50.047 | 52.504 |
| Frame-wide projection, no temporal parsing | 13.946 | 11.635 |
| Located temporal values | 34.277 | 34.486 |
| Located non-temporal values | 9.323 | 7.539 |

All paths reported zero managed allocations per operation. Generated X timings were noisy:
standard deviations were 10.963 us for full decoding and 24.117 us without temporal parsing.
Do not subtract these independent means to claim an exact CPU breakdown. Omitting getters can
also alter JIT code shape and dead-store elimination; these are workload comparisons, not a
profiler's accounting of exclusive costs.

Inspecting the generated backing fields also explains why a sparse projection may help X:
its entry reader locates 98 direct fields, W's locates 88, and `InstrumentReader` locates 154.
The fixture accesses only Symbol through Instrument, but X constructs that component reader
for every entry, while W accesses it once at message level. These counts are distinct from the
4,033/138 transitive membership tags used for boundary detection.

### Isolated temporal ASCII fast-path experiment

**Implementation status:** #30 promotes this temporal fast path into the generated runtime.
The investigation below describes the original isolated experiment against prework baseline
`a0b1aab`, not a claim that the current runtime still uses only the original parser. Its
historical timings are not new measurements of the integrated change.

An isolated source copy adds a checked ASCII fast path to the existing temporal runtime parsers:
`yyyyMMdd`, `HH:mm:ss[.fff]`, and `yyyyMMdd-HH:mm:ss[.fff]`. It checks digits, separators, calendar
day validity and time ranges before constructing the value; timestamps retain UTC Kind.
Anything not accepted by the fast path goes through the **unchanged original TryParseExact
path**. The generated reader shapes, membership lookup and benchmark methods are unchanged.
This prototype is not applied to the production generator.

The experimental parser was compared with the original framework parsing contract on 75,293
inputs: random valid dates/times, year/month/day boundaries, leap/century years, invalid values,
truncation, and all byte substitutions at each position of representative valid formats.
Comparison includes success/failure, output values and DateTime Kind. X/W fixture and projection
matrices also ran against the experimental runtime. This is .NET 10 evidence, not an exhaustive
proof for every consumer runtime or input.

Repeated selected BenchmarkDotNet paths on both implementations with two launches, five warmups
and ten measured iterations per launch (`--buildTimeout 300` accommodates the full dictionary):

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataProcessBenchmarks.DecodeGenerated*' \
    '*MarketDataProcessBenchmarks.ProjectGroupScoped(*' \
    '*MarketDataProcessBenchmarks.ParseTemporalLocated*' \
  --warmupCount 5 --iterationCount 10 --launchCount 2 --buildTimeout 300
```

Means (standard deviations), microseconds per 50-entry frame:

| Path | X original | X fast path | W original | W fast path |
|---|---:|---:|---:|---:|
| Generated | 88.34 (1.63) | 71.99 (6.49) | 77.81 (5.01) | 56.51 (5.93) |
| Generated, no temporal parsing | 48.16 (1.01) | 73.55 (13.86) | 54.18 (21.90) | 45.69 (5.01) |
| Group-scoped projection | 70.20 (1.65) | 43.30 (8.33) | 104.92 (44.09) | 40.08 (1.95) |
| Located temporal values | 35.23 (0.80) | 5.25 (0.33) | 35.93 (1.71) | 5.54 (0.40) |

The no-temporal control and several other paths fluctuated substantially across separate
processes/runs; BenchmarkDotNet also flagged multimodal distributions. That prevents treating
the full-decoding mean differences as precise causal percentages.

To reduce temporal drift, a supplementary load hosted the original and experimental assemblies
in separate AssemblyLoadContexts in **one process/thread**. It warmed each workload, then
alternated eight 250 ms blocks per implementation and case, reversing their order every round.
Reflection/setup were outside timing; timed calls used bound delegates. This is a paired load,
not another BenchmarkDotNet result. Block medians in microseconds per 50-entry frame:

| Path | X original | X fast path | W original | W fast path |
|---|---:|---:|---:|---:|
| Generated | 89.70 | 58.81 | 73.98 | 43.92 |
| Generated, no temporal parsing | 51.85 | 48.97 | 37.52 | 36.04 |
| Group-scoped projection | 70.40 | 38.10 | 71.37 | 39.55 |
| Located temporal values | 37.24 | 5.09 | 34.78 | 5.14 |

Both variants allocated zero bytes on the load thread during measured blocks, and the BDN cases
also reported zero allocations per operation. The generated full-decoding block ranges were
84.01-97.98 us vs. 55.74-70.85 us for X and 70.11-90.19 us vs. 40.62-49.64 us for W.
The no-temporal controls were much closer in this interleaved run, supporting temporal parsing
as a genuine optimization opportunity rather than attributing everything to membership lookup
or reader construction. These are still shared-host measurements, not latency guarantees.

Two separate opportunities emerge: a common-format temporal fast path that can retain the
existing reader API, and scoped selective entry/component readers that avoid locating many
unused fields. The latter has a narrower prototype contract and requires more design work to
generalize; neither experiment changes the production runtime in this investigation.

### Integrated temporal implementation (#30)

The 51 permanent temporal-reader cases also ran on .NET 6.0.36, 8.0.26, 9.0.14 and
10.0.11, comparing generated parsing with each executing framework's parser. This includes
failure defaults, UTC Kind and common-path allocation assertions; it is not exhaustive
coverage of every input or runtime release.

The opt-in test-project mode excludes QuickFIX/n interoperability dependencies (which require
.NET 8+) and links only the existing temporal cases and generation helpers. Its binaries and
intermediates are isolated from the normal suite; without the property, the original .NET 9
test project is unchanged. Install the corresponding runtimes and run:

```bash
for framework in net6.0 net8.0 net9.0 net10.0; do
  dotnet test tests/FixSourceGenerator.Tests/FixSourceGenerator.Tests.csproj \
    -c Release -p:TemporalRuntimeTargetFramework="$framework" \
    --filter FullyQualifiedName~TemporalReaderTests
done
```

CI runs the same matrix, including for the stacked prework PR. The local .NET 6 run used
an isolated runtime with `-- RunConfiguration.DotNetHostPath=/path/to/net6/dotnet`;
compiling the net6/C#11 compatibility consumer remains a separate check.

The interleaved load was rerun against the actual implementation commit `b97be92` and prework
baseline `a0b1aab`, with other local agents no longer building. Same .NET 10 host and paired-load
protocol: one thread, eight alternating 250 ms blocks per implementation/case after warmup.
Medians in microseconds per 50-entry frame:

| Path | X baseline | X integrated | W baseline | W integrated |
|---|---:|---:|---:|---:|
| Generated | 85.77 | 56.18 | 69.14 | 41.19 |
| Generated, no temporal parsing | 48.85 | 47.84 | 32.85 | 32.85 |
| Group-scoped projection | 68.05 | 37.19 | 67.69 | 34.96 |
| Located temporal values | 34.03 | 4.92 | 33.85 | 4.82 |

Both variants recorded zero allocated bytes on the measured thread. Full-decoding block
ranges were 83.64-92.73 us versus 51.70-57.62 us for X and 68.44-71.20 us versus
39.34-43.84 us for W. The no-temporal controls remained close. This supports the integrated
parser improvement, but remains a shared-host paired load, not a new BenchmarkDotNet run
or a production latency guarantee. Group-scoped projection remains a narrower prototype.

### Generated entry/component projections (#32)

Measured on 2026-09-09 after both implementation agents stopped building: same shared
EPYC 7763/Ubuntu 24.04 host, SDK 10.0.400, runtime 10.0.11, BDN 0.15.8, Release.
One launch, two warmups, three measured iterations. These are short exploratory runs,
not release latency guarantees: the 99.9% error intervals are wide with only three samples.

The full and projected methods run in the **same candidate build**, with the #30 temporal
parser, identical X/W frame bytes and selected-field digests. This is not a before/after
comparison against an unmodified old assembly. Envelope/network validation is not timed.
The projections here are generated FixViews, not the earlier handwritten process prototypes.

Means (sample standard deviation), microseconds per frame:

| Message | Entries | Full reader | Generated projection |
|---|---:|---:|---:|
| X | 1 | 1.391 (0.037) | 0.893 (0.031) |
| W | 1 | 1.497 (0.037) | 0.771 (0.037) |
| X | 10 | 12.629 (0.750) | 7.688 (0.186) |
| W | 10 | 8.561 (0.123) | 5.665 (0.063) |
| X | 50 | 56.043 (1.136) | 37.759 (1.731) |
| W | 50 | 39.326 (1.907) | 25.486 (0.351) |

No managed allocation was reported by MemoryDiagnoser for these warmed paths. At 50 entries,
the mean reductions were about 33% for X and 35% for W, subject to the uncertainty above.
For example, the 99.9% half-widths for X/50 were 20.717 us full and 31.580 us projected;
for W/50 they were 34.785 us and 6.396 us.

The small-message two-field benchmark measured 160.5 ns (3.16 ns SD) for the full reader
and 112.6 ns (3.03 ns SD) for FixView, with no reported allocation. Reading price/size twice
per X entry versus caching their first conversions at the call site measured:

| Entries | Repeated getters, us | Cached local conversions, us |
|---:|---:|---:|
| 1 | 0.569 (0.017) | 0.514 (0.003) |
| 10 | 5.242 (0.161) | 4.405 (0.046) |
| 50 | 27.266 (0.691) | 22.608 (0.339) |

Both access cases use the same lazy generated constructor. This measures call-site caching,
**not an alternative eagerly converting generated constructor**. It does not justify changing
the default conversion/storage strategy for all consumers.

Generated state/code inspection before helper sharing (`e8c137f`) exposed this trade-off:

| Type | Instance fields | Constructor IL bytes | Declared method IL bytes |
|---|---:|---:|---:|
| X projection | 36 | 3,515 | 96,557 |
| W projection | 30 | 1,225 | 1,778 |
| X two-field projection | 7 | 2,773 | 96,006 |
| X full entry reader | 294 | 5,780 | 6,807 |
| W full entry reader | 264 | 5,212 | 6,008 |

Field counts include the source span, not byte-sized instance-layout measurements. Method IL
excludes constructors and nested types; none of these columns represents native/JIT code size.
Those large X method totals included recursively generated group-boundary helpers duplicated
per view. Fewer cached fields do **not** imply less generated code.

### Shared helper update (#32)

Views now call one `Runtime.FixViewGroupSkipHelpers` container per runtime namespace. Helpers
are keyed by resolved group-definition identity, not short names, and retain contextual boundary
arguments. Using the runtime namespace also avoids colliding with FIX message/component names.

| Type | Previous declared method IL | Shared-helper version |
|------|----------------------------:|----------------------:|
| X projection | 96,557 bytes | 665 bytes |
| W projection | 1,778 bytes | 548 bytes |
| X two-field projection | 96,006 bytes | 114 bytes |

Field counts and constructor IL are unchanged. **The helper code has not disappeared:** the
fixture's schema-wide container has 339 methods / **104,814 IL bytes**, shared across its
header and entry projections. Ordinary reader boundary helpers still have their own schema-level
copy; the two incremental pipelines have not been combined. These are IL sizes, not native code.

A serial rerun on the same host/runtime and short-run settings above produced:

| Message | Entries | Full decode mean (SD), us | Projected mean (SD), us |
|---------|--------:|--------------------------:|-----------------------:|
| X | 1 | 1.318 (0.023) | 0.828 (0.021) |
| W | 1 | 1.193 (0.024) | 0.760 (0.001) |
| X | 10 | 11.285 (0.340) | 6.937 (0.108) |
| W | 10 | 8.332 (0.326) | 5.346 (0.072) |
| X | 50 | 54.333 (0.133) | 33.755 (0.082) |
| W | 50 | 37.962 (0.862) | 24.737 (0.701) |

Digests agree and no warmed managed allocation was reported. Projected means remain below full
decode for these fixtures; three measured iterations and wide confidence intervals do not
establish precise production speedups or an independent speedup caused by helper sharing.

Run from the relevant worktree root, not its parent repository containing other worktrees
(otherwise BDN can reject multiple identically named project files):

```bash
dotnet run --no-build -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --reader-fixview-check
dotnet run --no-build -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --reader-fixview-code-size
dotnet run --no-build -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataReaderBenchmarks.DecodeX' '*MarketDataReaderBenchmarks.DecodeXProjected' \
           '*MarketDataReaderBenchmarks.DecodeW' '*MarketDataReaderBenchmarks.DecodeWProjected' \
  --warmupCount 2 --iterationCount 3 --launchCount 1 --buildTimeout 300
dotnet run --no-build -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- \
  --filter '*MarketDataReaderBenchmarks.DecodeXProjectedRepeatedGetters' \
           '*MarketDataReaderBenchmarks.DecodeXProjectedCachedConversions' \
           '*FixViewBenchmarks.Decode_FullReader_TwoFields' '*FixViewBenchmarks.Decode_FixView_TwoFields' \
  --warmupCount 2 --iterationCount 3 --launchCount 1 --buildTimeout 300
```

Build first after source changes. Confirm the report contains actual results: BDN can exit
successfully after a boilerplate build failure and report `NA` with zero benchmarks executed.
