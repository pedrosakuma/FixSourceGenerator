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

## Running

```bash
dotnet run -c Release --project benchmarks/FixSourceGenerator.Benchmarks -- --filter "*"
```

Always run with `-c Release` — BenchmarkDotNet refuses to run a Debug build. Use `--filter` to
narrow to specific benchmarks (e.g. `--filter "*Generated*"` to skip the QuickFIX/n baseline,
which is much slower and dominates total run time).

## Latest recorded numbers

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

## Investigated and rejected: `IndexOf`-based (SIMD) field scanning

An attempt was made to replace `FixSpanReader.TryReadField`'s manual byte-by-byte scan (for the
`'='` and SOH delimiters) with `ReadOnlySpan<byte>.IndexOf` (vectorized) + `Utf8Parser.TryParse`
for the tag number. Measured **~50% *slower*** for this message shape (825 ns → 1,273 ns decode) —
the fixed setup cost of `IndexOf`/`Utf8Parser` outweighs the SIMD win for the short tag/value spans
typical of FIX messages. Kept the manual scalar loop. If revisiting this, benchmark against a
message shape with many long-valued fields (where per-field span lengths are large enough for
vectorization to pay off) before trying again.
