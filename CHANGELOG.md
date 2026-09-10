# Changelog

All notable changes to this project are documented here. This project is pre-1.0; the public
API (generated code shape, MSBuild properties, diagnostics) may still change between minor
versions until `1.0.0`.

## [Unreleased]

### Breaking
- Replace flat generated writers with scoped message/component/group-entry phases. Required
  scalar runs are constructor/factory arguments; `Write`/`Skip` and scope transitions return
  the next handle and consume their source. There is no compatibility facade for the old
  generated writer API (#31). See [`docs/MIGRATION.md`](docs/MIGRATION.md).
- Require caller-owned `Span<FixWriterState>` sized by `RequiredStateLength`. Shared generations
  reject stale copies, active-parent reuse and writes after failure; group entry counts are
  supplied upfront and checked at closure. Use typed allocation, not fixed byte sizes.

### Fixed
- Writer capacity failures now throw `ArgumentException` (`destination`) consistently across
  envelope fields, values, group counters, and finalization. A failed writer rejects further
  operations with `InvalidOperationException`, preventing malformed success-shaped frames (#23).
- Widen shared/handle writer generations to 64 bits: sustained reuse exhausted the old 32-bit
  counter after about 2.63 million X/50 messages. Exhaustion still fails closed without wrapping.

### Added
- In-place optional-tail `Set{Field}` calls preserve the current handle while invalidating older
  copies. Required inputs remain on constructors/factories; empty text, invalid enum/CHAR
  values and duplicate/backward writes are rejected by generated writers (#31).
- A runnable net6/C#11 and net9/C#13 migration example with scoped writing, state reuse,
  absent/zero/nonzero values and qualified-entry projections, exercised in CI (#34).
- Paired encode/decode/consume benchmarks, bounded sampled loads and separate metadata/code-size
  accounting. Shared writer templates and view skip helpers reduce generated duplication.
- Generated `[FixView]` component/qualified-entry projections with scoped group lookup,
  first-occurrence scalar selection, optional-span presence helpers and raw `CurrentSpan`
  access without constructing the full entry reader (#32). Children of optional components
  are contextually optional; affected scalar projections must use nullable property types.
  Ordinary reader/writer consumers retain the net6/C#11 floor; FixView still requires C#13.
- Scoped reader/writer API contract design (docs/CONTRACT.md §12) for the next-generation
  generated shape: per-scope required constructors/factories, optional-component/required-group
  contextual requiredness, group-count policy comparison (upfront count vs. backpatch), and
  selective-projection reuse of `[FixView]`'s early-exit model. Prototyped (not wired into the
  generator) in `tests/FixSourceGenerator.Tests/ScopedApiContractExamples*.cs` (#29).
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
- Generated repeating groups retain linear lookup through 16 tags. Larger sets use a bitmap
  only within 8 KiB and no larger than the replaced sorted-array payload; sparse/high tags fall
  back to binary search. No duplicate lookup structures are retained. The public runtime
  enumerator still accepts unsorted tags; nested-group/delimiter behavior is unchanged (#33).

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
