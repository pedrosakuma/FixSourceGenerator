# Using FixSourceGenerator

This guide covers installing the generator, wiring up your DataDictionary XML, a worked
end-to-end example (decode + encode, including a component and a repeating group), and how to
manage schema versions over time. For the *design* rationale (why `ref struct` readers/writers,
type mapping tables, diagnostics, naming rules), see [`CONTRACT.md`](CONTRACT.md) — this document
is the practical "how do I use it" companion.

## 1. Installing

Add the package to the project that should contain the generated FIX types, and reference your
schema XML file(s) as `AdditionalFiles` (not `Compile` — the generator reads them as text, they
are not C# source):

```xml
<ItemGroup>
  <PackageReference Include="FixSourceGenerator" Version="0.1.0" PrivateAssets="all" />
  <AdditionalFiles Include="Schemas\FIX44.xml" />
</ItemGroup>
```

`PrivateAssets="all"` is recommended (standard for source generators/analyzers) so the generator
itself isn't exposed as a dependency of your published package.

The generator targets consuming code at **.NET 6+** (it emits `DateOnly`/`TimeOnly` for FIX date/
time fields, and `u8` string literals in the runtime helpers) — the generator's own `netstandard2.0`
target is only a Roslyn hosting requirement and has no bearing on what TFM your project uses.

## 2. Namespace

Every schema file produces types under `{Root}.Fix.V{token}`, where:

- `{Root}` comes from, in priority order: the `FixGeneratorNamespace` MSBuild property (if you set
  one explicitly), then your project's `RootNamespace`, then a namespace segment derived from the
  schema file name as a last resort.
- `{token}` is derived from the schema's `<fix major="" minor="" servicepack="" type="">`
  attributes: `V44` for FIX 4.4, `V50SP2` for FIX 5.0 SP2, `FIXT11` for the FIXT1.1 transport
  dictionary. This lets multiple dictionary versions coexist in one project without name
  collisions (e.g. reference both `FIX42.xml` and `FIX44.xml` and get `Acme.Fix.V42.*` and
  `Acme.Fix.V44.*` side by side).

To pin the namespace explicitly instead of relying on `RootNamespace`:

```xml
<PropertyGroup>
  <FixGeneratorNamespace>Acme.Trading</FixGeneratorNamespace>
</PropertyGroup>
```

## 3. Worked example: decoding

Given a `NewOrderSingle` (`MsgType=D`) message that includes the `Instrument` component and a
`NoAllocs` repeating group, the generator produces (abbreviated):

```csharp
namespace Acme.Fix.V44;

public readonly ref struct NewOrderSingleReader
{
    public NewOrderSingleReader(ReadOnlySpan<byte> buffer);

    public ReadOnlySpan<byte> ClOrdID { get; }          // required STRING -> non-empty span
    public Side Side { get; }                            // required CHAR with <value> -> enum
    public decimal OrderQty { get; }                      // required QTY -> decimal
    public decimal? Price { get; }                        // optional PRICE -> decimal?
    public InstrumentReader Instrument { get; }            // component -> nested reader, not flattened
    public NoAllocsGroupReader NoAllocs { get; }           // group -> enumerable sub-reader
}
```

Usage:

```csharp
using Acme.Fix.V44;
using System.Text;

ReadOnlySpan<byte> buffer = ReceiveFromSocket();
var order = new NewOrderSingleReader(buffer);

string clOrdId = Encoding.ASCII.GetString(order.ClOrdID); // materialize only if you need a string
bool isBuy = order.Side == Side.Buy;                       // enum comparison, no boxing/lookup
string symbol = Encoding.ASCII.GetString(order.Instrument.Symbol); // nested component reader

// Optional value-type field: T? pattern, no allocation.
if (order.Price is { } price)
{
    Console.WriteLine($"Limit price: {price}");
}

// Optional span-like field: Try{Field} pattern (an empty span is itself a valid value, so it
// can't be used as an "absent" sentinel — see CONTRACT.md §4).
if (order.Instrument.TryGetSecurityID(out var securityId))
{
    Console.WriteLine(Encoding.ASCII.GetString(securityId));
}

// Groups are enumerated directly over the buffer — never materialized into a List<T>.
foreach (var allocation in order.NoAllocs)
{
    Console.WriteLine($"{Encoding.ASCII.GetString(allocation.AllocAccount)}: {allocation.AllocQty}");

    // Groups can nest; inner groups are enumerated the same way.
    foreach (var nested in allocation.NoNested)
    {
        Console.WriteLine(Encoding.ASCII.GetString(nested.NestedPartyID));
    }
}
```

## 4. Worked example: encoding

```csharp
using Acme.Fix.V44;

Span<byte> destination = stackalloc byte[512];
var writer = new NewOrderSingleWriter(destination); // writes BeginString/MsgType immediately

writer.WriteClOrdID("ORD-1"u8);
writer.WriteSide(Side.Buy);
writer.WriteOrderQty(100m);
writer.WriteOrdType(OrdType.Limit);
writer.WritePrice(101.25m);
writer.WriteSymbol("MSFT"u8);        // Instrument component fields are flattened onto the writer
writer.WriteNoAllocs(1);              // group counter is written explicitly (see note below)
writer.WriteAllocAccount("ACC-1"u8);
writer.WriteAllocQty(100m);

int messageLength = writer.Finish(); // backpatches BodyLength (tag 9) and CheckSum (tag 10)
SendOverSocket(destination.Slice(0, messageLength));
```

> **Note on the writer and groups (v1 pragmatic decision):** unlike the reader, which exposes
> components/groups as nested sub-readers, the *writer* flattens component and group fields
> into `Write{Field}` methods on the message writer, in wire-declaration order (see
> `docs/CONTRACT.md`, `WriterEmitter` remarks). You write the group counter field explicitly
> (`WriteNoAllocs(1)` above) before writing that many repetitions of the group's fields — the
> writer does not automatically count/backpatch group repetitions in v1. This is a documented
> fast-follow item, not a limitation you need to work around beyond writing the count yourself.

## 5. Diagnostics

If your schema has a structural problem, the generator reports it as a normal C# build
diagnostic (no build break unless the descriptor's severity is `Error`):

| ID | Meaning |
|---|---|
| FIX001 | A required XML attribute (e.g. `<field number="" name="" type="">`) is missing. |
| FIX002 | The schema XML itself could not be parsed (not well-formed, or missing `<fix>` root). |
| FIX003 | An XML element the generator doesn't understand was ignored. |
| FIX004 | Duplicate definition (e.g. two fields with the same number, two messages with the same `msgtype`). |
| FIX005 | A `<field>`/`<component>` reference doesn't resolve to a definition in `<fields>`/`<components>`. |
| FIX006 | A field's FIX type isn't recognized; it falls back to `ReadOnlySpan<byte>`. |
| FIX007 | A `<group>` has no matching `NUMINGROUP` field definition for its counter. |
| FIX008 | Two or more components reference each other circularly and can't be generated. |

See `docs/CONTRACT.md` §8 for full descriptions and `AnalyzerReleases.Shipped.md` for severities.

## 6. Versioning schemas over time

A few practices for evolving your schema(s) safely as your counterparty's dictionary changes or
as you add support for additional FIX versions:

- **Keep old and new dictionary files side by side**, each as its own `AdditionalFiles` entry
  (e.g. `Schemas/FIX44.xml`, `Schemas/FIX44-2024Q3.xml`). Because the generated namespace is
  keyed off the *schema's own* `major`/`minor`/`servicepack`/`type` attributes rather than the
  file name, two files that declare the *same* version token will collide (their generated types
  land in the same namespace) — give schema variants distinct version metadata, or maintain a
  single evolving file per version and rely on source control history/tags for point-in-time
  diffs rather than parallel files with the same declared version.
- **Diff before you deploy.** Use `FixSourceGenerator.Diff.SchemaDiffer.Diff(oldSchema, newSchema)`
  to compare two parsed `FixDictionary` instances (parse each with `SchemaReader.Parse(...)`) and
  get a structured list of `SchemaChange` entries, each classified `Breaking`/`Warning`/`Info`.
  Render a human-readable report with `SchemaDiffReport.ToMarkdown(changes)`:

  ```csharp
  using FixSourceGenerator.Diff;
  using FixSourceGenerator.Schema;

  var oldSchema = SchemaReader.Parse(File.ReadAllText("FIX44-old.xml"), "FIX44-old.xml", d => { });
  var newSchema = SchemaReader.Parse(File.ReadAllText("FIX44-new.xml"), "FIX44-new.xml", d => { });

  var changes = SchemaDiffer.Diff(oldSchema!, newSchema!);
  bool hasBreakingChanges = changes.Any(c => c.Severity == SchemaDiffSeverity.Breaking);
  string report = SchemaDiffReport.ToMarkdown(changes);
  ```

  Breaking changes flagged include: a required field removed/added, a message or component
  removed, a field's type changed, a message's `msgtype` changed, a group's entries changed, or
  an enum value removed from a field that has enumerated values. Wire this into CI (e.g. a script
  or MSBuild task that runs the diff against the previous committed schema and fails the build on
  any `Breaking` finding) to catch accidental incompatible schema edits before they ship.
- **Multiple simultaneous versions are first-class**, not a workaround: since each schema's
  version token produces its own namespace, a consumer that needs to talk to counterparties on
  both FIX 4.2 and FIX 4.4 simply references both XML files and gets `Acme.Fix.V42.*` and
  `Acme.Fix.V44.*` independently, with no shared mutable state between them.
- **FIXT1.1 + FIX50SPx (transport + application) composition** — real FIX 5.0+ deployments split
  the dictionary into a transport file (`FIXT11.xml`) and an application file (`FIX50SP2.xml`).
  Merging the two into a single effective dictionary before codegen is designed for in the model
  but not yet implemented (see `docs/CONTRACT.md` §1.1/§10) — track issue
  [#1](https://github.com/pedrosakuma/FixSourceGenerator/issues/1) for progress on this fast-follow.

## 7. Validating against a reference implementation

Beyond parsing/compiling a real dictionary (see the conformance tests in
`tests/FixSourceGenerator.Tests/RealSchemaConformanceTests.cs`), issue
[#9](https://github.com/pedrosakuma/FixSourceGenerator/issues/9) tracks cross-conformance testing
against [QuickFIX/n](https://github.com/connamara/quickfixn) — the mature, widely deployed .NET
FIX engine, which consumes the same DataDictionary XML format and generates its own message
classes via its `DDTool`. The plan is to build/decode the same logical message with both
implementations and assert the encoded bytes (or decoded field values) match, as an independent
sanity check beyond this project's own unit and conformance tests.

See [`CHANGELOG.md`](../CHANGELOG.md) for the release history of this generator itself.
