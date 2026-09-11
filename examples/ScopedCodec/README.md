# ScopedCodec example

A runnable console project that exercises the current (unreleased) scoped reader/writer API
against the repository's mini FIX44 dictionary
([`FIX44-mini.xml`](../../tests/FixSourceGenerator.Tests/TestData/FIX44-mini.xml), linked in as
`Schema/FIX44-mini.xml`), covering only `NewOrderSingle` (`Instrument` component, `NoPartyIDs`
group). It is not a general-purpose FIX client — see
[Lifetime and input assumptions](#lifetime-and-input-assumptions) below before reusing this code
against real traffic.

For narrative documentation, start with [`docs/USAGE.md`](../../docs/USAGE.md); this README only
covers what is specific to running/reading *this* example project.

## Files

| File | What it shows |
|---|---|
| [`ScopedCodec.csproj`](ScopedCodec.csproj) | Project wiring: analyzer reference, `AdditionalFiles`, namespace property (see below). |
| [`Program.cs`](Program.cs) | Entry point: loops over absent/zero/nonzero `Price`, calls `CodecExample`, `ProjectionExample` (net9 only) and `TransformationExampleChecks`. |
| [`CodecExample.cs`](CodecExample.cs) | Core round trip: `Encode` builds a full `NewOrderSingleWriter` message (header, `Instrument` component, `NoPartyIDs` group, optional `Price`); `Check` reads it back with the full `NewOrderSingleReader` and asserts every field/group entry. |
| [`ProjectionExample.cs`](ProjectionExample.cs) | Two `[FixView]` projections: `OrderRoutingView` (message-level: `ClOrdID`, `Price`, the whole `NoPartyIDs` group) and `PartyView` (entry-level, fed each entry's `CurrentSpan`). Requires C# 13/net9 partial properties, so it's excluded from the `net6.0` build (see the csproj's `Compile Remove` below). |
| [`TransformationExample.cs`](TransformationExample.cs) | `Rewrite`: decodes one `NewOrderSingle`, adjusts `Price` by a delta, optionally filters `NoPartyIDs` entries by `PartyRole`, and re-encodes to a new destination/routing/sequence/time/ClOrdID. This is application code built on the generated API, not a new generated surface. |
| [`TransformationExampleChecks.cs`](TransformationExampleChecks.cs) | Exhaustive checks for `Rewrite`: price present/zero/nonzero, `SecurityID` present/absent, all/some/no surviving parties, growing/shrinking the order ID, overlapping-buffer rejection, undersized-destination rejection, and post-call input immutability. |

## Building and running

The project multi-targets both consumer TFMs the generator supports:

```bash
dotnet restore examples/ScopedCodec --source https://api.nuget.org/v3/index.json
dotnet run --no-restore -c Release -f net6.0 --project examples/ScopedCodec
dotnet run --no-restore -c Release -f net9.0 --project examples/ScopedCodec
```

| TFM | `LangVersion` | What actually runs |
|---|---|---|
| `net6.0` | `11` | `CodecExample` + `TransformationExampleChecks` only. `ProjectionExample.cs` is excluded from this TFM's compile (`<Compile Remove="ProjectionExample.cs" />`, conditioned on `'$(TargetFramework)' == 'net6.0'`), because `[FixView]` partial properties need C# 13. |
| `net9.0` | `13` | Same as above, plus `ProjectionExample` (guarded in `Program.cs` by `#if NET9_0_OR_GREATER`). |

Both target frameworks and both `LangVersion`s are declared directly in
[`ScopedCodec.csproj`](ScopedCodec.csproj) — there is no separate "net6 flavor" of the project.
The explicit `--source` on `dotnet restore` points only at the public NuGet feed; it deliberately
bypasses the `local-packages`/benchmark-only feed used elsewhere in the solution, so this command
works from a clean checkout without extra configuration.

## Project wiring (`AdditionalFiles` and namespace)

```xml
<ItemGroup>
  <ProjectReference Include="../../src/FixSourceGenerator/FixSourceGenerator.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  <AdditionalFiles Include="../../tests/FixSourceGenerator.Tests/TestData/FIX44-mini.xml"
                   Link="Schema/FIX44-mini.xml" />
  <CompilerVisibleProperty Include="FixGeneratorNamespace" />
</ItemGroup>
```

- `OutputItemType="Analyzer" ReferenceOutputAssembly="false"` loads the generator DLL into the
  Roslyn analyzer pipeline for **this** project only, and — importantly — does *not* add it as a
  normal compile-time reference. That's correct for code generation, but it also means you cannot
  call generator-internal types like `SchemaReader`/`SchemaDiffer` from this project's own code;
  see [`docs/TROUBLESHOOTING.md`](../../docs/TROUBLESHOOTING.md) if you need that.
- The dictionary must be an `AdditionalFiles` item, not `Compile` — the generator reads it as
  additional (non-C#) source text. `Link="Schema/FIX44-mini.xml"` only affects how the file is
  displayed in an IDE's solution view; it does not change how it is read.
- `<FixGeneratorNamespace>Acme</FixGeneratorNamespace>` (set in the `PropertyGroup`, not shown
  above) is the root the generator uses to build `{Root}.Fix.V{token}` — here, `Acme.Fix.V44`
  (FIX44-mini declares `major="4" minor="4"`). `CompilerVisibleProperty` is required for a
  *source* `ProjectReference` to see MSBuild properties at all — a released NuGet package wires
  this automatically via its `buildTransitive` props, but a direct project reference (as used
  here) does not get that for free. Without either `FixGeneratorNamespace` or a project
  `RootNamespace`, the generator falls back to a namespace segment derived from the schema file
  name (see `docs/USAGE.md` §2).

## Lifetime and input assumptions

- Every reader/view/entry span here borrows the buffer it was constructed from; none of them are
  used after that buffer could have been reused or gone out of scope. If you copy this pattern
  into your own code, do the same — see `docs/MIGRATION.md` "Selective readers" for the full rule.
- Writer state (`Span<FixWriterState>`) is allocated once per call site and reused, never cleared,
  matching the ownership rules in `docs/MIGRATION.md` "Ownership, failure and presence"; a failed
  mutation poisons the shared owner and all its handles, so every failure path here reacquires a
  fresh writer instead of retrying an old handle.
- `TransformationExample.Rewrite` assumes its `source` span is **already a valid, schema-conforming
  `NewOrderSingle` for FIX44-mini** — it is not a FIX validator, a generic proxy, or hardened
  against arbitrary/adversarial input. It also normalizes an absent `NoPartyIDs` group to an
  explicit `453=0` on output and does not preserve unknown fields, duplicate tags, or the original
  header/envelope. See `docs/MIGRATION.md` "Direct transformation example" for the full list of
  what it does and does not guarantee before reusing this pattern against real traffic.

## Where to go next

- [`docs/USAGE.md`](../../docs/USAGE.md) — installation, the full worked decode/encode example,
  `[FixView]` guide, and schema-versioning practices.
- [`docs/MIGRATION.md`](../../docs/MIGRATION.md) — what changed from the flat 0.1.0 writer API,
  ownership/ordering/failure rules for the scoped writer, and performance evidence.
- [`docs/TROUBLESHOOTING.md`](../../docs/TROUBLESHOOTING.md) — fixes for common build-time
  surprises (missing generated types, `FIX014`/`FIX016`, inspecting generated `.g.cs` files).
- [`docs/CONTRACT.md`](../../docs/CONTRACT.md) — the normative design contract this example's
  generated code is checked against.
