# Migrating to the scoped codec API

This guide covers the **unreleased** integration of #30-#34. It is not an announcement of a
new package version. Package 0.1.0 uses the older flat writer API; do not expect these examples
to compile against that version. Version selection, merging and package publication remain
separate maintainer actions.

## Consumer requirements

| Surface | Requirement |
|---------|-------------|
| Ordinary generated readers/writers | net6+ and C#11+ |
| Optional `[FixView]` partial properties | net9+ and C#13+ |
| Generator assembly | netstandard2.0; this is not the consumer TFM |

Use a modern SDK/compiler when targeting net6 with C#11. The compatibility floor does not
make the out-of-support .NET 6 runtime a recommended deployment target.
The [runnable example](../examples/ScopedCodec) exercises both configurations in CI and reuses
[FIX44-mini.xml](../tests/FixSourceGenerator.Tests/TestData/FIX44-mini.xml). The full FIX44
compatibility consumer remains in `tests/FixSourceGenerator.Compatibility`.

## Breaking writer changes

| Previous usage | Scoped replacement |
|----------------|--------------------|
| Construct a writer with only a destination, then write required fields | Supply destination, typed metadata and the next required scalar run to the constructor |
| Write component fields on the message writer | `Begin{Component}(requiredInputs)`; close with `End{Component}(nextRequiredInputs)` |
| Write a raw group count and repeat flat setters | `Begin{Group}(expectedCount)`, `BeginEntry(requiredInputs)`, `EndEntry()`, `EndGroup()` |
| Ignore a setter's return or keep using a parent during a child scope | Retain the handle returned by each fluent transition; the source is consumed |
| Write optional fields repeatedly or in arbitrary order | Write once in schema order; duplicate/backward writes fail |
| Treat absent numeric values as zero | Omit the optional call; zero and false are real emitted values |
| Assume fixed owner-state bytes or clear state between messages | Allocate typed `FixWriterState[RequiredStateLength]`, initialize once and reuse without clearing |

There is no compatibility facade for old generated flat writers. Low-level `FixSpanWriter`
still exists for explicit raw encoding, but does **not** replace the generated schema, order,
required-scope and count guarantees.

Required inputs are **local to the scope**, not one transitive message constructor. Header
inputs precede body inputs according to the actual dictionary. Required fields after an
optional field/component/group become arguments of the transition past that member; this is
why even a `Skip{Field}(...)` can take required arguments. Never reorder XML just to obtain a
more convenient constructor.

An optional component can be skipped entirely. Once entered, its required fields/scopes must
be completed. Every group entry must start with its structural delimiter even when the XML
marks that field optional. `expectedCount` cannot be negative; too many entries fail at
`BeginEntry`, too few at `EndGroup`. A required NUMINGROUP means the counter must be present;
it does not by itself imply a strictly positive count. Group counters are not backpatched.

### Fluent calls versus in-place optional tails

```csharp
// Choose one approach after obtaining a tail whose remaining members are optional:
var next = tail.WritePrice(101.25m); // consume tail; continue through next
```

```csharp
tail.SetPrice(101.25m);             // keep this handle; earlier copies become stale
```

Do not perform both writes for the same field. `Set{Field}` returns `void` and is emitted only
for optional-only tails; it cannot bypass a required scope. Later optional fields can be
written directly in schema order, leaving earlier ones absent. Closing a tail can omit its
remaining optional members. Shared template/continuation type names are implementation detail;
prefer `var` and the generated factories rather than spelling those generic types yourself.

Required decimal-category values use `FixDecimal`, with implicit conversions from `decimal`
and `long`, or `FixDecimal.FromScaled(mantissa, scale)`. Optional numeric `Write`/`Set` methods
retain decimal, integral and scaled overloads. Scales 0-18 preserve trailing zeros; readers
still return `decimal`/`decimal?`. This is not permission to pass arbitrary business values:
application-specific price/quantity constraints remain the caller's responsibility.

## Ownership, failure and presence

The [example encoder](../examples/ScopedCodec/CodecExample.cs) shows required header/body inputs,
a component, a group, optional price and scaled quantity. Its
[caller](../examples/ScopedCodec/Program.cs) reuses destination and metadata, clears a scratch
identifier after encoding, and consumes frames with price absent, zero and nonzero.

Initialize the typed metadata once before first use. Keep it exclusive, alive and non-overlapping
with the destination until completion. Do not clear, overwrite, alias across independent active
writers or manually reset it. Reinitialization after completion/failure does not reset the
generation. Generations are 64-bit and advance per field/transfer, not merely per message;
exhaustion fails closed rather than wrapping to revive old handles.

Fluent transitions consume their source; a copied old handle cannot write or close a scope.
An in-place call preserves only the updated handle. Using a stale copy is rejected without
invalidating the current legitimate owner. A failed mutation through the live handle poisons
the owner and all of its handles. There is no rollback: **never send partial output**.
After failure, reacquire a new writer (using a larger destination if needed), not an old handle.
The same metadata can be reused after success/failure, unless its generation is exhausted.

Capacity failures throw `ArgumentException` with parameter `destination`. Invalid values/scales
and structural/order/ownership failures throw rather than silently omit fields. The destination
must fit the intermediate six-digit BodyLength placeholder as well as the final checksum.
`Finish()` retains the existing BodyLength/CheckSum algorithm; it is not available before
required scopes are complete.

Generated writers reject explicit empty text, including optional text. To omit it, skip the
call/scope instead. Requiredness is not a general business-domain validator. On **reading**,
an optional span's `TryGet` can still distinguish an absent field from an explicitly empty
wire value; typed optional parsing can return null for absent or unparseable values. `Count`
alone cannot distinguish an absent group from a present zero count. No new `TryGetCount`
API is promised by the historical prototype in the contract.

## Selective readers

Ordinary readers retain their existing eager-location/lazy-value API. They are not strict
FIX validators and retain legacy scan/duplicate behavior; do not infer valid inbound required
fields from the XML alone.

`[FixView("Message")]`, `[FixView("Component")]` and
`[FixView("Message.Component.Group")]` select one scope. Ambiguous short targets produce
`FIX016`; qualify the path. A field under an optional component is contextually optional and
must use a nullable value property even if required within that component.

Projected groups are bounded during the view scan using nested topology, not wrappers around
the whole message. Feed an entry projection the enumerator's `CurrentSpan` rather than the
whole message; this also avoids constructing the full entry reader. See the
[compiled message/entry views](../examples/ScopedCodec/ProjectionExample.cs).
Views select the first scalar occurrence in the applicable scope, but do not enforce strict
duplicate rejection or validate the entire FIX envelope.

Readers, views and entry spans borrow their input. Consume them before a receive/encode buffer
is recycled, do not retain them across `await` or capture them, and copy only selected values
when a longer lifetime is needed. Writer input spans are different: `scoped` inputs are copied
immediately; the destination and owner metadata remain borrowed.

## Direct transformation example

[TransformationExample.Rewrite](../examples/ScopedCodec/TransformationExample.cs) is executable
application code, **not a new generated API**. It uses the existing mini FIX44 NewOrderSingle
reader and scoped writer to route an order to `ROUTER` / `VENUE` with an outgoing sequence/time
and replacement ClOrdID supplied by the caller. It caches numeric getters, adjusts Price only
when present, borrows Instrument/Party text and optionally filters parties by role.

```bash
dotnet run -c Release -f net6.0 --project examples/ScopedCodec
dotnet run -c Release -f net9.0 --project examples/ScopedCodec
```

Keep the source alive and unchanged until the synchronous call returns. Source and destination
must not overlap; the example rejects this before creating a writer. Owner metadata must also
remain exclusive and non-overlapping as described above. Reuse initialized metadata and output
storage between completed calls, but consume/send the previous output before overwriting it.
On failure, discard partial output; reacquire a writer rather than using an old handle. The
example propagates errors rather than returning a success-shaped partial frame.

Without a filter, the advertised input group count is used. With a filter, an explicit first
pass counts survivors before `BeginNoPartyIDs`; the second pass writes them in original order.
This includes zero survivors. It is not a one-pass filter and does not sort or buffer entries.
Zero Price remains present when the delta is zero; absent Price stays absent.

This is a transformation of **already validated, schema-conforming input** for the mini
dictionary, not a generic FIX proxy or validation layer. Business rules for changing order
identifiers, prices, routing/session metadata and party roles remain the caller's responsibility.
It preserves the known body/component/entry values except the requested changes, but deliberately
normalizes an absent party group to `453=0`. It does not preserve unknown fields, duplicates,
original ordering/formatting, or the incoming header/envelope. Readers' permissive defaults
must not be treated as proof that arbitrary inbound traffic is valid.

The executable cases cover missing/zero/nonzero Price, optional SecurityID/party fields,
shorter/longer identifiers, all/some/no surviving parties, input immutability, rejected overlap,
capacity failure and subsequent state reuse. The existing CI example executions run these on
both consumer targets. Allocations in the executable assertions are not a benchmark of `Rewrite`.
The [recorded design experiments](../benchmarks/experiments/README.md) explain why this pattern
is the current starting point and why eager/owned alternatives remain experimental.

## Performance and integration evidence

All timings, runtime versions, uncertainty and reproduction commands live in the
[benchmark README](../benchmarks/FixSourceGenerator.Benchmarks/README.md). The phases are
measured independently; their percentage gains must **not** be added together:

| Change | Evidence and limitation |
|--------|-------------------------|
| #23-#27 capacity/input/numeric/temporal/prefix work | Retained targeted regressions and independent QuickFIX/n conformance; raw/scoped writer comparisons distinguish schema guarantees |
| #30 temporal parsing | ASCII fast paths plus original fallback, exercised on actual net6/net8/net9/net10 runtimes |
| #31 scoped writers and in-place tails | X/W50 in-place means about 31%/27% below fluent in the writer-only confirmation; no universal small-frame benefit; extra safety still costs more than raw writing |
| #32 scoped projections/shared helpers | Full versus projected reads consume the same selected fields; sharing reduces duplicated source/IL, not all topology work |
| Combined codec | Actual encode -> decode -> consume for Small and X/W1/10/50; persistent-state X50 load crossed the former 32-bit generation limit |
| #33 bounded membership | Projected combined X/W50 about 13%/8% below binary in confirmation; retained membership payload about 38% lower in the measured schema set; W full-reader path did not show a reliable gain |

Bitmap lookup replaces, rather than accompanies, the tag array only above 16 tags, within
8 KiB and no larger than that array. Small linear and sparse/high-tag binary paths remain.
Type initialization, array payload, generated/source/native code and warmed allocations are
reported separately. These are shared-host codec results, not network/session throughput or
latency SLAs. The full FIX44/FIX50SP2 cases still fit the exercised 2 GiB managed-heap limit.

Regression coverage is in `WriterContractTests`, `WriterMutationTests`, `InPlaceWriterTests`,
`GroupMembershipTests`, `FixViewEndToEndTests`, `RoundTripTests` and `QuickFixNConformanceTests`.
The CI fixture commands cover order permutations, temporal/non-temporal projections, writer
frame equality and combined changing-price/state reuse. The executable example keeps the
consumer-facing snippets anchored to a real dictionary and both consumer configurations.

No new transport implementation, FIXT composition, automatic frame sorting, group-count
backpatching, DTO ownership layer or package publication is part of this migration.
