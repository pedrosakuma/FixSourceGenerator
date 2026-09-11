# Troubleshooting

Practical fixes for the most common build-time and runtime surprises when consuming
`FixSourceGenerator`. For narrative usage, see [`USAGE.md`](USAGE.md); for the normative
contract behind these behaviors, see [`CONTRACT.md`](CONTRACT.md); for breaking-change/ownership
details of the current scoped writer API, see [`MIGRATION.md`](MIGRATION.md).

## The generated types (e.g. `Acme.Fix.V44.NewOrderSingleReader`) don't exist / IntelliSense can't find them

This is almost always one of:

1. **The schema isn't wired as `AdditionalFiles`.** The generator only looks at files it's told
   about via the `AdditionalFiles` MSBuild item, and only those ending in `.xml`
   (case-insensitive) — a schema under `Compile`, or with a different extension, is invisible to
   it and produces no diagnostic either. Confirm your `.csproj` has something like:

   ```xml
   <ItemGroup>
     <AdditionalFiles Include="Schemas/FIX44.xml" />
   </ItemGroup>
   ```

2. **The generator isn't referenced as an analyzer at all**, or is referenced as a normal library
   instead. There are two legitimate but very different reference shapes:

   | Reference shape | What you get | Use it for |
   |---|---|---|
   | `<ProjectReference Include="...FixSourceGenerator.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" />` | The generator runs during your build and emits reader/writer code; the generator assembly itself is **not** a compile-time reference. | Ordinary consumer projects building against a source checkout (see `examples/ScopedCodec/ScopedCodec.csproj`, `tests/FixSourceGenerator.Compatibility`). |
   | `<PackageReference Include="FixSourceGenerator" Version="..." PrivateAssets="all" />` | Same effect as above, via the published NuGet package (which only ships `analyzers/dotnet/cs/FixSourceGenerator.dll`, no `lib/` folder — it's marked `DevelopmentDependency`/`IncludeBuildOutput=false`). | Normal package consumers. |
   | Plain `<ProjectReference Include="...FixSourceGenerator.csproj" />` (no `OutputItemType`/`ReferenceOutputAssembly`) | A normal compile-time reference to the generator's own assembly — lets you call its public types (`SchemaReader`, `SchemaDiffer`, ...) from your code, but does **not** run it as a source generator against your schema. | Standalone tooling/CI projects that need `SchemaReader`/`SchemaDiffer` as a library — see `tests/FixSourceGenerator.Tests/FixSourceGenerator.Tests.csproj` for existing prior art, and `USAGE.md` §6 "Diff before you deploy". |

   Using the wrong shape for what you're trying to do produces one of two confusing failures:
   using the "Analyzer" shape when you wanted `SchemaReader` as a library gives you `CS0246`
   (type not found) on `SchemaReader`; using the plain shape when you wanted codegen against a
   schema silently produces **no** generated reader/writer types at all (the assembly is
   referenced, but it never runs as an analyzer).

3. **`FixGeneratorNamespace`/`RootNamespace` isn't visible to the generator via a source
   `ProjectReference`.** A published NuGet package wires
   `<CompilerVisibleProperty Include="FixGeneratorNamespace" />` automatically through its
   `buildTransitive` props; a direct source `ProjectReference` to
   `src/FixSourceGenerator/FixSourceGenerator.csproj` does not, and needs it declared explicitly
   in your own `.csproj`:

   ```xml
   <ItemGroup>
     <CompilerVisibleProperty Include="FixGeneratorNamespace" />
   </ItemGroup>
   ```

   Without `FixGeneratorNamespace` *and* without a project `RootNamespace`, the generator falls
   back to a namespace segment derived from the schema file name — which may not be the namespace
   you expected. Check the actual namespace token: it's `{Root}.Fix.V{major}{minor}{servicepack}`
   (e.g. `V44`, `V50SP2`) or `FIXT{major}{minor}` for a `type="FIXT"` transport dictionary — see
   `USAGE.md` §2.

## `SchemaReader`/`SchemaDiffer` aren't visible / `CS0246` when trying to call them

The `FixSourceGenerator` NuGet package (and the equivalent `OutputItemType="Analyzer"
ReferenceOutputAssembly="false"` project reference) intentionally does **not** expose the
generator assembly as a compile-time reference — it's loaded into the Roslyn analyzer host only.
`SchemaReader`/`SchemaDiffer`/`SchemaDiffReport` are ordinary public types in that same assembly,
but there is no separate published API package for them. To call them directly (e.g. from a CI
schema-diff script), use a plain `ProjectReference` to the generator project with no
`OutputItemType`/`ReferenceOutputAssembly` override — see the table above and
`tests/FixSourceGenerator.Tests/FixSourceGenerator.Tests.csproj` for the existing pattern. This
only works from a source checkout of this repository (there is no NuGet package that exposes
these types as a library today).

<a id="fix014"></a>
## `FIX014`: "Property has type 'X', which is not compatible with field..."

Use a type in the diagnostic's **Accepted types** list, including its required/optional shape:
`decimal` for a required price, `decimal?` for an optional price, or `ReadOnlySpan<byte>` for raw
wire bytes. Resolved types use semantic identity: `int` and `System.Int32`, for example, are
equivalent. A same-named type in an unrelated namespace is not equivalent.

For generated enum/group types, the diagnostic prints the fully qualified name, for example
`global::Acme.Fix.V44.Side` or `global::Acme.Fix.V44.NoPartyIDsGroupReader`. You can copy that
name directly, use `using Acme.Fix.V44;` with `Side`/`NoPartyIDsGroupReader`, or declare a C#
type/namespace alias. The generator accounts for imports even though its schema types do not
exist in the input compilation yet. Generated implementations do not depend on your imports.
Groups accept only their generated reader type — no nullable or raw-span variant; an absent
group has `Count == 0`.

See `CONTRACT.md` §11 and `USAGE.md` §5.1 for the full compatibility matrix.

<a id="fix016"></a>
## `FIX016`: "[FixView(\"X\")] ... matches more than one scope"

A `[FixView]` target name matched more than one message/component/group
candidate. Two different situations produce this, and they need different fixes:

- **Within one schema**, the same short name is reachable as more than one component/group (e.g.
  `NoMDEntries` appears both under `MDIncGrp` and under `MDFullGrp`). Fix this by qualifying the
  path from a message or component root, e.g.
  `MarketDataIncrementalRefresh.MDIncGrp.NoMDEntries`. Each segment after the root must be a
  component or group directly owned by the previous segment's scope, and must itself be unique at
  that point, or resolution still fails.
- **Across multiple loaded schemas**, the *same* qualified path can independently resolve inside
  more than one schema (e.g. two FIX dictionary versions that both declare a component at that
  path). **A qualified path is not a schema-version selector** — if it resolves in more than one
  loaded schema, `FIX016` is reported regardless of how precisely you qualified it. There is
  currently no syntax to pin a `[FixView]` target to one specific loaded schema/version; if you
  need that, keep the ambiguous schemas from being loaded into the same compilation (e.g. split
  them across separate consumer projects), or give the colliding schemas distinct target names
  where you control the dictionary.

See `CONTRACT.md` §11 for the exact resolution order (message root, then component root, then
per-segment unique-match walk).

## Schema diagnostics point into XML

`FIX001`–`FIX009` identify malformed XML, missing/invalid attributes, unsupported constructs,
duplicate definitions, missing references or group counters, and component cycles. Diagnostics
point to the offending schema file and XML position. For `FIX005`, the message names the actual
owner, such as `message 'Order' references undefined field 'TypoField'`; fix that reference or
add the intended definition rather than renaming the message.

## Inspecting the generated code

Generated sources normally exist in the compiler, not as files on disk. To materialize `.g.cs`
files, run this from your consumer project's directory (choose its target framework):

```bash
dotnet build -f net9.0 -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=obj/generated/net9.0
```

Expect one set of files per loaded schema/namespace (readers, writers, components, enums, and a
shared runtime), plus the shared `[FixView]`/`[FixField]` attribute definitions emitted once via
`RegisterPostInitializationOutput`. Keep the output under `obj/` (excluded from the SDK's
`Compile` glob), or outside your project. A `generated/` folder inside the normal source tree
would be compiled again on the next build and cause duplicate-type/member errors unless you
explicitly exclude it with `<Compile Remove="generated/**/*.cs" />`. Use distinct directories
for multiple target frameworks/configurations. If files are missing after an up-to-date build,
rebuild with `-t:Rebuild`.

## Writer reuse, stale handles and "poisoned" state

These behaviors are intentional invariants of the scoped writer API, not bugs — see
`MIGRATION.md` "Ownership, failure and presence" for the full rules this section summarizes:

- **Stale handles are rejected, not silently ignored.** Every fluent `Write`/`Skip`/`Begin`/`End`
  call consumes its source handle; a copy of an earlier handle can no longer write or close a
  scope once a later transition has happened. This is enforced via a generation counter in the
  shared `FixWriterState`, not by nulling out the old handle — using it throws rather than
  corrupting the buffer.
- **A failed mutation poisons the whole writer**, not just the call that failed. Once any live
  handle's operation throws (capacity, invalid value, wrong order/scope), every other handle
  sharing that state is poisoned too. There is no rollback: discard any partial output in the
  destination buffer and do not send it. Reacquire a brand-new writer (with a larger destination
  if the failure was capacity-related) rather than retrying an old handle.
- **`Span<FixWriterState>` must contain at least `RequiredStateLength` elements, allocated once and
  reused across messages without clearing it between them** — `InitializeState` sets it up once;
  clearing/reinitializing between successful messages is unnecessary and reinitializing after a
  failure does not "forgive" the poisoned generation. The same metadata can be reused for the
  next message after a success (or after a failure, provided its generation isn't exhausted —
  generations are 64-bit and fail closed on exhaustion rather than wrapping).
- **Group `expectedCount` is a hard contract, not a hint.** Too many entries fail at
  `BeginEntry`; too few fail at `EndGroup`. Group counters are not backpatched — unlike
  `BodyLength`/`CheckSum`, which are computed automatically at `Finish()`, you must supply the
  correct count upfront.
- **Capacity failures throw `ArgumentException` with `paramName` `"destination"`** (or `"state"`
  for undersized/overlapping writer state) — consistently across envelope fields, values, group
  counters and finalization; they do not return a partial/success-shaped result.

See the [runnable example's ownership assertions](../examples/ScopedCodec/TransformationExampleChecks.cs)
for executable cases covering overlap rejection, undersized destinations, and state reuse across
calls.
