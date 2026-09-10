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

    internal void CheckPipelineFrames(bool snapshot)
    {
        byte[] expected = snapshot ? CreateWFrame() : CreateXFrame();
        if (!expected.AsSpan().SequenceEqual(_destination.AsSpan(0, WriteRaw(snapshot))))
            throw new InvalidOperationException("Raw and scoped writer frames differ.");
        if (!expected.AsSpan().SequenceEqual(_destination.AsSpan(0, WriteWithContext(snapshot))))
            throw new InvalidOperationException("Checked-context and scoped writer frames differ.");
        if (!expected.AsSpan().SequenceEqual(_destination.AsSpan(0, WriteInPlace(snapshot))))
            throw new InvalidOperationException("In-place and scoped writer frames differ.");
    }

    internal int WriteInPlace(bool snapshot) => snapshot ? WriteW(true, true) : WriteX(true, true);

    internal int WriteCombined(bool snapshot, bool inPlace, Span<FixWriterState> state) =>
        snapshot ? WriteW(true, inPlace, state) : WriteX(true, inPlace, state);

    internal ReadOnlySpan<byte> WrittenFrame(int length) => _destination.AsSpan(0, length);

    internal void SetPriceOffset(int offset) => _price = 123456700 + offset;

    internal int WriteRaw(bool snapshot)
    {
        var writer = new FixSpanWriter(_destination);
        writer.BeginMessage("FIX.5.0"u8, snapshot ? "W"u8 : "X"u8);
        writer.WriteField("75="u8, _date);
        if (snapshot)
        {
            writer.WriteField("55="u8, "SYMBOL"u8);
            writer.WriteField("779="u8, _expiry);
        }
        writer.WriteField("268="u8, Entries);
        for (int i = 0; i < Entries; i++)
        {
            if (!snapshot)
                writer.WriteField("279="u8, (char)MDUpdateAction.New);
            writer.WriteField("269="u8, (char)MDEntryType.Bid);
            writer.WriteField("278="u8, "1234567890123456789"u8);
            if (!snapshot)
                writer.WriteField("55="u8, "SYMBOL"u8);
            writer.WriteField("270="u8, _price + i, 4);
            writer.WriteField("271="u8, _quantity + i);
            writer.WriteField("272="u8, _date);
            writer.WriteField("273="u8, _time);
            writer.WriteField("432="u8, _date);
            writer.WriteField("126="u8, _expiry);
            writer.WriteField("37="u8, "9223372036854775807"u8);
            writer.WriteField("346="u8, i + 1);
        }
        return writer.Finish();
    }

    internal int WriteWithContext(bool snapshot)
    {
        int stateLength = snapshot
            ? MarketDataSnapshotFullRefreshWriter.RequiredStateLength
            : MarketDataIncrementalRefreshWriter.RequiredStateLength;
        Span<FixWriterState> state = stackalloc FixWriterState[stateLength];
        FixWriterState.Initialize(state);
        var writer = FixWriterContext.Begin(_destination, state, stateLength, "FIX.5.0"u8, snapshot ? "W"u8 : "X"u8);
        writer.WriteField("75="u8, _date);
        if (snapshot)
        {
            writer.WriteField("55="u8, "SYMBOL"u8);
            writer.WriteField("779="u8, _expiry);
        }
        writer.BeginGroup("268="u8, Entries);
        for (int i = 0; i < Entries; i++)
        {
            writer.BeginEntry(snapshot ? 269 : 279);
            if (!snapshot)
                writer.WriteField("279="u8, (char)MDUpdateAction.New);
            writer.WriteField("269="u8, (char)MDEntryType.Bid);
            writer.WriteField("278="u8, "1234567890123456789"u8);
            if (!snapshot)
                writer.WriteField("55="u8, "SYMBOL"u8);
            writer.WriteField("270="u8, _price + i, 4);
            writer.WriteField("271="u8, _quantity + i);
            writer.WriteField("272="u8, _date);
            writer.WriteField("273="u8, _time);
            writer.WriteField("432="u8, _date);
            writer.WriteField("126="u8, _expiry);
            writer.WriteField("37="u8, "9223372036854775807"u8);
            writer.WriteField("346="u8, i + 1);
            writer.EndEntry();
        }
        writer.EndGroup();
        return writer.Finish();
    }

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

    private int WriteX(bool scaled, bool inPlace = false)
    {
        Span<FixWriterState> state = stackalloc FixWriterState[MarketDataIncrementalRefreshWriter.RequiredStateLength];
        MarketDataIncrementalRefreshWriter.InitializeState(state);
        return WriteX(scaled, inPlace, state);
    }

    private int WriteX(bool scaled, bool inPlace, Span<FixWriterState> state)
    {
        var writer = new MarketDataIncrementalRefreshWriter(_destination, state);
        var component = writer
            .SkipApplicationSequenceControl()
            .SkipMDBookType()
            .SkipMDFeedType()
            .SkipMDSubFeedType()
            .WriteTradeDate(_date)
            .SkipMDReqID()
            .SkipMarketID()
            .SkipMarketSegmentID()
            .BeginMDIncGrp();
        var group = component.BeginNoMDEntries(Entries);
        for (int i = 0; i < Entries; i++)
        {
            var entry = group.BeginEntry(MDUpdateAction.New);
            if (inPlace)
            {
                entry.SetMDEntryType(MDEntryType.Bid);
                entry.SetMDEntryID("1234567890123456789"u8);
            }
            else
            {
                entry = entry.WriteMDEntryType(MDEntryType.Bid).WriteMDEntryID("1234567890123456789"u8);
            }
            var instrument = entry.BeginInstrument();
            if (inPlace)
                instrument.SetSymbol("SYMBOL"u8);
            else
                instrument = instrument.WriteSymbol("SYMBOL"u8);
            var numeric = instrument.EndInstrument();
            if (inPlace)
            {
                numeric.SetMDEntryPx(_price + i, 4);
                numeric.SetMDEntrySize(_quantity + i);
                numeric.SetMDEntryDate(_date);
                numeric.SetMDEntryTime(_time);
                numeric.SetExpireDate(_date);
                numeric.SetExpireTime(_expiry);
                numeric.SetOrderID("9223372036854775807"u8);
                numeric.SetNumberOfOrders(i + 1);
                group = numeric.EndEntry();
                continue;
            }
            if (scaled)
            {
                var afterPrice = numeric.WriteMDEntryPx(_price + i, 4);
                var afterSize = afterPrice.WriteMDEntrySize(_quantity + i);
                group = afterSize
                    .WriteMDEntryDate(_date)
                    .WriteMDEntryTime(_time)
                    .WriteExpireDate(_date).WriteExpireTime(_expiry)
                    .WriteOrderID("9223372036854775807"u8)
                    .WriteNumberOfOrders(i + 1).EndEntry();
            }
            else
            {
                var afterPrice = numeric.WriteMDEntryPx(ExactPrice(_price + i));
                var afterSize = afterPrice.WriteMDEntrySize((decimal)(_quantity + i));
                group = afterSize
                    .WriteMDEntryDate(_date)
                    .WriteMDEntryTime(_time)
                    .WriteExpireDate(_date).WriteExpireTime(_expiry)
                    .WriteOrderID("9223372036854775807"u8)
                    .WriteNumberOfOrders(i + 1).EndEntry();
            }
        }
        return group.EndGroup().EndMDIncGrp().Finish();
    }

    private int WriteW(bool scaled, bool inPlace = false)
    {
        Span<FixWriterState> state = stackalloc FixWriterState[MarketDataSnapshotFullRefreshWriter.RequiredStateLength];
        MarketDataSnapshotFullRefreshWriter.InitializeState(state);
        return WriteW(scaled, inPlace, state);
    }

    private int WriteW(bool scaled, bool inPlace, Span<FixWriterState> state)
    {
        var writer = new MarketDataSnapshotFullRefreshWriter(_destination, state);
        var instrument = writer
            .SkipApplicationSequenceControl()
            .SkipTotNumReports()
            .SkipMDReportID()
            .SkipClearingBusinessDate()
            .SkipMDBookType()
            .SkipMDSubBookType()
            .SkipMarketDepth()
            .SkipMDFeedType()
            .SkipMDSubFeedType()
            .SkipRefreshIndicator()
            .WriteTradeDate(_date)
            .SkipMDReqID()
            .SkipMDStreamID()
            .SkipMarketID()
            .SkipMarketSegmentID()
            .BeginInstrument();
        if (inPlace)
            instrument.SetSymbol("SYMBOL"u8);
        else
            instrument = instrument.WriteSymbol("SYMBOL"u8);
        var afterInstrument = instrument.EndInstrument();
        var component = afterInstrument
            .SkipInstrumentExtension()
            .SkipFinancingDetails()
            .SkipUndInstrmtGrp()
            .SkipInstrmtLegGrp()
            .SkipRelatedInstrumentGrp(_expiry)
            .SkipFinancialStatus()
            .SkipCorporateAction()
            .SkipNetChgPrevDay()
            .SkipMDSecurityTradingStatus()
            .SkipMDHaltReason()
            .BeginMDFullGrp();
        var group = component.BeginNoMDEntries(Entries);
        for (int i = 0; i < Entries; i++)
        {
            var entry = group.BeginEntry(MDEntryType.Bid);
            if (inPlace)
            {
                entry.SetMDEntryID("1234567890123456789"u8);
                entry.SetMDEntryPx(_price + i, 4);
                entry.SetMDEntrySize(_quantity + i);
                entry.SetMDEntryDate(_date);
                entry.SetMDEntryTime(_time);
                entry.SetExpireDate(_date);
                entry.SetExpireTime(_expiry);
                entry.SetOrderID("9223372036854775807"u8);
                entry.SetNumberOfOrders(i + 1);
                group = entry.EndEntry();
                continue;
            }
            entry = entry.WriteMDEntryID("1234567890123456789"u8);
            if (scaled)
            {
                var afterPrice = entry.WriteMDEntryPx(_price + i, 4);
                var afterSize = afterPrice.WriteMDEntrySize(_quantity + i);
                group = afterSize
                    .WriteMDEntryDate(_date)
                    .WriteMDEntryTime(_time)
                    .WriteExpireDate(_date).WriteExpireTime(_expiry)
                    .WriteOrderID("9223372036854775807"u8)
                    .WriteNumberOfOrders(i + 1).EndEntry();
            }
            else
            {
                var afterPrice = entry.WriteMDEntryPx(ExactPrice(_price + i));
                var afterSize = afterPrice.WriteMDEntrySize((decimal)(_quantity + i));
                group = afterSize
                    .WriteMDEntryDate(_date)
                    .WriteMDEntryTime(_time)
                    .WriteExpireDate(_date).WriteExpireTime(_expiry)
                    .WriteOrderID("9223372036854775807"u8)
                    .WriteNumberOfOrders(i + 1).EndEntry();
            }
        }
        return group.EndGroup().EndMDFullGrp().Finish();
    }

}
