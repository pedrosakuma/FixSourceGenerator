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

The generator targets consuming code at **.NET 6+ with C# 11+** (it emits `DateOnly`/`TimeOnly`
for FIX date/time fields, `u8` string literals, and `scoped` input spans). When targeting .NET 6,
use a compiler supporting C# 11 and set `<LangVersion>11</LangVersion>`.
The generator's own `netstandard2.0`
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

// Enum fields default to a permissive cast: a wire value outside the schema's known <value>
// domain still decodes into an "unnamed" enum member instead of throwing. To reject unknown
// values explicitly, use the strict variant instead:
if (order.TryGetSideStrict(out var side))
{
    Console.WriteLine($"Known side: {side}");
}

// MULTIPLEVALUESTRING/MULTIPLECHARVALUE/MULTIPLESTRINGVALUE: space-delimited token list.
// The raw span is still available (order.ExecInst / TryGetExecInst), and {Field}Values
// gives a forward-only, allocation-free enumerator over each token.
foreach (var token in order.ExecInstValues)
{
    Console.WriteLine(Encoding.ASCII.GetString(token));
}

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
using Acme.Fix.V44.Runtime;

Span<byte> destination = stackalloc byte[512];
Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
NewOrderSingleWriter.InitializeState(state);
var message = new NewOrderSingleWriter(destination, state, "ORD-1"u8);
var instrument = message.BeginInstrument("MSFT"u8);
var tail = instrument.SkipSecurityID().EndInstrument(Side.Buy, 100m);
var group = tail.WritePrice(101.25m).SkipTransactTime().SkipExecInst().BeginNoAllocs(1);
group = group.BeginEntry("ACC-1"u8, 100m).SkipNoNested().EndEntry();
int messageLength = group.EndGroup().Finish();
SendOverSocket(destination.Slice(0, messageLength));
```

Required scalar runs are constructor or scope-transition arguments. Optional fields advance the
phase through either `Write{Field}` or `Skip{Field}`. Components use `Begin`/`End`; groups use an
upfront expected count, `BeginEntry`/`EndEntry`, then `EndGroup`. Only completed entries count.
The typed state span is bounded by maximum group nesting, must not overlap the destination, and
must remain alive and exclusive until completion. `InitializeState` is idempotent after completion
but rejects live ownership; it never resets the generation.

### Destination capacity and failures

The constructor, field setters (including group counters), and `Finish()` throw
`ArgumentException` with `ParamName == "destination"` when the caller's buffer is too small.
After a failed operation, the writer is **invalid**: subsequent writes, `BeginMessage()`, and
`Finish()` throw `InvalidOperationException`. Discard the partial contents and construct a
new writer over a sufficiently large buffer; never send a buffer from a failed write.
Writes are not transactional: a failed field may already have changed bytes or the cursor.
`Finish()` checks capacity for both the BodyLength shift and the complete checksum before
changing the frame.

Capacity must accommodate the intermediate six-digit BodyLength placeholder, not just the
final frame size. `Finish()` removes unused placeholder digits (or expands beyond six digits),
then appends the seven-byte `10=ddd<SOH>` field. The library does not rent, grow, or own buffers,
and successful writes remain allocation-free.

### Stack-based identifiers

Byte-span inputs and the runtime's `WriteField` / `BeginMessage` use
`scoped ReadOnlySpan<byte>` for inputs: bytes are copied immediately, never retained. This also
works across generated scope transitions:

```csharp
using System;
using System.Buffers.Text;
using Acme.Fix.V44;
using Acme.Fix.V44.Runtime;

static int Encode(Span<byte> destination, long orderId, long accountId)
{
    Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
    NewOrderSingleWriter.InitializeState(state);
    Span<byte> scratch = stackalloc byte[20]; // sufficient even for long.MinValue
    if (!Utf8Formatter.TryFormat(orderId, scratch, out int written))
        throw new InvalidOperationException("Order ID formatting failed.");
    var message = new NewOrderSingleWriter(destination, state, scratch[..written]);
    var instrument = message.BeginInstrument("MSFT"u8);
    var tail = instrument.SkipSecurityID().EndInstrument(Side.Buy, 100m);
    if (!Utf8Formatter.TryFormat(accountId, scratch, out int accountWritten))
        throw new InvalidOperationException("Account ID formatting failed.");
    var group = tail.SkipPrice().SkipTransactTime().SkipExecInst().BeginNoAllocs(1);
    group = group.BeginEntry(scratch[..accountWritten], 100m).SkipNoNested().EndEntry();
    scratch.Clear(); // does not change bytes already copied into the destination
    return group.EndGroup().Finish();
}
```

The destination span is still retained by the writer and must outlive its use. Reader spans,
which reference their original input, keep their existing lifetime contracts.

### Integral and scaled numeric values

For optional `FLOAT`, `PRICE`, `PRICEOFFSET`, `QTY`, `AMT`, and `PERCENTAGE`, generated writers
expose decimal, integral and scaled overloads. Required numeric inputs use the allocation-free
`FixDecimal` carrier, with implicit conversions from `decimal`/`long` and
`FixDecimal.FromScaled(mantissa, scale)`. Readers still return `decimal` / `decimal?`:

```csharp
writer.WritePrice(123.4500m);     // existing decimal API: 44=123.4500
writer.WritePrice(1234500L, 4);   // mantissa * 10^-scale: 44=123.4500
writer.WriteOrderQty(1000L);     // integral quantity, without conversion to decimal
```

The scaled overload accepts a signed `long` mantissa and a scale from **0 through 18**
(inclusive). It preserves exactly that many fractional digits, including trailing zeros:
`(0L, 4)` emits `0.0000`, `(-1L, 4)` emits `-0.0001`, and `(long.MinValue, 18)` emits
`-9.223372036854775808`. Formatting is invariant ASCII, without scientific notation.
An unsupported scale throws `ArgumentOutOfRangeException` (`ParamName == "scale"`) before
writing that field and invalidates the writer, just like a capacity failure.

Existing `int` and `long` arguments resolve to the integral overload for decimal fields;
`decimal` arguments continue to resolve to the decimal overload, retaining fractional support.
FIX `INT` fields and group counters remain `int`; STRING identifiers do not acquire numeric
setters. The same formatting is available through runtime `WriteField(int tag, long value)`
and `WriteField(int tag, long mantissa, int scale)`.

### Temporal formatting and timezone semantics

Temporal setters write digits directly into the byte destination:

| API type | Wire format |
|----------|-------------|
| `DateTime` | `yyyyMMdd-HH:mm:ss.fff` |
| `DateOnly` | `yyyyMMdd` |
| `TimeOnly` | `HH:mm:ss.fff` |

These formats are culture-independent, zero-padded, and keep exactly three fractional
second digits. Sub-millisecond ticks are **truncated**, not rounded; no rollover is introduced.
For compatibility with the original writer, `DateTime` writes the supplied calendar/clock
fields **without converting its `Kind`**. For UTC FIX fields, supply a UTC value (convert
explicitly before calling the setter if needed). The reader's UTC parsing behavior is
unchanged. TZ types continue to use the same mapped format; this update does not add offsets
or change precision.

> **Note on scoped writers and groups:** generated writers expose message, component, group, and
> entry scopes. Supply required scalar values when entering the phase that owns them, explicitly
> write or skip optional members in schema order, and close each scope. Start a group with
> `Begin{Group}(expectedCount)`; the runtime rejects excess entries immediately and rejects a
> short count at `EndGroup()`. Automatic group-count backpatching is not currently generated.

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

## 5.1. Selective projection with `[FixView]`

When you only need a handful of fields from a message (not the full reader), annotate your own
`partial ref struct` with `[FixView("MessageName")]` and declare a `partial` property per field
you're interested in. The generator matches each property against the target message's fields —
by property name, or via `[FixField("...")]` when the names diverge — and emits the missing
property bodies plus a single scanning constructor that **stops early** once every requested tag
has been located (unlike the full reader, which always scans everything it declares).

```csharp
using FixSourceGenerator.Attributes;
using Acme.Fix.V44; // for the Side enum, if you want the typed variant

[FixView("NewOrderSingle")]
public readonly ref partial struct OrderRoutingView
{
    public partial ReadOnlySpan<byte> ClOrdID { get; }
    public partial decimal? Price { get; }
    public partial Side Side { get; }

    // Expose an entire repeating group (issue #17): the declared type must be exactly the
    // {Group}GroupReader the full message reader already generates for this message.
    public partial NoPartyIDsGroupReader NoPartyIDs { get; }
}

// ...
var view = new OrderRoutingView(buffer);
Console.WriteLine(Encoding.ASCII.GetString(view.ClOrdID));
foreach (var party in view.NoPartyIDs)
{
    Console.WriteLine(Encoding.ASCII.GetString(party.PartyID));
}
```

Every property's declared type must be either the field's native C# type (same mapping as §3 in
`docs/CONTRACT.md`) or `ReadOnlySpan<byte>` as a raw, no-parse escape hatch; enum-eligible fields
also accept the generated enum type or its underlying wire type (`byte`/`int`). A property that
matches a **group** instead of a scalar field must be typed exactly `{Group}GroupReader` — no
nullable/span variants, since a group always "exists" as a reader (`Count` is simply `0` if
absent). A mismatch is reported as `FIX014` at build time — no `dynamic`, no runtime cast failures.

Groups exposed this way don't participate in the early-exit scan: the property just wraps the
whole buffer (`new NoPartyIDsGroupReader(_buffer)`), exactly like the full reader does, because
`{Group}GroupReader` already finds its own counter/entries lazily on access. Fields *inside* a
group still can't be selected individually — there's no single scalar value to expose for a 0..N
repetition; declare a second `[FixView]` over the group's own generated entry type if you need
selective projection there too.

**Requirements:**
- The struct must be declared `partial` **and** `ref struct` (`FIX011`) — the generated
  implementation holds a `ReadOnlySpan<byte>` field internally.
- **Requires the consuming project to target C# 13 / a net9+-era SDK**, since partial properties
  are a C# 13 feature. This is stricter than the rest of the generator (readers/writers only need
  net6+, see §2) — if you can't upgrade, use the full message reader instead.
| ID | Meaning |
|---|---|
| FIX010 | `[FixView("X")]`'s message name doesn't match any loaded message. |
| FIX011 | The `[FixView]` struct isn't declared `partial ref struct`. |
| FIX012 | A `partial` property doesn't match any field or group by name (includes a "did you mean" suggestion). |
| FIX013 | A `[FixField("X")]` override references a field or group that doesn't exist on the message. |
| FIX014 | The property's declared type isn't compatible with the matched field's or group's type. |
| FIX015 | Two or more properties target the same field or group. |

See `docs/CONTRACT.md` §11 for the full design (type-compatibility matrix, scope limitations).

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
