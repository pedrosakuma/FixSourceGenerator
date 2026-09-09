using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class TemporalWriterBenchmarks
{
    private readonly byte[] _destination = new byte[128];
    private readonly DateTime _timestamp = new(2024, 2, 29, 23, 59, 59, 123, DateTimeKind.Utc);
    private readonly DateOnly _date = new(2024, 2, 29);
    private readonly TimeOnly _time = new(23, 59, 59, 123);

    [Benchmark]
    public int Timestamp()
    {
        var writer = new FixSpanWriter(_destination);
        writer.WriteField(52, _timestamp);
        return writer.Position;
    }

    [Benchmark]
    public int Date()
    {
        var writer = new FixSpanWriter(_destination);
        writer.WriteField(272, _date);
        return writer.Position;
    }

    [Benchmark]
    public int Time()
    {
        var writer = new FixSpanWriter(_destination);
        writer.WriteField(273, _time);
        return writer.Position;
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class NumericWriterBenchmarks
{
    private readonly byte[] _destination = new byte[128];
    private long _price = 123456700;
    private long _quantity = 1000;

    [Benchmark(Baseline = true)]
    public int Decimal()
    {
        var writer = new FixSpanWriter(_destination);
        writer.WriteField(270, MarketDataWriterBenchmarks.ExactPrice(_price));
        writer.WriteField(271, (decimal)_quantity);
        return writer.Position;
    }

    [Benchmark]
    public int ScaledAndIntegral()
    {
        var writer = new FixSpanWriter(_destination);
        writer.WriteField(270, _price, 4);
        writer.WriteField(271, _quantity);
        return writer.Position;
    }
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class MarketDataWriterBenchmarks
{
    private readonly byte[] _destination = new byte[16384];
    private readonly DateOnly _date = new(2024, 2, 29);
    private readonly TimeOnly _time = new(23, 59, 59, 123);
    private readonly DateTime _expiry = new(2024, 3, 1, 0, 0, 0, 987, DateTimeKind.Utc);
    private long _price = 123456700;
    private long _quantity = 1000;

    [Params(10, 50)]
    public int Entries { get; set; }

    // Exact decimal construction, including the caller's original scale (not decimal division).
    internal static decimal ExactPrice(long mantissa) =>
        new(unchecked((int)mantissa), (int)(mantissa >> 32), 0, false, 4);

    internal byte[] CreateXFrame() => _destination.AsSpan(0, X_ScaledAndIntegral()).ToArray();
    internal byte[] CreateWFrame() => _destination.AsSpan(0, W_ScaledAndIntegral()).ToArray();

    [GlobalSetup]
    public void CheckEquivalentFrames()
    {
        byte[] x = _destination.AsSpan(0, X_Decimal()).ToArray();
        if (!x.AsSpan().SequenceEqual(_destination.AsSpan(0, X_ScaledAndIntegral())))
            throw new InvalidOperationException("X numeric APIs produced different frames.");
        byte[] w = _destination.AsSpan(0, W_Decimal()).ToArray();
        if (!w.AsSpan().SequenceEqual(_destination.AsSpan(0, W_ScaledAndIntegral())))
            throw new InvalidOperationException("W numeric APIs produced different frames.");
    }

    [Benchmark]
    public int X_Decimal() => WriteX(false);

    [Benchmark]
    public int X_ScaledAndIntegral() => WriteX(true);

    [Benchmark]
    public int W_Decimal() => WriteW(false);

    [Benchmark]
    public int W_ScaledAndIntegral() => WriteW(true);

    private int WriteX(bool scaled)
    {
        var writer = new MarketDataIncrementalRefreshWriter(_destination);
        writer.WriteTradeDate(_date);
        writer.WriteNoMDEntries(Entries);
        for (int i = 0; i < Entries; i++)
        {
            writer.WriteMDUpdateAction(MDUpdateAction.New);
            writer.WriteMDEntryType(MDEntryType.Bid);
            writer.WriteMDEntryID("1234567890123456789"u8);
            writer.WriteSymbol("SYMBOL"u8);
            if (scaled)
            {
                writer.WriteMDEntryPx(_price + i, 4);
                writer.WriteMDEntrySize(_quantity + i);
            }
            else
            {
                writer.WriteMDEntryPx(ExactPrice(_price + i));
                writer.WriteMDEntrySize((decimal)(_quantity + i));
            }
            writer.WriteMDEntryDate(_date);
            writer.WriteMDEntryTime(_time);
            writer.WriteExpireDate(_date);
            writer.WriteExpireTime(_expiry);
            writer.WriteOrderID("9223372036854775807"u8);
            writer.WriteNumberOfOrders(i + 1);
        }
        return writer.Finish();
    }

    private int WriteW(bool scaled)
    {
        var writer = new MarketDataSnapshotFullRefreshWriter(_destination);
        writer.WriteTradeDate(_date);
        writer.WriteSymbol("SYMBOL"u8);
        writer.WriteNoMDEntries(Entries);
        for (int i = 0; i < Entries; i++)
        {
            writer.WriteMDEntryType(MDEntryType.Bid);
            writer.WriteMDEntryID("1234567890123456789"u8);
            if (scaled)
            {
                writer.WriteMDEntryPx(_price + i, 4);
                writer.WriteMDEntrySize(_quantity + i);
            }
            else
            {
                writer.WriteMDEntryPx(ExactPrice(_price + i));
                writer.WriteMDEntrySize((decimal)(_quantity + i));
            }
            writer.WriteMDEntryDate(_date);
            writer.WriteMDEntryTime(_time);
            writer.WriteExpireDate(_date);
            writer.WriteExpireTime(_expiry);
            writer.WriteOrderID("9223372036854775807"u8);
            writer.WriteNumberOfOrders(i + 1);
        }
        return writer.Finish();
    }
}
