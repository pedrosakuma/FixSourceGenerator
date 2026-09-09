# Changelog

All notable changes to this project are documented here. This project is pre-1.0; the public
API (generated code shape, MSBuild properties, diagnostics) may still change between minor
versions until `1.0.0`.

## [Unreleased]

### Fixed
- Writer capacity failures now throw `ArgumentException` (`destination`) consistently across
  envelope fields, values, group counters, and finalization. A failed writer rejects further
  operations with `InvalidOperationException`, preventing malformed success-shaped frames (#23).

### Added
- `scoped` byte-span inputs on generated setters and runtime writer APIs, allowing local
  `stackalloc` identifiers through by-reference helpers and repeating-group fields without
  retaining scratch memory. Consumers remain .NET 6+ with C# 11+ (#26).
- Integral `long` and scaled-integer `(long mantissa, int scale)` setters for decimal FIX
  fields. Scales 0..18 preserve trailing zeros and handle `long.MinValue`; decimal setters
  and reader types remain unchanged (#25).
- Isolated numeric/temporal and full-dictionary generated X/W writer benchmarks with
  10/50 repeating-group entries.

### Changed
- Temporal readers use validated ASCII fast paths for the supported date, time and timestamp
  formats, preserving the original `TryParseExact` fallback, failure outputs and UTC timestamp
  Kind without changing the public reader API (#30).
- Temporal writers now format fixed-position ASCII directly, preserving existing precision,
  invariant formatting, and supplied DateTime clock fields without implicit timezone conversion (#27).
- Generated setters copy compile-time ASCII tag prefixes through shared runtime value formatters.
  Dynamic integer-tag runtime APIs and envelope backpatching are unchanged (#24).
- Generated repeating groups sort their membership tags at generation time and use binary search
  for sets larger than 16 tags. Small sets retain linear lookup, and the public runtime enumerator
  still accepts unsorted tags. Wire-order delimiters and nested-group boundaries are unchanged.

## [0.1.0] - 2026-08-27

### Added
- Initial `IIncrementalGenerator` implementation converting QuickFIX-style DataDictionary XML
  (`AdditionalFiles`) into allocation-minimal `ref struct` reader/writer C# types.
- `SchemaReader`: parses and fully resolves `<fields>`/`<components>`/`<header>`/`<trailer>`/
  `<messages>` into the `FixDictionary` model, reporting FIX001–FIX009 diagnostics.
- Codegen layer: per-message `{Name}Reader`/`{Name}Writer`, nested component readers, repeating
  group enumerators (no `List<T>` materialization), value enums for CHAR/INT fields with a fixed
  `<value>` domain, and an embedded runtime (`FixSpanReader`/`FixSpanWriter`/`FixGroupEnumerator`)
  emitted once per namespace.
- Writer support for header/trailer fields (`BeginString`/`BodyLength`/`CheckSum`, standard
  trailer), alongside the message body writer (issue #10).
- Opt-in strict enum validation via generated `TryGet{Field}Strict` accessors, for callers that
  want to reject out-of-domain CHAR/INT values instead of silently exposing the raw value.
- Typed, allocation-free parsing for `MULTIPLEVALUESTRING`/`MULTIPLECHARVALUE` fields.
- Reader engine reworked to a single-scan eager-location + lazy-parsing design (perf; issue #12):
  one pass locates every declared field, parsing is deferred to first access.
- `[FixView]` (issues #13/#17): opt-in selective field projection over a message — a
  `partial ref struct` decorated with `[FixView("MessageName")]` whose declared properties map to
  a subset of the message's fields (including repeating groups, exposed via the same
  `{Group}GroupReader` the full reader uses). Backed by an early-exit scanning constructor that
  stops once every requested field has been located, instead of always scanning to the end of the
  buffer like the full reader. Adds diagnostics FIX010–FIX015.
- `FixSourceGenerator.Diff.SchemaDiffer`: compares two `FixDictionary` versions and classifies
  changes (`Breaking`/`Warning`/`Info`), with a markdown report renderer.
- Conformance tests against the full public FIX 4.4 SP2 DataDictionary (QuickFIX, BSD-licensed),
  plus cross-conformance testing against QuickFIX/n as a reference oracle (issue #9).
- `benchmarks/FixSourceGenerator.Benchmarks`: BenchmarkDotNet suite comparing generated
  reader/writer and `[FixView]` performance against QuickFIX/n and the full reader, including
  CPU-hotspot attribution via `dotnet-diagnostics-benchmarkdotnet`.
- `docs/CONTRACT.md` (design contract) and `docs/USAGE.md` (usage/versioning guide).
- CI (GitHub Actions): build/test on every push/PR to `main`; release workflow publishes to
  nuget.org via NuGet Trusted Publishing (OIDC) on `v*` tags.

### Known limitations (tracked as fast-follows)
- The writer flattens component/group fields onto the message writer (no nested writer ref
  structs) and does not auto-count/backpatch repeating groups — the group counter is written
  explicitly. See `docs/CONTRACT.md` and `WriterEmitter`'s remarks.
- FIXT1.1 (transport) + FIX50SPx (application) two-file dictionary composition is designed for
  but not yet implemented (issue #1).
