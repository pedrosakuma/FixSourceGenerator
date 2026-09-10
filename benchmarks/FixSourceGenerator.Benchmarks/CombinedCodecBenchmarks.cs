using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;
using Small = FixSourceGenerator.Benchmarks.Generated.Fix.V44;
using SmallRuntime = FixSourceGenerator.Benchmarks.Generated.Fix.V44.Runtime;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class CombinedCodecBenchmarks
{
    private CombinedCodecWorkload _workload = null!;

    [Params("Small", "X1", "X10", "X50", "W1", "W10", "W50")]
    public string Scenario { get; set; } = "Small";

    [GlobalSetup]
    public void Setup() => _workload = new CombinedCodecWorkload(Scenario);

    [Benchmark(Baseline = true)]
    public decimal FluentFull() => _workload.Run(optimized: false);

    [Benchmark]
    public decimal InPlaceProjected() => _workload.Run(optimized: true);
}

internal sealed class CombinedCodecWorkload
{
    private readonly MarketDataWriterBenchmarks _market = new();
    private readonly byte[] _smallBuffer = new byte[512];
    private readonly FixWriterState[] _state;
    private readonly SmallRuntime.FixWriterState[] _smallState;
    private readonly bool _small;
    private readonly bool _snapshot;
    private readonly int _entries;
    private readonly decimal _baseDigest;
    private int _sequence;
    private int _length;

    internal CombinedCodecWorkload(string scenario)
    {
        (_small, _snapshot, _entries) = scenario switch
        {
            "Small" => (true, false, 2),
            "X1" => (false, false, 1),
            "X10" => (false, false, 10),
            "X50" => (false, false, 50),
            "W1" => (false, true, 1),
            "W10" => (false, true, 10),
            "W50" => (false, true, 50),
            _ => throw new ArgumentException("Expected Small, X1/X10/X50 or W1/W10/W50.", nameof(scenario)),
        };
        _market.Entries = _entries;
        _smallState = _small ? new SmallRuntime.FixWriterState[Small.NewOrderSingleWriter.RequiredStateLength] : [];
        _state = _small ? [] : new FixWriterState[_snapshot
            ? MarketDataSnapshotFullRefreshWriter.RequiredStateLength
            : MarketDataIncrementalRefreshWriter.RequiredStateLength];
        if (_small)
            SmallRuntime.FixWriterState.Initialize(_smallState);
        else
            FixWriterState.Initialize(_state);

        Encode(false, 0);
        byte[] reference = Frame.ToArray();
        _baseDigest = Decode(false);
        if (Decode(true) != _baseDigest)
            throw new InvalidOperationException("Initial full/projected digests differ.");
        Encode(true, 0);
        if (!Frame.SequenceEqual(reference) || Decode(true) != _baseDigest)
            throw new InvalidOperationException("Initial fluent/in-place frames differ.");
        // A changing price detects reuse of stale input, not just constant-frame agreement.
        _ = Run(false);
        _ = Run(true);
    }

    internal long Generation => _small ? _smallState[0].Generation : _state[0].Generation;
    internal int FrameLength => _length;
    internal ReadOnlySpan<byte> Frame => _small ? _smallBuffer.AsSpan(0, _length) : _market.WrittenFrame(_length);

    internal decimal Run(bool optimized)
    {
        int offset = _sequence = (_sequence + 1) & 1023;
        Encode(optimized, offset);
        decimal digest = Decode(optimized);
        decimal expected = _baseDigest + (_small ? offset / 100m : _entries * offset / 10000m);
        if (digest != expected)
            throw new InvalidOperationException("Combined codec digest mismatch.");
        return digest;
    }

    private void Encode(bool inPlace, int offset)
    {
        if (!_small)
        {
            _market.SetPriceOffset(offset);
            _length = _market.WriteCombined(_snapshot, inPlace, _state);
            return;
        }

        var message = new Small.NewOrderSingleWriter(_smallBuffer, _smallState, "SENDER"u8, "TARGET"u8, 7,
            new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc), "ORD-1"u8);
        var instrument = message.BeginInstrument("MSFT"u8);
        var tail = Small.FixWriterScopeExtensions.EndInstrument(instrument.SkipSecurityID(),
            Small.Side.Buy, 100m, Small.OrdType.Limit);
        if (inPlace)
            tail.SetPrice(101.25m + offset / 100m);
        else
            tail = tail.WritePrice(101.25m + offset / 100m);
        var group = tail.BeginNoPartyIDs(2);
        for (int i = 0; i < 2; i++)
        {
            var entry = group.BeginEntry(i == 0 ? "PARTY-1"u8 : "PARTY-2"u8);
            if (inPlace)
            {
                entry.SetPartyIDSource('1');
                entry.SetPartyRole(i == 0 ? 1 : 3);
            }
            else
                entry = entry.WritePartyIDSource('1').WritePartyRole(i == 0 ? 1 : 3);
            group = entry.EndEntry();
        }
        _length = Small.FixWriterScopeExtensions.EndGroup(group).Finish();
    }

    private decimal Decode(bool projected)
    {
        if (_small)
            return projected ? FixViewBenchmarks.DecodeProjected(Frame) : FixViewBenchmarks.DecodeFull(Frame);
        if (_snapshot)
            return projected ? MarketDataReaderBenchmarks.DecodeWProjected(Frame) : MarketDataReaderBenchmarks.DecodeW(Frame);
        return projected ? MarketDataReaderBenchmarks.DecodeXProjected(Frame) : MarketDataReaderBenchmarks.DecodeX(Frame);
    }
}
