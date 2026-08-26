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

## Investigated and rejected: `IndexOf`-based (SIMD) field scanning

An attempt was made to replace `FixSpanReader.TryReadField`'s manual byte-by-byte scan (for the
`'='` and SOH delimiters) with `ReadOnlySpan<byte>.IndexOf` (vectorized) + `Utf8Parser.TryParse`
for the tag number. Measured **~50% *slower*** for this message shape (825 ns → 1,273 ns decode) —
the fixed setup cost of `IndexOf`/`Utf8Parser` outweighs the SIMD win for the short tag/value spans
typical of FIX messages. Kept the manual scalar loop. If revisiting this, benchmark against a
message shape with many long-valued fields (where per-field span lengths are large enough for
vectorization to pay off) before trying again.
