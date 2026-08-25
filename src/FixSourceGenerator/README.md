# FIX Source Generator

A Roslyn-based source generator that converts FIX tag=value DataDictionary XML schemas
(the classic QuickFIX/QuickFIX-J/QuickFIX-n format) into allocation-minimal C# reader/writer
types, in the spirit of [SbeSourceGenerator](https://github.com/pedrosakuma/SbeSourceGenerator)
but for the tag=value wire format instead of Simple Binary Encoding.

Status: early scaffolding. See [`docs/CONTRACT.md`](docs/CONTRACT.md) for the design contract
(input schema shape, C# output shape, type mapping, versioning, diagnostics) and the tracking
issue [#1](https://github.com/pedrosakuma/FixSourceGenerator/issues/1) for the overall roadmap.

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

## Repository layout

- `src/FixSourceGenerator` — the Roslyn incremental source generator (`netstandard2.0`).
- `tests/FixSourceGenerator.Tests` — unit and generator-driver tests.
- `docs/CONTRACT.md` — the normative design contract for input schema and generated output.

## License

MIT — see [`LICENSE.txt`](LICENSE.txt).
