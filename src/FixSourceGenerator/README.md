# FIX Source Generator

A Roslyn source generator for allocation-minimal C# readers and writers over FIX tag=value
DataDictionary XML (QuickFIX format). Add your schema as `AdditionalFiles`; generated types
live under `{RootNamespace}.Fix.V{version}`, or the root selected by `FixGeneratorNamespace`.

**Unreleased API:** scoped writers replace the flat writer API from 0.1.0. This source branch
does not select or publish a new package version. See the
[migration guide](https://github.com/pedrosakuma/FixSourceGenerator/blob/main/docs/MIGRATION.md)
before upgrading.

| Surface | Consumer requirement |
|---------|----------------------|
| Generated ordinary readers/writers | .NET 6+ and C# 11+ |
| Optional `[FixView]` partial-property projections | .NET 9+ and C# 13+ |
| Generator assembly | netstandard2.0, hosted by Roslyn |

Required scalar inputs are constructor/scope-transition arguments. Optional-only tails provide
in-place `Set{Field}` methods; fluent `Write`/`Skip` and component/group transitions consume their
source handle. Groups accept `expectedCount` and enforce completion; `Finish()` computes the
envelope's BodyLength and CheckSum. Destination and typed metadata remain caller-owned.

Readers expose spans, lazy value parsing and group enumerators, not heap DTOs. `[FixView]`
selects message/component/qualified-entry fields and can consume each group's `CurrentSpan`.
Keep the original buffer alive and unchanged while any reader/view is in use. Readers are
not complete protocol validators.

## Example

This fragment uses the repository's mini FIX44 dictionary, not the full standard dictionary.
The exact required arguments depend on the supplied XML.

```csharp
using System;
using Acme.Fix.V44;
using Acme.Fix.V44.Runtime;

Span<byte> destination = stackalloc byte[512];
Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
NewOrderSingleWriter.InitializeState(state);
var message = new NewOrderSingleWriter(destination, state, "SENDER"u8, "TARGET"u8, 7,
    new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc), "ORD-1"u8);
var instrument = message.BeginInstrument("MSFT"u8);
var tail = instrument.SkipSecurityID().EndInstrument(Side.Buy, 100m, OrdType.Limit);
tail.SetPrice(101.25m);
int length = tail.SkipNoPartyIDs().Finish();
```

An omitted value differs from explicit zero/false. Explicit empty text is rejected by generated
writers. Failure invalidates the writer owner; discard partial output and never send it.

- [Runnable example](https://github.com/pedrosakuma/FixSourceGenerator/tree/main/examples/ScopedCodec)
- [Usage and installation](https://github.com/pedrosakuma/FixSourceGenerator/blob/main/docs/USAGE.md)
- [Design contract](https://github.com/pedrosakuma/FixSourceGenerator/blob/main/docs/CONTRACT.md)
- [Performance evidence and limitations](https://github.com/pedrosakuma/FixSourceGenerator/blob/main/benchmarks/FixSourceGenerator.Benchmarks/README.md)

MIT licensed.
