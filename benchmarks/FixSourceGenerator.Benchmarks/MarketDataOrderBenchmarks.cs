using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;

namespace FixSourceGenerator.Benchmarks;

public enum EntryFieldOrder
{
    Writer,
    TagAscending,
    ShuffledFixed,
    ShuffledPerEntry,
}

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class MarketDataOrderBenchmarks
{
    private byte[] _x = null!;
    private byte[] _w = null!;
    private MarketDataReaderBenchmarks _reader = null!;

    [Params(50)]
    public int Entries { get; set; }

    [ParamsAllValues]
    public EntryFieldOrder Order { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var writer = new MarketDataWriterBenchmarks { Entries = Entries };
        byte[] x = writer.CreateXFrame(), w = writer.CreateWFrame();
        _reader = new MarketDataReaderBenchmarks { Entries = Entries };
        _reader.SetFrames(x, w);
        decimal expectedX = _reader.DecodeX(), expectedW = _reader.DecodeW();

        _x = Reorder(x, 279, Entries, Order);
        _w = Reorder(w, 269, Entries, Order);
        _reader.SetFrames(_x, _w);
        decimal actualX = _reader.DecodeX(), actualW = _reader.DecodeW();
        decimal projectedX = ProjectSinglePass(_x, incremental: true);
        decimal projectedW = ProjectSinglePass(_w, incremental: false);
        if (actualX != expectedX || actualW != expectedW
            || projectedX != expectedX || projectedW != expectedW)
            throw new InvalidOperationException(
                $"Order {Order}: X expected={expectedX}, generated={actualX}, projected={projectedX}; "
                + $"W expected={expectedW}, generated={actualW}, projected={projectedW}.");
    }

    [Benchmark]
    public int SliceX() => _reader.SliceX();

    [Benchmark]
    public int SliceW() => _reader.SliceW();

    [Benchmark]
    public decimal DecodeGeneratedX() => _reader.DecodeX();

    [Benchmark]
    public decimal DecodeGeneratedW() => _reader.DecodeW();

    [Benchmark]
    public decimal ProjectSinglePassX() => ProjectSinglePass(_x, incremental: true);

    [Benchmark]
    public decimal ProjectSinglePassW() => ProjectSinglePass(_w, incremental: false);

    // Fixture-specific projection, not a replacement for the general nested-group reader API.
    internal static decimal ProjectSinglePass(ReadOnlySpan<byte> frame, bool incremental,
        bool checkEntryCount = true, bool includeTemporal = true)
    {
        decimal total = 0;
        int expectedEntries = -1, entries = 0;
        var scanner = new FixSpanReader(frame);
        while (scanner.TryReadNext(out int tag, out var value))
        {
            switch (tag)
            {
                case 268:
                    expectedEntries = FixSpanReader.ParseInt(value);
                    total += expectedEntries;
                    break;
                case 279:
                    if (!incremental)
                        throw new InvalidOperationException("Unexpected update action in W fixture.");
                    entries++;
                    total += FixSpanReader.ParseByte(value);
                    break;
                case 269:
                    if (!incremental) entries++;
                    total += FixSpanReader.ParseByte(value);
                    break;
                case 37:
                case 55:
                case 278:
                    total += value.Length + value[0];
                    break;
                case 270:
                case 271:
                    total += FixSpanReader.ParseDecimal(value);
                    break;
                case 346:
                    total += FixSpanReader.ParseInt(value);
                    break;
                case 75:
                case 272:
                case 432:
                    if (includeTemporal) total += FixSpanReader.ParseDateOnly(value).DayNumber;
                    break;
                case 273:
                    if (includeTemporal) total += FixSpanReader.ParseTimeOnly(value).Ticks;
                    break;
                case 126:
                    if (includeTemporal) total += FixSpanReader.ParseDateTime(value).Ticks;
                    break;
                case 779:
                    if (!incremental && includeTemporal) total += FixSpanReader.ParseDateTime(value).Ticks;
                    break;
            }
        }
        if (checkEntryCount && entries != expectedEntries)
            throw new InvalidOperationException("Projected group count mismatch.");
        return total;
    }

    internal static byte[] Reorder(byte[] frame, int delimiter, int entries, EntryFieldOrder order)
    {
        byte[] result = (byte[])frame.Clone();
        int position = 0;
        bool foundCounter = false;
        while (FixSpanReader.TryReadField(frame, position, out int tag, out int start, out int length, out int next))
        {
            position = next;
            if (tag != 268) continue;
            if (FixSpanReader.ParseInt(frame.AsSpan(start, length)) != entries)
                throw new InvalidOperationException("Unexpected fixture group count.");
            foundCounter = true;
            break;
        }
        if (!foundCounter)
            throw new InvalidOperationException("Fixture group counter missing.");

        for (int entry = 0; entry < entries; entry++)
        {
            if (!FixSpanReader.TryReadField(frame, position, out int tag, out _, out _, out int next)
                || tag != delimiter)
                throw new InvalidOperationException("Fixture entry delimiter missing.");

            // Leave the first field in place; only permute fields within this entry.
            position = next;
            int output = position;
            var fields = new List<(int Tag, int Start, int Length)>();
            while (FixSpanReader.TryReadField(frame, position, out tag, out _, out _, out next)
                && tag != delimiter && tag != 10)
            {
                fields.Add((tag, position, next - position));
                position = next;
            }

            switch (order)
            {
                case EntryFieldOrder.Writer:
                    break;
                case EntryFieldOrder.TagAscending:
                    fields.Sort(static (left, right) => left.Tag.CompareTo(right.Tag));
                    break;
                case EntryFieldOrder.ShuffledFixed:
                case EntryFieldOrder.ShuffledPerEntry:
                    var random = new Random(order == EntryFieldOrder.ShuffledFixed ? 42 : 42 + entry);
                    for (int i = fields.Count - 1; i > 0; i--)
                    {
                        int j = random.Next(i + 1);
                        (fields[i], fields[j]) = (fields[j], fields[i]);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(order));
            }
            foreach (var field in fields)
            {
                frame.AsSpan(field.Start, field.Length).CopyTo(result.AsSpan(output));
                output += field.Length;
            }
            if (output != position)
                throw new InvalidOperationException("Entry byte length changed.");
        }

        if (!FixSpanReader.TryReadField(result, position, out int trailerTag,
            out int checksumStart, out int checksumLength, out int end)
            || trailerTag != 10 || end != result.Length)
            throw new InvalidOperationException("Unexpected fixture trailer.");
        int sum = 0;
        foreach (byte value in result.AsSpan(0, position)) sum += value;
        if ((sum & 255) != FixSpanReader.ParseInt(result.AsSpan(checksumStart, checksumLength)))
            throw new InvalidOperationException("Reordering changed the FIX checksum.");
        return result;
    }
}
