# Changelog

All notable changes to this project are documented here. This project is pre-1.0; the public
API (generated code shape, MSBuild properties, diagnostics) may still change between minor
versions until `1.0.0`.

## [Unreleased]

### Added
- Initial `IIncrementalGenerator` implementation converting QuickFIX-style DataDictionary XML
  (`AdditionalFiles`) into allocation-minimal `ref struct` reader/writer C# types.
- `SchemaReader`: parses and fully resolves `<fields>`/`<components>`/`<header>`/`<trailer>`/
  `<messages>` into the `FixDictionary` model, reporting FIX001–FIX008 diagnostics.
- Codegen layer: per-message `{Name}Reader`/`{Name}Writer`, nested component readers, repeating
  group enumerators (no `List<T>` materialization), value enums for CHAR/INT fields with a fixed
  `<value>` domain, and an embedded runtime (`FixSpanReader`/`FixSpanWriter`/`FixGroupEnumerator`)
  emitted once per namespace.
- `FixSourceGenerator.Diff.SchemaDiffer`: compares two `FixDictionary` versions and classifies
  changes (`Breaking`/`Warning`/`Info`), with a markdown report renderer.
- Conformance tests against the full public FIX 4.4 SP2 DataDictionary (QuickFIX, BSD-licensed).
- `docs/CONTRACT.md` (design contract) and `docs/USAGE.md` (usage/versioning guide).

### Known limitations (tracked as fast-follows)
- The writer flattens component/group fields onto the message writer (no nested writer ref
  structs) and does not auto-count/backpatch repeating groups — the group counter is written
  explicitly. See `docs/CONTRACT.md` and `WriterEmitter`'s remarks.
- FIXT1.1 (transport) + FIX50SPx (application) two-file dictionary composition is designed for
  but not yet implemented (issue #1).
- Cross-conformance testing against QuickFIX/n as a reference oracle is tracked in issue #9.
