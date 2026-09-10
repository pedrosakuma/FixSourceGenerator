using EagerProjection.Experiment;
using FixSourceGenerator.Attributes;

namespace FixSourceGenerator.Benchmarks;

[ExperimentalEager("NativeDto.xml", "NativeEnvelope", "NoEntries",
    "FixSourceGenerator.Benchmarks.Generated.Fix.V42.Runtime")]
internal readonly ref partial struct UndEagerValues
{
    public partial ReadOnlySpan<byte> EntryID { get; }
    public partial decimal? Price { get; }
    public partial decimal? Size { get; }
}

[ExperimentalEager("NativeDto.xml", "NativeEnvelope", "NoEntries",
    "FixSourceGenerator.Benchmarks.Generated.Fix.V42.Runtime")]
internal readonly ref partial struct UndPriceValues
{
    [ExperimentalField("Price")]
    public partial decimal? Quote { get; }
}

[FixView("NativeEnvelope.NoEntries")]
internal readonly ref partial struct UndLazyValues
{
    public partial ReadOnlySpan<byte> EntryID { get; }
    public partial decimal? Price { get; }
    public partial decimal? Size { get; }
}
