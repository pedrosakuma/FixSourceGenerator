using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Attributes;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class DesignReadBenchmarks
{
    private byte[] _frame = null!;
    private DesignMembership _membership = null!;

    [Params("Small", "X1", "X10", "X50", "W1", "W10", "W50")]
    public string Scenario { get; set; } = "Small";

    [Params(1, 4)]
    public int Uses { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Initialize(new CombinedCodecWorkload(Scenario).Frame.ToArray());
    }

    internal void Initialize(byte[] frame)
    {
        _frame = frame;
        _membership = Scenario == "Small" ? DesignMembership.Scalar :
            DesignMembership.From(Scenario[0] == 'X'
                ? typeof(MDIncGrpReader.NoMDEntriesGroupReader)
                : typeof(MDFullGrpReader.NoMDEntriesGroupReader), 268, Scenario[0] == 'X' ? 279 : 269);
        decimal expected = GeneratedCached();
        if (GeneratedLazy() != expected || FusedLazy() != expected ||
            FusedTyped() != expected || Visitor() != expected)
            throw new InvalidOperationException("Design reader digests differ.");
    }

    [Benchmark]
    public decimal GeneratedLazy() => Generated(cache: false);

    [Benchmark(Baseline = true)]
    public decimal GeneratedCached() => Generated(cache: true);

    private decimal Generated(bool cache)
    {
        decimal total = 0;
        if (Scenario == "Small")
        {
            var view = new DesignSmallView(_frame);
            decimal price = cache ? view.Price ?? 0m : 0m;
            decimal size = cache ? view.OrderQty : 0m;
            for (int i = 0; i < Uses; i++)
                total += cache ? price + size : (view.Price ?? 0m) + view.OrderQty;
        }
        else if (Scenario[0] == 'X')
        {
            var iterator = new MDIncGrpReader(_frame).NoMDEntries.GetEnumerator();
            while (iterator.MoveNext())
            {
                var view = new XRepeatedProjection(iterator.CurrentSpan);
                decimal price = cache ? view.MDEntryPx ?? 0m : 0m;
                decimal size = cache ? view.MDEntrySize ?? 0m : 0m;
                for (int i = 0; i < Uses; i++)
                    total += cache ? price + size : (view.MDEntryPx ?? 0m) + (view.MDEntrySize ?? 0m);
            }
        }
        else
        {
            var iterator = new MDFullGrpReader(_frame).NoMDEntries.GetEnumerator();
            while (iterator.MoveNext())
            {
                var view = new DesignWView(iterator.CurrentSpan);
                decimal price = cache ? view.MDEntryPx ?? 0m : 0m;
                decimal size = cache ? view.MDEntrySize ?? 0m : 0m;
                for (int i = 0; i < Uses; i++)
                    total += cache ? price + size : (view.MDEntryPx ?? 0m) + (view.MDEntrySize ?? 0m);
            }
        }
        return total;
    }

    [Benchmark]
    public decimal FusedLazy() => Fused(typed: false, visitor: false);

    [Benchmark]
    public decimal FusedTyped() => Fused(typed: true, visitor: false);

    [Benchmark]
    public decimal Visitor() => Fused(typed: true, visitor: true);

    internal (int Rows, int Fields, int OuterBytes, int NestedSkippedBytes) CountWork()
    {
        var scanner = new DesignFusedEnumerator(_frame, _membership, Scenario == "Small" ? 44 : 270,
            Scenario == "Small" ? 38 : 271, countWork: true);
        int rows = 0;
        while (scanner.MoveNext()) rows++;
        return (rows, scanner.FieldsRead, scanner.TokenizedBytes, scanner.NestedBytesSkipped);
    }

    private decimal Fused(bool typed, bool visitor)
    {
        var scanner = new DesignFusedEnumerator(_frame, _membership, Scenario == "Small" ? 44 : 270,
            Scenario == "Small" ? 38 : 271, visitor, Uses);
        decimal total = 0;
        while (scanner.MoveNext())
        {
            if (visitor)
                total += scanner.VisitedTotal;
            else
            {
                var row = scanner.Current;
                var values = typed ? new DesignValues(row.Price, row.Size) : default;
                for (int i = 0; i < Uses; i++)
                    total += typed ? (values.Price ?? 0m) + (values.Size ?? 0m) :
                        (row.Price ?? 0m) + (row.Size ?? 0m);
            }
        }
        return total;
    }
}

internal readonly record struct DesignValues(decimal? Price, decimal? Size);

[FixView("NewOrderSingle")]
public readonly ref partial struct DesignSmallView
{
    public partial decimal? Price { get; }
    public partial decimal OrderQty { get; }
}

[FixView("MarketDataSnapshotFullRefresh.MDFullGrp.NoMDEntries")]
public readonly ref partial struct DesignWView
{
    public partial decimal? MDEntryPx { get; }
    public partial decimal? MDEntrySize { get; }
}

internal delegate int DesignSkipper(ReadOnlySpan<byte> buffer, int tag, int valueStart,
    int valueLength, int next, out int end);

internal sealed record DesignMembership(int Counter, int Delimiter, int[] Words, bool Bitmap, DesignSkipper? Skip)
{
    internal static readonly DesignMembership Scalar = new(0, 0, [], false, null);

    internal static DesignMembership From(Type group, int counter, int delimiter)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var field = group.GetField("EntryTagBits", flags) ?? group.GetField("EntryTags", flags)
            ?? throw new InvalidOperationException("Generated membership missing.");
        var skipper = (Delegate?)group.GetField("NestedGroupSkipper", flags)?.GetValue(null);
        return new(counter, delimiter, (int[])field.GetValue(null)!, field.Name == "EntryTagBits",
            skipper?.Method.CreateDelegate<DesignSkipper>());
    }

    internal bool Contains(int tag)
    {
        if (Bitmap)
        {
            uint word = (uint)tag >> 5;
            return word < (uint)Words.Length && (Words[(int)word] & (1 << (tag & 31))) != 0;
        }
        if (Words.Length > 16)
        {
            int low = 0, high = Words.Length - 1;
            while (low <= high)
            {
                int middle = low + ((high - low) >> 1);
                int value = Words[middle];
                if (value == tag) return true;
                if (value < tag) low = middle + 1;
                else high = middle - 1;
            }
            return false;
        }
        foreach (int value in Words)
            if (value == tag) return true;
        return false;
    }
}

internal readonly ref struct DesignRow(ReadOnlySpan<byte> frame, int priceStart, int priceLength,
    int sizeStart, int sizeLength)
{
    private readonly ReadOnlySpan<byte> _frame = frame;
    internal decimal? Price => Parse(_frame, priceStart, priceLength);
    internal decimal? Size => Parse(_frame, sizeStart, sizeLength);

    internal static decimal? Parse(ReadOnlySpan<byte> frame, int start, int length) =>
        start >= 0 && FixSpanReader.TryParseDecimal(frame.Slice(start, length), out decimal value) ? value : null;
}

// Research-only scanner: shares the generated enumerator's boundary policy, not a general
// FixView replacement. Market fixtures have no nested groups; the nested fixture supplies a skipper.
internal ref struct DesignFusedEnumerator
{
    private readonly ReadOnlySpan<byte> _frame;
    private readonly DesignMembership _membership;
    private readonly int _priceTag, _sizeTag, _uses;
    private readonly bool _visitor;
    private int _position, _remaining;
    private int _priceStart, _priceLength, _sizeStart, _sizeLength;
    internal decimal VisitedTotal { get; private set; }
    internal int FieldsRead { get; private set; }
    internal int TokenizedBytes { get; private set; }
    internal int NestedBytesSkipped { get; private set; }
    private readonly bool _countWork;

    internal DesignFusedEnumerator(ReadOnlySpan<byte> frame, DesignMembership membership,
        int priceTag, int sizeTag, bool visitor = false, int uses = 1, bool countWork = false)
    {
        this = default;
        _frame = frame;
        _membership = membership;
        _priceTag = priceTag;
        _sizeTag = sizeTag;
        _visitor = visitor;
        _uses = uses;
        _countWork = countWork;
        _position = membership.Counter == 0 ? 0 : frame.Length;
        _remaining = membership.Counter == 0 ? 1 : 0;
        if (membership.Counter != 0)
        {
            int pos = 0;
            while (Read(pos, out int tag, out int start, out int length, out int next))
            {
                if (tag == membership.Counter)
                {
                    FixSpanReader.TryParseInt(frame.Slice(start, length), out _remaining);
                    _position = next;
                    break;
                }
                pos = next;
            }
        }
    }

    internal DesignRow Current => new(_frame, _priceStart, _priceLength, _sizeStart, _sizeLength);

    internal bool MoveNext()
    {
        if (_remaining <= 0)
            return false;
        _priceStart = _sizeStart = -1;
        _priceLength = _sizeLength = 0;
        VisitedTotal = 0;
        if (!Read(_position, out int tag, out int start, out int length, out int next) ||
            (_membership.Counter != 0 && tag != _membership.Delimiter))
        {
            _remaining = 0;
            return false;
        }
        int cursor = _position;
        bool first = true;
        do
        {
            if (!first && _membership.Counter != 0 &&
                (tag == _membership.Delimiter || !_membership.Contains(tag)))
                break;
            first = false;
            int skipped = _membership.Skip?.Invoke(_frame, tag, start, length, next, out cursor) ?? 0;
            if (skipped < 0)
            {
                _remaining = 0;
                return false;
            }
            if (skipped == 0)
            {
                if (tag == _priceTag && _priceStart < 0)
                {
                    _priceStart = start;
                    _priceLength = length;
                    Visit(start, length);
                }
                if (tag == _sizeTag && _sizeStart < 0)
                {
                    _sizeStart = start;
                    _sizeLength = length;
                    Visit(start, length);
                }
                cursor = next;
            }
            else if (_countWork)
                NestedBytesSkipped += cursor - next;
        } while (Read(cursor, out tag, out start, out length, out next));
        _position = cursor;
        _remaining--;
        return true;
    }

    private void Visit(int start, int length)
    {
        if (!_visitor)
            return;
        decimal value = DesignRow.Parse(_frame, start, length) ?? 0m;
        for (int i = 0; i < _uses; i++)
            VisitedTotal += value;
    }

    private bool Read(int pos, out int tag, out int start, out int length, out int next)
    {
        bool read = FixSpanReader.TryReadField(_frame, pos, out tag, out start, out length, out next);
        if (read && _countWork)
        {
            FieldsRead++;
            TokenizedBytes += next - pos;
        }
        return read;
    }
}
