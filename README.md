# FIX Source Generator

A Roslyn-based source generator that converts FIX tag=value DataDictionary XML schemas
(the classic QuickFIX/QuickFIX-J/QuickFIX-n format) into allocation-minimal C# reader/writer
types, in the spirit of [SbeSourceGenerator](https://github.com/pedrosakuma/SbeSourceGenerator)
but for the tag=value wire format instead of Simple Binary Encoding.

See [`docs/CONTRACT.md`](docs/CONTRACT.md) for the full design contract (input schema shape,
C# output shape, type mapping, versioning, diagnostics) and [`docs/USAGE.md`](docs/USAGE.md) for
a getting-started guide, a worked example, and the schema-versioning guide. The tracking issue
[#1](https://github.com/pedrosakuma/FixSourceGenerator/issues/1) has the overall roadmap.

**Unreleased API:** scoped writers intentionally break the flat writer API from 0.1.0.
See [the migration guide](docs/MIGRATION.md). No new package version is implied by this branch.
Ordinary consumers require net6+/C#11; optional `[FixView]` projections require net9+/C#13.

## Design highlights

- **Allocation-minimal by default.** The generated API is a pair of `ref struct` reader/writer
  types over `Span<byte>` / `ReadOnlySpan<byte>` (analogous to `System.Text.Json.Utf8JsonReader`/
  `Utf8JsonWriter`), not heap-allocated DTOs. Strings are exposed as spans by default and only
  materialize a `string` when the consumer explicitly asks for one.
- **Repeating groups without materialization**, matching the same principle used by
  SbeSourceGenerator: groups are exposed via `foreach`-style enumerators over the underlying
  buffer, not `List<T>`.
- **Decode and encode.** The generator emits both a reader (parses a buffer into typed field
  access) and a writer (writes fields directly into a caller-supplied buffer, computing
  `BodyLength`/`CheckSum` via backpatch).
- **Required inputs belong to their scope.** Constructors/factories require the next required
  scalar run; component/group transitions preserve wire order. Groups use an upfront expected
  count, and failed mutations invalidate the shared writer owner.
- **Selective reading.** `[FixView]` supports messages, components and qualified group entries;
  `CurrentSpan` feeds a projection without constructing the complete entry reader.
- **Schema-driven, zero-lookup groups.** Group delimiter tags are known at compile time from the
  schema and embedded as constants in the generated code — no runtime dictionary lookup is
  needed to find group boundaries.
- **Namespace-isolated versioning**, so multiple FIX dictionary versions (e.g. 4.2 and 4.4) can
  coexist in the same consumer project.

## Quick start

1. Run the [compiled example](examples/ScopedCodec), which uses the current source generator:

   ```bash
   dotnet restore examples/ScopedCodec --source https://api.nuget.org/v3/index.json
   dotnet run --no-restore -c Release -f net6.0 --project examples/ScopedCodec
   dotnet run --no-restore -c Release -f net9.0 --project examples/ScopedCodec
   ```

2. Add your dictionary as `AdditionalFiles` and reference the generator as an analyzer, as in
   the [example project](examples/ScopedCodec/ScopedCodec.csproj).
   The generator produces a reader/writer per message in a namespace derived from your
   project's `RootNamespace` (or the `FixGeneratorNamespace` property) plus a version token, e.g.
   `Acme.Fix.V44.NewOrderSingleReader` / `...NewOrderSingleWriter`.

3. Decode:

   ```csharp
   using Acme.Fix.V44;
   using System.Text;

   var reader = new NewOrderSingleReader(buffer); // ReadOnlySpan<byte>
   string clOrdId = Encoding.ASCII.GetString(reader.ClOrdID);
   decimal? price = reader.Price;      // T? for optional value fields
   foreach (var party in reader.NoPartyIDs) // groups are enumerated, never materialized
   {
       int? role = party.PartyRole;
   }
   ```

4. Encode:

   ```csharp
   using System;
   using Acme.Fix.V44;
   using Acme.Fix.V44.Runtime;

   Span<byte> destination = stackalloc byte[512];
   Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
   NewOrderSingleWriter.InitializeState(state);
   var message = new NewOrderSingleWriter(destination, state, "SENDER"u8, "TARGET"u8, 7,
       new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc), "ORD-1"u8);
   var instrument = message.BeginInstrument("MSFT"u8);
   var tail = instrument.SkipSecurityID().EndInstrument(Side.Buy, 100m, OrdType.Limit);
   tail.SetPrice(101.25m);
   int length = tail.SkipNoPartyIDs().Finish();
   ```

These snippets use the example's [mini dictionary](tests/FixSourceGenerator.Tests/TestData/FIX44-mini.xml),
not the full FIX44 schema. Your dictionary determines the exact required arguments and phases.

Optional-only tails support in-place `Set{Field}` calls: the current handle remains valid and
older copies become stale. Fluent `Write`/`Skip` and scope transitions still consume their source.
Omitted fields are distinct from zero/false, and explicit empty text is rejected by generated
writers. Keep input buffers alive and unchanged while readers/views are in use.

Measured trade-offs, including small messages, combined codec loads, metadata and uncertainty,
are recorded in the [benchmark results](benchmarks/FixSourceGenerator.Benchmarks/README.md);
these are not transport latency guarantees.

See [`docs/USAGE.md`](docs/USAGE.md) for the worked component/group example and guidance on
versioning schemas over time.

## Repository layout

- `src/FixSourceGenerator` — the Roslyn incremental source generator (`netstandard2.0`).
  - `Schema/` — the parsed/resolved schema model (`SchemaReader` + `FixDictionary` and friends).
  - `Generators/` — codegen for readers, writers, enums, components, and the embedded runtime.
  - `Diff/` — `SchemaDiffer`, for comparing two dictionary versions and flagging breaking changes.
- `tests/FixSourceGenerator.Tests` — unit, generator-driver, and real-schema conformance tests.
- `tests/FixSourceGenerator.Compatibility` — build-only .NET 6 / C# 11 consumer, including
  stack-based writer inputs; built by the solution's CI build.
- `benchmarks/FixSourceGenerator.Benchmarks` — BenchmarkDotNet CPU/allocation benchmarks for the
  generated reader/writer (see the benchmarks project's own README for how to run them and the
  latest recorded numbers).
- `docs/CONTRACT.md` — the normative design contract for input schema and generated output.
- `docs/USAGE.md` — getting-started guide, worked example, and schema-versioning guide.
- `docs/MIGRATION.md` — breaking API changes, ownership rules and integration evidence.
- `examples/ScopedCodec` — runnable net6/C#11 codec and net9/C#13 projected consumer.
- `CHANGELOG.md` — release history.

## License

MIT — see [`LICENSE.txt`](LICENSE.txt).
