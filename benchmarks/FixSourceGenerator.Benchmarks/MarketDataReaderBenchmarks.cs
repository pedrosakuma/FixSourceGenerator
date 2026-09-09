using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class MarketDataReaderBenchmarks
{
    private byte[] _x = null!;
    private byte[] _w = null!;

    [Params(10, 50)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var writer = new MarketDataWriterBenchmarks { Entries = Entries };
        writer.CheckEquivalentFrames();
        SetFrames(writer.CreateXFrame(), writer.CreateWFrame());
    }

    internal void SetFrames(byte[] x, byte[] w)
    {
        _x = x;
        _w = w;
        if (SliceX() != Entries || SliceW() != Entries)
            throw new InvalidOperationException("Group entry count mismatch.");
    }

    [Benchmark]
    public int SliceX()
    {
        var iterator = new MDIncGrpReader(_x).NoMDEntries.GetEnumerator();
        int count = 0;
        while (iterator.MoveNext()) count++;
        return count;
    }

    [Benchmark]
    public int SliceW()
    {
        var iterator = new MDFullGrpReader(_w).NoMDEntries.GetEnumerator();
        int count = 0;
        while (iterator.MoveNext()) count++;
        return count;
    }

    [Benchmark]
    public decimal DecodeX()
    {
        var reader = new MarketDataIncrementalRefreshReader(_x);
        decimal total = reader.TradeDate!.Value.DayNumber;
        var group = reader.MDIncGrp.NoMDEntries;
        total += group.Count;
        foreach (var entry in group)
        {
            total += (int)entry.MDUpdateAction + (int)entry.MDEntryType!.Value;
            if (!entry.TryGetMDEntryID(out var id) || !entry.TryGetOrderID(out var order))
                throw new InvalidOperationException("Entry identifier missing.");
            if (!entry.Instrument.TryGetSymbol(out var symbol))
                throw new InvalidOperationException("Symbol missing.");
            total += id.Length + id[0] + order.Length + order[0] + symbol.Length + symbol[0];
            total += entry.MDEntryPx!.Value + entry.MDEntrySize!.Value + entry.NumberOfOrders!.Value;
            total += entry.MDEntryDate!.Value.DayNumber + entry.MDEntryTime!.Value.Ticks;
            total += entry.ExpireDate!.Value.DayNumber + entry.ExpireTime!.Value.Ticks;
        }
        return total;
    }

    [Benchmark]
    public decimal DecodeW()
    {
        var reader = new MarketDataSnapshotFullRefreshReader(_w);
        decimal total = reader.TradeDate!.Value.DayNumber;
        if (!reader.Instrument.TryGetSymbol(out var symbol))
            throw new InvalidOperationException("Symbol missing.");
        total += symbol.Length + symbol[0];
        var group = reader.MDFullGrp.NoMDEntries;
        total += group.Count;
        foreach (var entry in group)
        {
            total += (int)entry.MDEntryType;
            if (!entry.TryGetMDEntryID(out var id) || !entry.TryGetOrderID(out var order))
                throw new InvalidOperationException("Entry identifier missing.");
            total += id.Length + id[0] + order.Length + order[0];
            total += entry.MDEntryPx!.Value + entry.MDEntrySize!.Value + entry.NumberOfOrders!.Value;
            total += entry.MDEntryDate!.Value.DayNumber + entry.MDEntryTime!.Value.Ticks;
            total += entry.ExpireDate!.Value.DayNumber + entry.ExpireTime!.Value.Ticks;
        }
        return total;
    }
}
