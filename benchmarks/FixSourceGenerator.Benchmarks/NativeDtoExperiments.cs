using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FixSourceGenerator.Attributes;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;
using Native = FixSourceGenerator.Benchmarks.Generated.Fix.V42;
using NativeRuntime = FixSourceGenerator.Benchmarks.Generated.Fix.V42.Runtime;

namespace FixSourceGenerator.Benchmarks;

// Dense schema ordinals, not FIX tag numbers. Slots remain allocated when absent.
internal sealed class NativeEntryDto
{
    internal const ulong IdBit = 1, PriceBit = 2, SizeBit = 4;
    internal string Id = "";
    internal decimal Price, Size;
    internal ulong Presence, Dirty;

    internal void Materialize(string id, decimal? price, decimal? size)
    {
        Id = id;
        Price = price.GetValueOrDefault();
        Size = size.GetValueOrDefault();
        Presence = IdBit | (price.HasValue ? PriceBit : 0) | (size.HasValue ? SizeBit : 0);
        Dirty = 0;
    }

    internal void SetId(string value, bool track = true)
    {
        if ((Presence & IdBit) != 0 && Id == value) return;
        Id = value;
        Presence |= IdBit;
        if (track) Dirty |= IdBit;
    }

    internal void SetPrice(decimal? value, bool track = true) =>
        SetNumber(ref Price, PriceBit, value, track);

    internal void SetSize(decimal? value, bool track = true) =>
        SetNumber(ref Size, SizeBit, value, track);

    private void SetNumber(ref decimal slot, ulong bit, decimal? value, bool track)
    {
        bool wasPresent = (Presence & bit) != 0;
        if (wasPresent == value.HasValue && (!wasPresent || slot == value!.Value)) return;
        slot = value.GetValueOrDefault();
        Presence = value.HasValue ? Presence | bit : Presence & ~bit;
        if (track) Dirty |= bit;
    }

    internal decimal Sum => ((Presence & PriceBit) != 0 ? Price : 0m) +
        ((Presence & SizeBit) != 0 ? Size : 0m) + Id.Length;
}

[FixView("NewOrderSingle")]
public readonly ref partial struct NativeSmallProjection
{
    public partial ReadOnlySpan<byte> ClOrdID { get; }
    public partial decimal? Price { get; }
    public partial decimal OrderQty { get; }
}

[FixView("MarketDataIncrementalRefresh.MDIncGrp.NoMDEntries")]
public readonly ref partial struct NativeXProjection
{
    public partial ReadOnlySpan<byte> MDEntryID { get; }
    public partial decimal? MDEntryPx { get; }
    public partial decimal? MDEntrySize { get; }
}

// Small and X are selected-field projections only; never serialized as full market messages.
internal sealed class NativeProjectionWorkload
{
    private readonly byte[] _frame;
    private readonly bool _small;
    private readonly int _count;
    private readonly List<NativeEntryDto> _destination;
    private readonly List<NativeEntryDto> _steady;
    private readonly (decimal Price, decimal Size, int IdLength)[] _cached;
    private readonly (int Start, int Length)[] _ranges;
    private List<NativeEntryDto>? _last;
    internal int Uses = 1;

    internal NativeProjectionWorkload(string scenario)
    {
        _small = scenario == "Small";
        _frame = new CombinedCodecWorkload(scenario).Frame.ToArray();
        int count = _small ? 1 : int.Parse(scenario[1..], CultureInfo.InvariantCulture);
        _count = count;
        _destination = new List<NativeEntryDto>(count);
        _steady = Acquire(null);
        _cached = _steady.Select(row => (row.Price, row.Size, row.Id.Length)).ToArray();
        _ranges = new (int, int)[count];
        if (_small) _ranges[0] = (0, _frame.Length);
        else
        {
            int i = 0;
            var iterator = new MDIncGrpReader(_frame).NoMDEntries.GetEnumerator();
            while (iterator.MoveNext())
            {
                ReadOnlySpan<byte> span = iterator.CurrentSpan;
                NativeDtoExperiments.Require(((ReadOnlySpan<byte>)_frame).Overlaps(span, out int start),
                    "Generated entry span must alias the input");
                _ranges[i++] = (start, span.Length);
            }
        }
        for (int i = 0; i < _ranges.Length; i++)
        {
            var span = _frame.AsSpan(_ranges[i].Start, _ranges[i].Length);
            if (_small)
            {
                var view = new NativeSmallProjection(span);
                CheckRow(_steady[i], view.ClOrdID, view.Price, view.OrderQty);
            }
            else
            {
                var view = new NativeXProjection(span);
                CheckRow(_steady[i], view.MDEntryID, view.MDEntryPx, view.MDEntrySize);
            }
        }
        for (int uses = 1; uses <= 64; uses *= 4)
        {
            Uses = uses;
            decimal expected = GeneratedCached();
            NativeDtoExperiments.Require(GeneratedGetters() == expected && FreshAndRead() == expected &&
                ReuseAndRead() == expected && DtoOnly() == expected && CachedOnly() == expected &&
                IndexedViewsOnly() == expected && FreshAndReadCached() == expected &&
                ReuseAndReadCached() == expected && DtoCachedOnly() == expected, "Native projection digests");
        }
        Uses = 1;
    }

    private static void CheckRow(NativeEntryDto row, ReadOnlySpan<byte> id, decimal? price, decimal? size)
    {
        ulong presence = 1UL | (price.HasValue ? 2UL : 0) | (size.HasValue ? 4UL : 0);
        NativeDtoExperiments.Require(row.Presence == presence && row.Dirty == 0 &&
            row.Price == price.GetValueOrDefault() && row.Size == size.GetValueOrDefault() &&
            row.Id == Encoding.UTF8.GetString(id), "Per-entry native ownership/value/presence");
    }

    private List<NativeEntryDto> Acquire(List<NativeEntryDto>? destination)
    {
        destination ??= new List<NativeEntryDto>(_count);
        int count = 0;
        if (_small)
        {
            var view = new NativeSmallProjection(_frame);
            LoadRow(destination, count++, view.ClOrdID, view.Price, view.OrderQty);
        }
        else
        {
            var iterator = new MDIncGrpReader(_frame).NoMDEntries.GetEnumerator();
            while (iterator.MoveNext())
            {
                var view = new NativeXProjection(iterator.CurrentSpan);
                LoadRow(destination, count++, view.MDEntryID, view.MDEntryPx, view.MDEntrySize);
            }
        }
        if (destination.Count > count) destination.RemoveRange(count, destination.Count - count);
        return destination;
    }

    private static void LoadRow(List<NativeEntryDto> rows, int index, ReadOnlySpan<byte> id,
        decimal? price, decimal? size)
    {
        if (index == rows.Count) rows.Add(new NativeEntryDto());
        rows[index].Materialize(Encoding.UTF8.GetString(id), price, size);
    }

    internal decimal AcquireFresh()
    {
        _last = Acquire(null);
        return _last.Count;
    }

    internal decimal AcquireReuse()
    {
        _last = Acquire(_destination);
        return _last.Count;
    }

    internal decimal FreshAndRead() => Read(Acquire(null));
    internal decimal ReuseAndRead() => Read(Acquire(_destination));
    internal decimal DtoOnly() => Read(_steady);
    internal decimal FreshAndReadCached() => Read(Acquire(null), true);
    internal decimal ReuseAndReadCached() => Read(Acquire(_destination), true);
    internal decimal DtoCachedOnly() => Read(_steady, true);

    private decimal Read(List<NativeEntryDto> rows, bool cache = false)
    {
        decimal sum = 0;
        foreach (var row in rows)
        {
            decimal price = cache && (row.Presence & NativeEntryDto.PriceBit) != 0 ? row.Price : 0m;
            decimal size = cache && (row.Presence & NativeEntryDto.SizeBit) != 0 ? row.Size : 0m;
            int length = cache ? row.Id.Length : 0;
            for (int i = 0; i < Uses; i++) sum += cache ? price + size + length : row.Sum;
        }
        return sum;
    }

    internal decimal CachedOnly()
    {
        decimal sum = 0;
        foreach (var row in _cached)
            for (int i = 0; i < Uses; i++) sum += row.Price + row.Size + row.IdLength;
        return sum;
    }

    internal decimal IndexedViewsOnly()
    {
        decimal sum = 0;
        foreach (var range in _ranges)
        {
            var span = _frame.AsSpan(range.Start, range.Length);
            if (_small)
            {
                var view = new NativeSmallProjection(span);
                for (int i = 0; i < Uses; i++) sum += (view.Price ?? 0m) + view.OrderQty + view.ClOrdID.Length;
            }
            else
            {
                var view = new NativeXProjection(span);
                for (int i = 0; i < Uses; i++) sum += (view.MDEntryPx ?? 0m) +
                    (view.MDEntrySize ?? 0m) + view.MDEntryID.Length;
            }
        }
        return sum;
    }

    internal decimal GeneratedCached() => Generated(true);
    internal decimal GeneratedGetters() => Generated(false);

    private decimal Generated(bool cache)
    {
        decimal sum = 0;
        if (_small)
        {
            var view = new NativeSmallProjection(_frame);
            decimal price = cache ? view.Price ?? 0m : 0, size = cache ? view.OrderQty : 0;
            int length = cache ? view.ClOrdID.Length : 0;
            for (int i = 0; i < Uses; i++)
                sum += cache ? price + size + length : (view.Price ?? 0m) + view.OrderQty + view.ClOrdID.Length;
        }
        else
        {
            var iterator = new MDIncGrpReader(_frame).NoMDEntries.GetEnumerator();
            while (iterator.MoveNext())
            {
                var view = new NativeXProjection(iterator.CurrentSpan);
                decimal price = cache ? view.MDEntryPx ?? 0m : 0, size = cache ? view.MDEntrySize ?? 0m : 0;
                int length = cache ? view.MDEntryID.Length : 0;
                for (int i = 0; i < Uses; i++)
                    sum += cache ? price + size + length :
                        (view.MDEntryPx ?? 0m) + (view.MDEntrySize ?? 0m) + view.MDEntryID.Length;
            }
        }
        return sum;
    }
}

// Complete representation of the controlled UND schema, not of FIX X/W.
// Header edits and structural List mutations are outside dirty tracking; writer always uses Count.
internal sealed class NativeMessageDto
{
    internal string Sender = "", Target = "";
    internal int Sequence;
    internal DateTime SendingTime;
    internal readonly List<NativeEntryDto> Entries;
    internal NativeMessageDto(int capacity) => Entries = new(capacity);
}

internal sealed class NativeRoundtripWorkload
{
    internal readonly byte[] Original;
    internal readonly NativeMessageDto Dto;
    internal readonly byte[] Output = new byte[16384];
    private readonly NativeRuntime.FixWriterState[] _state =
        new NativeRuntime.FixWriterState[Native.NativeEnvelopeWriter.RequiredStateLength];
    private readonly byte[] _text = new byte[512];
    private readonly string[] _changes = ["G", "IDENTIFIER-GROWN-0000000000000000000000000000", "MID", "FINAL-IDENTIFIER"];
    private readonly int _count;

    internal NativeRoundtripWorkload(int count, bool dense = true)
    {
        _count = count;
        NativeRuntime.FixWriterState.Initialize(_state);
        Dto = new NativeMessageDto(count)
        {
            Sender = "SENDER", Target = "TARGET", Sequence = 7,
            SendingTime = new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc),
        };
        for (int i = 0; i < count; i++)
        {
            var entry = new NativeEntryDto();
            entry.Materialize($"ENTRY-{i:D3}", dense ? 101.25m + i : null, dense ? 100m : null);
            Dto.Entries.Add(entry);
        }
        Original = Output.AsSpan(0, Write(Dto, Output)).ToArray();
        NativeDtoExperiments.Require(Original.SequenceEqual(Oracle(Dto)),
            $"Initial native oracle: actual={Encoding.UTF8.GetString(Original).Replace('\x01', '|')}, " +
            $"expected={Encoding.UTF8.GetString(Oracle(Dto)).Replace('\x01', '|')}");
    }

    internal NativeMessageDto Acquire(NativeMessageDto? destination = null)
    {
        destination ??= new NativeMessageDto(_count);
        // Header ownership is included, not supplied out of band by the benchmark adapter.
        int position = 0;
        while (NativeRuntime.FixSpanReader.TryReadField(Original, position, out int tag,
            out int start, out int length, out int next))
        {
            ReadOnlySpan<byte> value = Original.AsSpan(start, length);
            if (tag == 49) destination.Sender = Encoding.UTF8.GetString(value);
            if (tag == 56) destination.Target = Encoding.UTF8.GetString(value);
            if (tag == 34) destination.Sequence = int.Parse(value, CultureInfo.InvariantCulture);
            if (tag == 52) destination.SendingTime = DateTime.ParseExact(
                Encoding.ASCII.GetString(value), "yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            position = next;
            if (tag == 100) break;
        }
        int index = 0;
        foreach (var row in new Native.NativeEnvelopeReader(Original).NoEntries)
        {
            if (index == destination.Entries.Count) destination.Entries.Add(new NativeEntryDto());
            destination.Entries[index++].Materialize(Encoding.UTF8.GetString(row.EntryID), row.Price, row.Size);
        }
        if (destination.Entries.Count > index) destination.Entries.RemoveRange(index, destination.Entries.Count - index);
        return destination;
    }

    internal int Write(NativeMessageDto dto, byte[] output)
    {
        // One reusable UTF8 scratch buffer; each generated call copies its span immediately.
        int targetLength = Encoding.UTF8.GetBytes(dto.Target, _text.AsSpan(256));
        var writer = new Native.NativeEnvelopeWriter(output, _state,
            Text(dto.Sender), _text.AsSpan(256, targetLength), dto.Sequence, dto.SendingTime);
        var group = writer.BeginNoEntries(dto.Entries.Count);
        foreach (var row in dto.Entries)
        {
            var entry = group.BeginEntry(Text(row.Id));
            if ((row.Presence & NativeEntryDto.PriceBit) != 0) entry.SetPrice(row.Price);
            if ((row.Presence & NativeEntryDto.SizeBit) != 0) entry.SetSize(row.Size);
            group = entry.EndEntry();
        }
        return Native.FixWriterScopeExtensions.EndGroup(group).Finish();
    }

    private ReadOnlySpan<byte> Text(string value) => _text.AsSpan(0, Encoding.UTF8.GetBytes(value, _text.AsSpan(0, 256)));

    internal decimal Run(bool acquire, bool reuseDto, bool freshOutput, bool each, bool track)
    {
        NativeMessageDto dto = acquire ? Acquire(reuseDto ? Dto : null) : Dto;
        byte[] output = freshOutput ? new byte[Output.Length] : Output;
        // Baseline reset is included on BOTH setup-excluded variants, so each call edits A -> B.
        if (!acquire)
        {
            dto.Entries[0].Materialize("ENTRY-000", 101.25m, 100m);
        }
        int total = 0;
        for (int i = 0; i < _changes.Length; i++)
        {
            dto.Entries[0].SetId(_changes[i], track);
            dto.Entries[0].SetPrice(i == 1 ? null : i == 2 ? 0m : 102.5m, track);
            if (each) total += Write(dto, output);
        }
        if (!each) total = Write(dto, output);
        return total;
    }

    internal static byte[] Oracle(NativeMessageDto dto)
    {
        string Number(decimal number) => number.ToString("0.############################", CultureInfo.InvariantCulture);
        var body = new StringBuilder($"35=UND|49={dto.Sender}|56={dto.Target}|34={dto.Sequence.ToString(CultureInfo.InvariantCulture)}|" +
            $"52={dto.SendingTime.ToString("yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture)}|100={dto.Entries.Count}|");
        foreach (var row in dto.Entries)
        {
            body.Append($"101={row.Id}|");
            if ((row.Presence & NativeEntryDto.PriceBit) != 0) body.Append($"270={Number(row.Price)}|");
            if ((row.Presence & NativeEntryDto.SizeBit) != 0) body.Append($"271={Number(row.Size)}|");
        }
        string text = body.ToString().Replace('|', '\x01');
        byte[] prefix = Encoding.UTF8.GetBytes($"8=FIX.4.2\u00019={Encoding.UTF8.GetByteCount(text)}\u0001{text}");
        int checksum = prefix.Sum(value => value) & 255;
        return [.. prefix, .. Encoding.ASCII.GetBytes($"10={checksum.ToString("D3", CultureInfo.InvariantCulture)}\x01")];
    }
}

internal static class NativeDtoExperiments
{
    internal static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    internal static void Check()
    {
        var row = new NativeEntryDto();
        row.Materialize("A", null, null);
        row.SetPrice(null);
        Require(row.Dirty == 0 && row.Presence == 1, "Clear absent no-op");
        row.SetPrice(0m);
        Require(row.Dirty == 2 && row.Presence == 3 && row.Price == 0, "Present zero");
        row.Dirty = 0;
        row.SetPrice(0m);
        row.SetId(new string('A', 1));
        Require(row.Dirty == 0, "Equal value no-op");
        row.SetPrice(1m);
        row.SetPrice(0m);
        Require(row.Dirty == 2, "A-B-A remains dirty");
        row.Dirty = 0;
        row.SetPrice(null);
        Require(row.Dirty == 2 && row.Presence == 1, "Clear present zero");
        row.Materialize("B", 5m, 6m);
        Require(row.Dirty == 0 && row.Presence == 7, "Materialization baseline");
        row.SetSize(null);
        Require(row.Dirty == 4 && row.Presence == 3, "Independent size bit");
        foreach (string scenario in new[] { "Small", "X1", "X10", "X50" }) _ = new NativeProjectionWorkload(scenario);
        foreach (int count in new[] { 1, 10, 50 })
            foreach (bool dense in new[] { false, true })
            {
                var work = new NativeRoundtripWorkload(count, dense);
                var dto = work.Acquire();
                Require(dto.Entries.All(entry => entry.Dirty == 0) &&
                    NativeRoundtripWorkload.Oracle(dto).SequenceEqual(work.Original), "Full acquisition");
                foreach (string id in new[] { "Z", "LONG-IDENTIFIER-000000000000000000000000000000000000000", "AÇÃO" })
                    foreach (decimal? price in new decimal?[] { null, 0m, -123.45m })
                    {
                        dto.Entries[0].SetId(id);
                        dto.Entries[0].SetPrice(price);
                        foreach (byte[] output in new[] { work.Output, new byte[work.Output.Length] })
                        {
                            int length = work.Write(dto, output);
                            Require(output.AsSpan(0, length).SequenceEqual(NativeRoundtripWorkload.Oracle(dto)),
                                "Complete writer vs independent byte/envelope oracle");
                            var iterator = new Native.NativeEnvelopeReader(output.AsSpan(0, length)).NoEntries.GetEnumerator();
                            Require(iterator.MoveNext() && iterator.Current.Price == price &&
                                Encoding.UTF8.GetString(iterator.Current.EntryID) == id, "Writer reader semantic values");
                        }
                    }
                dto.Entries.RemoveAt(0);
                Require(work.Output.AsSpan(0, work.Write(dto, work.Output)).SequenceEqual(NativeRoundtripWorkload.Oracle(dto)),
                    "Actual collection count including empty");
                dto.Entries.Add(row);
                Require(work.Output.AsSpan(0, work.Write(dto, work.Output)).SequenceEqual(NativeRoundtripWorkload.Oracle(dto)),
                    "Actual collection count after add");
            }
        foreach (int count in new[] { 1, 50 })
        {
            var work = new NativeRoundtripWorkload(count);
            decimal once = work.Run(true, true, false, false, true);
            byte[] expected = work.Output.AsSpan(0, (int)once).ToArray();
            foreach (bool acquire in new[] { false, true })
                foreach (bool track in new[] { false, true })
                {
                    Require(work.Run(acquire, true, false, false, track) == once &&
                        work.Output.AsSpan(0, (int)once).SequenceEqual(expected), "Edit variants equivalent");
                    work.Run(acquire, true, false, true, track);
                    Require(work.Output.AsSpan(0, (int)once).SequenceEqual(expected), "Serialize each final frame");
                    Require(expected.SequenceEqual(NativeRoundtripWorkload.Oracle(work.Dto)), "Edited independent oracle");
                }
        }
        Console.WriteLine("Native DTO checks: masks, projections, complete UND frames, UTF8, collection count, fresh/reused output.");
    }

    internal static void Load()
    {
        Check();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Environment = "native-dto", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ServerGC = System.Runtime.GCSettings.IsServerGC, Stopwatch.Frequency,
            WarmupSeconds = .2, BlockSeconds = .1, Rounds = 8,
        }));
        var cases = new List<Case>();
        foreach (string scenario in new[] { "Small", "X1", "X10", "X50" })
        {
            var acquisition = new NativeProjectionWorkload(scenario);
            cases.Add(new($"projected/{scenario}/acquire",
                ["Fresh", "ReuseObjects"], [acquisition.AcquireFresh, acquisition.AcquireReuse]));
            foreach (int uses in new[] { 1, 4, 16, 64 })
            {
                var work = new NativeProjectionWorkload(scenario) { Uses = uses };
                cases.Add(new($"projected/{scenario}/combined/uses{uses}",
                    ["GeneratedCached", "GeneratedGetters", "DtoFresh", "DtoReuseObjects",
                     "DtoFreshCached", "DtoReuseObjectsCached"],
                    [work.GeneratedCached, work.GeneratedGetters, work.FreshAndRead, work.ReuseAndRead,
                     work.FreshAndReadCached, work.ReuseAndReadCached]));
                if (scenario is "Small" or "X50")
                    cases.Add(new($"projected/{scenario}/steady/uses{uses}",
                        ["CachedValues", "IndexedViewGetters", "Dto", "DtoCached"],
                        [work.CachedOnly, work.IndexedViewsOnly, work.DtoOnly, work.DtoCachedOnly]));
            }
        }
        foreach (int count in new[] { 1, 50 })
        {
            var work = new NativeRoundtripWorkload(count);
            cases.Add(new($"full/UND{count}/four-edits",
                ["AcqFresh_WriteOnce", "AcqReuse_WriteOnce", "AcqFresh_FreshOutput_WriteOnce",
                 "AcqFresh_WriteEach", "Resident_WriteOnce", "Resident_WriteEach", "Resident_NoDirty_WriteOnce"],
                [() => work.Run(true, false, false, false, true),
                 () => work.Run(true, true, false, false, true),
                 () => work.Run(true, false, true, false, true),
                 () => work.Run(true, false, false, true, true),
                 () => work.Run(false, false, false, false, true),
                 () => work.Run(false, false, false, true, true),
                 () => work.Run(false, false, false, false, false)]));
        }
        Retention();
        decimal sink = 0;
        foreach (var item in cases)
            foreach (var action in item.Actions) Measure(action, .2, ref sink);
        for (int round = 0; round < 8; round++)
            foreach (var item in cases)
                for (int turn = 0; turn < item.Actions.Length; turn++)
                {
                    int variant = (round + (round % 2 == 0 ? turn : item.Actions.Length - 1 - turn)) % item.Actions.Length;
                    item.Samples[variant].Add(Measure(item.Actions[variant], .1, ref sink));
                }
        foreach (var item in cases)
            for (int i = 0; i < item.Actions.Length; i++)
            {
                var samples = item.Samples[i];
                double[] ordered = samples.Select(sample => sample.Us).Order().ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Case = item.Name, Method = item.Names[i], MedianBlockUs = (ordered[3] + ordered[4]) / 2,
                    MinBlockUs = ordered[0], MaxBlockUs = ordered[^1], Samples = samples,
                    BytesPerOperation = samples.Sum(sample => sample.Bytes) / (double)samples.Sum(sample => sample.Operations),
                    Gen0 = samples.Sum(sample => sample.Gen0), Gen1 = samples.Sum(sample => sample.Gen1),
                    Gen2 = samples.Sum(sample => sample.Gen2),
                }));
            }
        Console.WriteLine($"Sink={sink}");
    }

    private static Sample Measure(Func<decimal> action, double seconds, ref decimal sink)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), operations = 0;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long start = Stopwatch.GetTimestamp();
        do
        {
            for (int i = 0; i < 32; i++) sink += action();
            operations += 32;
        } while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds);
        double us = Stopwatch.GetElapsedTime(start).TotalSeconds * 1e6 / operations;
        return new(us, operations, GC.GetAllocatedBytesForCurrentThread() - allocated,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
    }

    private static void Retention()
    {
        // Allocation deltas with all objects kept alive measure shallow row size and complete
        // allocated graph bytes, not process RSS or a guess derived from GC.GetTotalMemory.
        _ = new NativeEntryDto();
        var rows = new NativeEntryDto[10000];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < rows.Length; i++) rows[i] = new NativeEntryDto();
        long rowBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / rows.Length;
        GC.KeepAlive(rows);
        foreach (bool dense in new[] { false, true })
            foreach (int count in new[] { 1, 50 })
            {
                var work = new NativeRoundtripWorkload(count, dense);
                _ = work.Acquire();
                var graphs = new NativeMessageDto[1000];
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < graphs.Length; i++) graphs[i] = work.Acquire();
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / graphs.Length;
                // x64 layout: 16-byte object headers, 24-byte arrays, 8-byte alignment.
                // Date parsing creates one nonretained timestamp string per acquisition.
                int StringBytes(string text) => (22 + 2 * text.Length + 7) & ~7;
                long retainedEstimate = 56 + 32 + 24 + 8 * count + rowBytes * count +
                    graphs[0].Entries.Sum(entry => StringBytes(entry.Id)) +
                    StringBytes(graphs[0].Sender) + StringBytes(graphs[0].Target);
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Retention = $"UND{count}/{(dense ? "dense" : "sparse")}",
                    ShallowRowBytesMeasured = rowBytes, NativeDecimalPayloadPerRow = 32,
                    MaskPayloadPerRow = 16, GraphAllocationBytesMeasured = bytes,
                    RetainedGraphBytesEstimateX64 = retainedEstimate,
                    PresentNumericFields = dense ? 2 * count : 0,
                    NumericSlots = 2 * count, InputBytes = work.Original.Length,
                    ReusedOutputCapacity = work.Output.Length,
                }));
                GC.KeepAlive(graphs);
            }
    }

    private sealed record Sample(double Us, long Operations, long Bytes, int Gen0, int Gen1, int Gen2);
    private sealed class Case(string name, string[] names, Func<decimal>[] actions)
    {
        internal string Name => name;
        internal string[] Names => names;
        internal Func<decimal>[] Actions => actions;
        internal List<Sample>[] Samples { get; } = actions.Select(_ => new List<Sample>()).ToArray();
    }
}
