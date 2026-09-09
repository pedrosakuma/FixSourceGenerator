using System.Reflection;
using FixSourceGenerator.Benchmarks;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;

internal sealed class DecodeLoad
{
    private static readonly DateOnly Date = new(2024, 2, 29);
    private static readonly TimeOnly Time = new(23, 59, 59, 123);
    private static readonly DateTime Expiry = new(2024, 3, 1, 0, 0, 0, 987, DateTimeKind.Utc);
    private readonly byte[] _x;
    private readonly byte[] _w;
    private readonly int _entries;

    public DecodeLoad(int entries, bool scaled = true)
    {
        _entries = entries;
        var benchmark = new MarketDataWriterBenchmarks { Entries = entries };
        benchmark.CheckEquivalentFrames();
        // Only fixture setup uses reflection; decoding runs on prebuilt caller-owned byte arrays.
        var field = typeof(MarketDataWriterBenchmarks).GetField("_destination", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Benchmark buffer field was not found.");
        var destination = (byte[])(field.GetValue(benchmark)
            ?? throw new InvalidOperationException("Benchmark buffer was null."));
        int xLength = scaled ? benchmark.X_ScaledAndIntegral() : benchmark.X_Decimal();
        _x = destination.AsSpan(0, xLength).ToArray();
        int wLength = scaled ? benchmark.W_ScaledAndIntegral() : benchmark.W_Decimal();
        _w = destination.AsSpan(0, wLength).ToArray();
    }

    public static void ValidateMatrix()
    {
        foreach (int entries in new[] { 1, 10, 50 })
        {
            var scaled = new DecodeLoad(entries);
            var original = new DecodeLoad(entries, scaled: false);
            Require(scaled._x.AsSpan().SequenceEqual(original._x), "X wire equivalence");
            Require(scaled._w.AsSpan().SequenceEqual(original._w), "W wire equivalence");
            Require(scaled.ReadX(true) == original.ReadX(true), "X decoded equivalence");
            Require(scaled.ReadW(true) == original.ReadW(true), "W decoded equivalence");
        }
    }

    public int DecodeX() => ReadX(false);
    public int DecodeW() => ReadW(false);
    internal byte[] XFrame => _x;
    internal byte[] WFrame => _w;
    internal int DecodeXWithoutInstrument() => ReadX(false, false);

    private int ReadX(bool validate, bool includeInstrument = true)
    {
        var reader = new MarketDataIncrementalRefreshReader(_x);
        DateOnly tradeDate = reader.TradeDate ?? throw new InvalidOperationException("TradeDate missing.");
        var group = reader.MDIncGrp.NoMDEntries;
        int declaredCount = group.Count;
        int count = 0, digest = tradeDate.DayNumber + declaredCount;
        decimal total = 0;
        foreach (var entry in group)
        {
            var action = entry.MDUpdateAction;
            var type = entry.MDEntryType ?? throw new InvalidOperationException("MDEntryType missing.");
            if (!entry.TryGetMDEntryID(out var id)) throw new InvalidOperationException("MDEntryID missing.");
            ReadOnlySpan<byte> symbol = default;
            if (includeInstrument && !entry.Instrument.TryGetSymbol(out symbol)) throw new InvalidOperationException("Symbol missing.");
            decimal price = entry.MDEntryPx ?? throw new InvalidOperationException("MDEntryPx missing.");
            decimal size = entry.MDEntrySize ?? throw new InvalidOperationException("MDEntrySize missing.");
            DateOnly date = entry.MDEntryDate ?? throw new InvalidOperationException("MDEntryDate missing.");
            TimeOnly time = entry.MDEntryTime ?? throw new InvalidOperationException("MDEntryTime missing.");
            DateOnly expireDate = entry.ExpireDate ?? throw new InvalidOperationException("ExpireDate missing.");
            DateTime expiry = entry.ExpireTime ?? throw new InvalidOperationException("ExpireTime missing.");
            if (!entry.TryGetOrderID(out var order)) throw new InvalidOperationException("OrderID missing.");
            int orders = entry.NumberOfOrders ?? throw new InvalidOperationException("NumberOfOrders missing.");
            if (validate)
            {
                Require(tradeDate == Date && declaredCount == _entries, "X header");
                Require(action == MDUpdateAction.New && type == MDEntryType.Bid, "X enums");
                Require(id.SequenceEqual("1234567890123456789"u8), "X MDEntryID");
                Require(symbol.SequenceEqual("SYMBOL"u8), "X group Symbol");
                CheckValues(count, price, size, date, time, expireDate, expiry, order, orders);
                Require(entry.HighPx is null && !entry.TryGetTradeID(out _), "X optional absence");
            }
            total += price + size;
            digest = unchecked(digest + id.Length + id[0] + symbol.Length + (symbol.IsEmpty ? 0 : symbol[0]) + order.Length +
                order[0] + orders + date.DayNumber + time.GetHashCode() + expireDate.DayNumber +
                expiry.GetHashCode() + (int)action + (int)type);
            count++;
        }
        if (validate) Require(count == _entries, "X enumerated count");
        return unchecked(digest + count + total.GetHashCode());
    }

    private int ReadW(bool validate)
    {
        var reader = new MarketDataSnapshotFullRefreshReader(_w);
        DateOnly tradeDate = reader.TradeDate ?? throw new InvalidOperationException("TradeDate missing.");
        if (!reader.Instrument.TryGetSymbol(out var symbol)) throw new InvalidOperationException("Symbol missing.");
        var group = reader.MDFullGrp.NoMDEntries;
        int declaredCount = group.Count;
        int count = 0, digest = tradeDate.DayNumber + declaredCount + symbol.Length + symbol[0];
        decimal total = 0;
        foreach (var entry in group)
        {
            var type = entry.MDEntryType;
            if (!entry.TryGetMDEntryID(out var id)) throw new InvalidOperationException("MDEntryID missing.");
            decimal price = entry.MDEntryPx ?? throw new InvalidOperationException("MDEntryPx missing.");
            decimal size = entry.MDEntrySize ?? throw new InvalidOperationException("MDEntrySize missing.");
            DateOnly date = entry.MDEntryDate ?? throw new InvalidOperationException("MDEntryDate missing.");
            TimeOnly time = entry.MDEntryTime ?? throw new InvalidOperationException("MDEntryTime missing.");
            DateOnly expireDate = entry.ExpireDate ?? throw new InvalidOperationException("ExpireDate missing.");
            DateTime expiry = entry.ExpireTime ?? throw new InvalidOperationException("ExpireTime missing.");
            if (!entry.TryGetOrderID(out var order)) throw new InvalidOperationException("OrderID missing.");
            int orders = entry.NumberOfOrders ?? throw new InvalidOperationException("NumberOfOrders missing.");
            if (validate)
            {
                Require(tradeDate == Date && declaredCount == _entries, "W header");
                Require(symbol.SequenceEqual("SYMBOL"u8), "W Symbol");
                Require(type == MDEntryType.Bid, "W enum");
                Require(id.SequenceEqual("1234567890123456789"u8), "W MDEntryID");
                CheckValues(count, price, size, date, time, expireDate, expiry, order, orders);
                Require(entry.HighPx is null && !entry.TryGetTradeID(out _), "W optional absence");
            }
            total += price + size;
            digest = unchecked(digest + id.Length + id[0] + order.Length + order[0] + orders +
                date.DayNumber + time.GetHashCode() + expireDate.DayNumber + expiry.GetHashCode() + (int)type);
            count++;
        }
        if (validate) Require(count == _entries, "W enumerated count");
        return unchecked(digest + count + total.GetHashCode());
    }

    private static void CheckValues(int index, decimal price, decimal size, DateOnly date, TimeOnly time,
        DateOnly expireDate, DateTime expiry, ReadOnlySpan<byte> order, int orders)
    {
        Require(price == (123456700m + index) / 10000m, "Price");
        Require(size == 1000m + index, "Size");
        Require(date == Date && time == Time && expireDate == Date, "Temporal fields");
        Require(expiry == Expiry && expiry.Kind == DateTimeKind.Utc, "Timestamp and UTC Kind");
        Require(order.SequenceEqual("9223372036854775807"u8), "OrderID bytes");
        Require(orders == index + 1, "NumberOfOrders");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Decode mismatch: " + message);
    }
}
