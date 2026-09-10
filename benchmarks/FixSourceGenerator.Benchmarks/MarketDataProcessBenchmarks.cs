using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class MarketDataProcessBenchmarks
{
    private byte[] _frame = null!;
    private int[] _entryTags = null!;
    private bool _bitmapEntryTags;
    private MarketDataReaderBenchmarks _reader = null!;
    private bool _incremental;
    private (int Tag, ReadOnlyMemory<byte> Value)[] _temporal = null!;
    private (int Tag, ReadOnlyMemory<byte> Value)[] _nonTemporal = null!;

    [Params("X", "W")]
    public string Message { get; set; } = "X";

    [Params(50)]
    public int Entries { get; set; }

    public EntryFieldOrder Order { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _incremental = Message switch
        {
            "X" => true,
            "W" => false,
            _ => throw new ArgumentOutOfRangeException(nameof(Message)),
        };
        var writer = new MarketDataWriterBenchmarks { Entries = Entries };
        byte[] x = MarketDataOrderBenchmarks.Reorder(writer.CreateXFrame(), 279, Entries, Order);
        byte[] w = MarketDataOrderBenchmarks.Reorder(writer.CreateWFrame(), 269, Entries, Order);
        _reader = new MarketDataReaderBenchmarks { Entries = Entries };
        _reader.SetFrames(x, w);
        _frame = _incremental ? x : w;
        Type group = _incremental
            ? typeof(MDIncGrpReader.NoMDEntriesGroupReader)
            : typeof(MDFullGrpReader.NoMDEntriesGroupReader);
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var membership = group.GetField("EntryTagBits", flags) ?? group.GetField("EntryTags", flags)
            ?? throw new InvalidOperationException("Generated group membership metadata is missing.");
        _entryTags = (int[])membership.GetValue(null)!;
        _bitmapEntryTags = membership.Name == "EntryTagBits";

        var temporal = new List<(int, ReadOnlyMemory<byte>)>();
        var nonTemporal = new List<(int, ReadOnlyMemory<byte>)>();
        int position = 0;
        while (FixSpanReader.TryReadField(_frame, position, out int tag, out int start, out int length, out int next))
        {
            if (tag is 75 or 126 or 272 or 273 or 432 || (!_incremental && tag == 779))
                temporal.Add((tag, _frame.AsMemory(start, length)));
            else if (tag is 37 or 55 or 268 or 269 or 270 or 271 or 278 or 279 or 346)
                nonTemporal.Add((tag, _frame.AsMemory(start, length)));
            position = next;
        }
        _temporal = temporal.ToArray();
        _nonTemporal = nonTemporal.ToArray();
        decimal expected = DecodeGenerated();
        decimal withoutTemporal = expected - ParseTemporalLocated();
        if (ProjectGroupScoped() != expected || ProjectFrameSinglePass() != expected
            || DecodeGeneratedNoTemporal() != withoutTemporal
            || ProjectGroupScopedNoTemporal() != withoutTemporal
            || ProjectFrameNoTemporal() != withoutTemporal
            || ParseNonTemporalLocated() != withoutTemporal)
            throw new InvalidOperationException($"Process projections disagree: {Message}, {Order}, {Entries} entries.");
        if (_temporal.Length != (_incremental ? 1 : 2) + 4 * Entries)
            throw new InvalidOperationException("Unexpected number of temporal conversions.");
    }

    [Benchmark]
    public decimal DecodeGenerated() => _incremental ? _reader.DecodeX() : _reader.DecodeW();

    [Benchmark]
    public decimal DecodeGeneratedNoTemporal() => _incremental ? ReadXNoTemporal() : ReadWNoTemporal();

    [Benchmark]
    public decimal ProjectGroupScoped() => ProjectGroups(includeTemporal: true);

    [Benchmark]
    public decimal ProjectGroupScopedNoTemporal() => ProjectGroups(includeTemporal: false);

    [Benchmark]
    public decimal ProjectFrameSinglePass()
        => MarketDataOrderBenchmarks.ProjectSinglePass(_frame, _incremental);

    [Benchmark]
    public decimal ProjectFrameNoTemporal()
        => MarketDataOrderBenchmarks.ProjectSinglePass(_frame, _incremental, includeTemporal: false);

    [Benchmark]
    public decimal ParseTemporalLocated()
    {
        decimal total = 0;
        foreach (var field in _temporal)
        {
            var value = field.Value.Span;
            total += field.Tag switch
            {
                75 or 272 or 432 => FixSpanReader.ParseDateOnly(value).DayNumber,
                273 => FixSpanReader.ParseTimeOnly(value).Ticks,
                126 or 779 => FixSpanReader.ParseDateTime(value).Ticks,
                _ => throw new InvalidOperationException("Unexpected temporal field."),
            };
        }
        return total;
    }

    [Benchmark]
    public decimal ParseNonTemporalLocated()
    {
        decimal total = 0;
        foreach (var field in _nonTemporal)
        {
            var value = field.Value.Span;
            total += field.Tag switch
            {
                37 or 55 or 278 => value.Length + value[0],
                269 or 279 => FixSpanReader.ParseByte(value),
                268 or 346 => FixSpanReader.ParseInt(value),
                270 or 271 => FixSpanReader.ParseDecimal(value),
                _ => throw new InvalidOperationException("Unexpected non-temporal field."),
            };
        }
        return total;
    }

    private decimal ProjectGroups(bool includeTemporal)
    {
        decimal total = 0;
        if (_incremental)
        {
            var reader = new MarketDataIncrementalRefreshReader(_frame);
            if (includeTemporal) total += reader.TradeDate!.Value.DayNumber;
            total += reader.MDIncGrp.NoMDEntries.Count;
        }
        else
        {
            var reader = new MarketDataSnapshotFullRefreshReader(_frame);
            if (includeTemporal) total += reader.TradeDate!.Value.DayNumber + (decimal)reader.LastUpdateTime.Ticks;
            if (!reader.Instrument.TryGetSymbol(out var symbol))
                throw new InvalidOperationException("Symbol missing.");
            total += symbol.Length + symbol[0];
            total += reader.MDFullGrp.NoMDEntries.Count;
        }
        // Same generated metadata and runtime boundary algorithm, but project within each entry.
        var iterator = new FixGroupEnumerator(_frame, 268, _incremental ? 279 : 269,
            _entryTags, sortedEntryTags: true, bitmapEntryTags: _bitmapEntryTags);
        while (iterator.MoveNext())
            total += MarketDataOrderBenchmarks.ProjectSinglePass(iterator.Current, _incremental,
                checkEntryCount: false, includeTemporal: includeTemporal);
        return total;
    }

    private decimal ReadXNoTemporal()
    {
        var reader = new MarketDataIncrementalRefreshReader(_frame);
        var group = reader.MDIncGrp.NoMDEntries;
        decimal total = group.Count;
        foreach (var entry in group)
        {
            total += (int)entry.MDUpdateAction + (int)entry.MDEntryType!.Value;
            if (!entry.TryGetMDEntryID(out var id) || !entry.TryGetOrderID(out var order))
                throw new InvalidOperationException("Entry identifier missing.");
            if (!entry.Instrument.TryGetSymbol(out var symbol))
                throw new InvalidOperationException("Symbol missing.");
            total += id.Length + id[0] + order.Length + order[0] + symbol.Length + symbol[0];
            total += entry.MDEntryPx!.Value + entry.MDEntrySize!.Value + entry.NumberOfOrders!.Value;
        }
        return total;
    }

    private decimal ReadWNoTemporal()
    {
        var reader = new MarketDataSnapshotFullRefreshReader(_frame);
        if (!reader.Instrument.TryGetSymbol(out var symbol))
            throw new InvalidOperationException("Symbol missing.");
        decimal total = symbol.Length + symbol[0];
        var group = reader.MDFullGrp.NoMDEntries;
        total += group.Count;
        foreach (var entry in group)
        {
            total += (int)entry.MDEntryType;
            if (!entry.TryGetMDEntryID(out var id) || !entry.TryGetOrderID(out var order))
                throw new InvalidOperationException("Entry identifier missing.");
            total += id.Length + id[0] + order.Length + order[0];
            total += entry.MDEntryPx!.Value + entry.MDEntrySize!.Value + entry.NumberOfOrders!.Value;
        }
        return total;
    }
}
