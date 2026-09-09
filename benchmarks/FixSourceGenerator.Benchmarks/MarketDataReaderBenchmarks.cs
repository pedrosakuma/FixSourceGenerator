using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Attributes;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class MarketDataReaderBenchmarks
{
    private byte[] _x = null!;
    private byte[] _w = null!;

    [Params(1, 10, 50)]
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

    [Benchmark]
    public decimal DecodeXProjected()
    {
        var header = new XHeaderProjection(_x);
        decimal total = header.TradeDate!.Value.DayNumber;
        var group = new MDIncGrpReader(_x).NoMDEntries;
        total += group.Count;
        var iterator = group.GetEnumerator();
        while (iterator.MoveNext())
        {
            var entry = new XEntryProjection(iterator.CurrentSpan);
            total += entry.MDUpdateAction + entry.MDEntryType!.Value;
            if (!entry.TryGetMDEntryID(out var id) ||
                !entry.TryGetOrderID(out var order) ||
                !entry.TryGetSymbol(out var symbol))
            {
                throw new InvalidOperationException("Projected entry identifier missing.");
            }

            total += id.Length + id[0] + order.Length + order[0] + symbol.Length + symbol[0];
            total += entry.MDEntryPx!.Value + entry.MDEntrySize!.Value + entry.NumberOfOrders!.Value;
            total += entry.MDEntryDate!.Value.DayNumber + entry.MDEntryTime!.Value.Ticks;
            total += entry.ExpireDate!.Value.DayNumber + entry.ExpireTime!.Value.Ticks;
        }

        return total;
    }

    [Benchmark]
    public decimal DecodeWProjected()
    {
        var header = new WHeaderProjection(_w);
        decimal total = header.TradeDate!.Value.DayNumber;
        if (!header.TryGetSymbol(out var symbol))
            throw new InvalidOperationException("Projected symbol missing.");
        total += symbol.Length + symbol[0];

        var group = new MDFullGrpReader(_w).NoMDEntries;
        total += group.Count;
        var iterator = group.GetEnumerator();
        while (iterator.MoveNext())
        {
            var entry = new WEntryProjection(iterator.CurrentSpan);
            total += entry.MDEntryType;
            if (!entry.TryGetMDEntryID(out var id) || !entry.TryGetOrderID(out var order))
                throw new InvalidOperationException("Projected entry identifier missing.");
            total += id.Length + id[0] + order.Length + order[0];
            total += entry.MDEntryPx!.Value + entry.MDEntrySize!.Value + entry.NumberOfOrders!.Value;
            total += entry.MDEntryDate!.Value.DayNumber + entry.MDEntryTime!.Value.Ticks;
            total += entry.ExpireDate!.Value.DayNumber + entry.ExpireTime!.Value.Ticks;
        }

        return total;
    }

    [Benchmark]
    public decimal DecodeXProjectedRepeatedGetters()
    {
        decimal total = 0;
        var iterator = new MDIncGrpReader(_x).NoMDEntries.GetEnumerator();
        while (iterator.MoveNext())
        {
            var entry = new XRepeatedProjection(iterator.CurrentSpan);
            total += entry.MDEntryPx!.Value;
            total += entry.MDEntryPx!.Value;
            total += entry.MDEntrySize!.Value;
            total += entry.MDEntrySize!.Value;
        }

        return total;
    }

    [Benchmark]
    public decimal DecodeXProjectedCachedConversions()
    {
        decimal total = 0;
        var iterator = new MDIncGrpReader(_x).NoMDEntries.GetEnumerator();
        while (iterator.MoveNext())
        {
            var entry = new XRepeatedProjection(iterator.CurrentSpan);
            decimal price = entry.MDEntryPx!.Value;
            decimal size = entry.MDEntrySize!.Value;
            total += price + price + size + size;
        }

        return total;
    }

    public void CheckProjectedDigests()
    {
        decimal fullX = DecodeX();
        decimal fullW = DecodeW();
        if (fullX != DecodeXProjected() || fullW != DecodeWProjected())
            throw new InvalidOperationException("Full and projected reader digests differ.");
        if (DecodeXProjectedRepeatedGetters() != DecodeXProjectedCachedConversions())
            throw new InvalidOperationException("Repeated and cached conversion digests differ.");
    }
}

[FixView("MarketDataIncrementalRefresh")]
public readonly ref partial struct XHeaderProjection
{
    public partial DateOnly? TradeDate { get; }
}

[FixView("MarketDataIncrementalRefresh.MDIncGrp.NoMDEntries")]
public readonly ref partial struct XEntryProjection
{
    public partial byte MDUpdateAction { get; }
    public partial byte? MDEntryType { get; }
    public partial ReadOnlySpan<byte> MDEntryID { get; }
    public partial ReadOnlySpan<byte> OrderID { get; }
    public partial ReadOnlySpan<byte> Symbol { get; }
    public partial decimal? MDEntryPx { get; }
    public partial decimal? MDEntrySize { get; }
    public partial int? NumberOfOrders { get; }
    public partial DateOnly? MDEntryDate { get; }
    public partial TimeOnly? MDEntryTime { get; }
    public partial DateOnly? ExpireDate { get; }
    public partial DateTime? ExpireTime { get; }
}

[FixView("MarketDataSnapshotFullRefresh")]
public readonly ref partial struct WHeaderProjection
{
    public partial DateOnly? TradeDate { get; }
    public partial ReadOnlySpan<byte> Symbol { get; }
}

[FixView("MarketDataSnapshotFullRefresh.MDFullGrp.NoMDEntries")]
public readonly ref partial struct WEntryProjection
{
    public partial byte MDEntryType { get; }
    public partial ReadOnlySpan<byte> MDEntryID { get; }
    public partial ReadOnlySpan<byte> OrderID { get; }
    public partial decimal? MDEntryPx { get; }
    public partial decimal? MDEntrySize { get; }
    public partial int? NumberOfOrders { get; }
    public partial DateOnly? MDEntryDate { get; }
    public partial TimeOnly? MDEntryTime { get; }
    public partial DateOnly? ExpireDate { get; }
    public partial DateTime? ExpireTime { get; }
}

[FixView("MarketDataIncrementalRefresh.MDIncGrp.NoMDEntries")]
public readonly ref partial struct XRepeatedProjection
{
    public partial decimal? MDEntryPx { get; }
    public partial decimal? MDEntrySize { get; }
}
