# FIX Source Generator

A Roslyn-based source generator that converts FIX tag=value DataDictionary XML schemas
(the classic QuickFIX/QuickFIX-J/QuickFIX-n format) into allocation-minimal C# reader/writer
types, in the spirit of [SbeSourceGenerator](https://github.com/pedrosakuma/SbeSourceGenerator)
but for the tag=value wire format instead of Simple Binary Encoding.

See [`docs/CONTRACT.md`](docs/CONTRACT.md) for the full design contract (input schema shape,
C# output shape, type mapping, versioning, diagnostics) and [`docs/USAGE.md`](docs/USAGE.md) for
a getting-started guide, a worked example, and the schema-versioning guide. The tracking issue
[#1](https://github.com/pedrosakuma/FixSourceGenerator/issues/1) has the overall roadmap.

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
- **Schema-driven, zero-lookup groups.** Group delimiter tags are known at compile time from the
  schema and embedded as constants in the generated code — no runtime dictionary lookup is
  needed to find group boundaries.
- **Namespace-isolated versioning**, so multiple FIX dictionary versions (e.g. 4.2 and 4.4) can
  coexist in the same consumer project.

## Quick start

1. Reference the package and add your DataDictionary XML as an `AdditionalFiles` item:

   ```xml
   <ItemGroup>
     <PackageReference Include="FixSourceGenerator" Version="0.1.0" PrivateAssets="all" />
     <AdditionalFiles Include="Schemas\FIX44.xml" />
   </ItemGroup>
   ```

2. Build. The generator produces a reader/writer per message in a namespace derived from your
   project's `RootNamespace` (or the `FixGeneratorNamespace` property) plus a version token, e.g.
   `Acme.Fix.V44.NewOrderSingleReader` / `...NewOrderSingleWriter`.

3. Decode:

   ```csharp
   using Acme.Fix.V44;

   var reader = new NewOrderSingleReader(buffer); // ReadOnlySpan<byte>
   string clOrdId = Encoding.ASCII.GetString(reader.ClOrdID);
   decimal? price = reader.Price;      // T? for optional value fields
   foreach (var alloc in reader.NoAllocs)   // groups are enumerated, never materialized
   {
       decimal qty = alloc.AllocQty;
   }
   ```

4. Encode:

   ```csharp
   Span<byte> destination = stackalloc byte[512];
   var writer = new NewOrderSingleWriter(destination);
   writer.WriteClOrdID("ORD-1"u8);
   writer.WriteSide(Side.Buy);
   writer.WriteOrderQty(100m);
   int length = writer.Finish(); // backpatches BodyLength + CheckSum
   ```

See [`docs/USAGE.md`](docs/USAGE.md) for the full worked example (including components and
nested groups) and guidance on versioning schemas over time.

## Repository layout

- `src/FixSourceGenerator` — the Roslyn incremental source generator (`netstandard2.0`).
  - `Schema/` — the parsed/resolved schema model (`SchemaReader` + `FixDictionary` and friends).
  - `Generators/` — codegen for readers, writers, enums, components, and the embedded runtime.
  - `Diff/` — `SchemaDiffer`, for comparing two dictionary versions and flagging breaking changes.
- `tests/FixSourceGenerator.Tests` — unit, generator-driver, and real-schema conformance tests.
- `benchmarks/FixSourceGenerator.Benchmarks` — BenchmarkDotNet CPU/allocation benchmarks for the
  generated reader/writer (see the benchmarks project's own README for how to run them and the
  latest recorded numbers).
- `docs/CONTRACT.md` — the normative design contract for input schema and generated output.
- `docs/USAGE.md` — getting-started guide, worked example, and schema-versioning guide.
- `CHANGELOG.md` — release history.

## License

MIT — see [`LICENSE.txt`](LICENSE.txt).

