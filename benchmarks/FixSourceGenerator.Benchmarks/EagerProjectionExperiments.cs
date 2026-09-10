using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Native = FixSourceGenerator.Benchmarks.Generated.Fix.V42;

namespace FixSourceGenerator.Benchmarks;

internal sealed class EagerReadWorkload(byte[] input, int uses)
{
    internal int FullCached()
    {
        int digest = 0;
        foreach (var row in new Native.NativeEnvelopeReader(input).NoEntries)
        {
            var id = row.EntryID;
            decimal? price = row.Price, size = row.Size;
            for (int i = 0; i < uses; i++)
                digest = unchecked(digest + id.Length + (price?.GetHashCode() ?? -1) + (size?.GetHashCode() ?? -2));
        }
        return digest;
    }

    internal int Eager()
    {
        int digest = 0;
        foreach (var row in UndEagerValues.Enumerate(input))
        {
            var id = row.EntryID;
            decimal? price = row.Price, size = row.Size;
            for (int i = 0; i < uses; i++)
                digest = unchecked(digest + id.Length + (price?.GetHashCode() ?? -1) + (size?.GetHashCode() ?? -2));
        }
        return digest;
    }

    internal int LazyCached()
    {
        int digest = 0;
        var iterator = new Native.NativeEnvelopeReader(input).NoEntries.GetEnumerator();
        while (iterator.MoveNext())
        {
            var row = new UndLazyValues(iterator.CurrentSpan);
            var id = row.EntryID;
            decimal? price = row.Price, size = row.Size;
            for (int i = 0; i < uses; i++)
                digest = unchecked(digest + id.Length + (price?.GetHashCode() ?? -1) + (size?.GetHashCode() ?? -2));
        }
        return digest;
    }

    internal int PriceCached()
    {
        int digest = 0;
        foreach (var row in new Native.NativeEnvelopeReader(input).NoEntries)
        {
            decimal? price = row.Price;
            for (int i = 0; i < uses; i++) digest = unchecked(digest + (price?.GetHashCode() ?? -1));
        }
        return digest;
    }

    internal int PriceEager()
    {
        int digest = 0;
        foreach (var row in UndPriceValues.Enumerate(input))
        {
            decimal? price = row.Quote;
            for (int i = 0; i < uses; i++) digest = unchecked(digest + (price?.GetHashCode() ?? -1));
        }
        return digest;
    }
}

internal static class EagerProjectionExperiments
{
    internal static void Check()
    {
        DirectWriterExperiments.Check(eager: true);
        foreach (int count in new[] { 0, 1, 10, 50 })
            foreach (var density in Enum.GetValues<DirectDensity>())
                foreach (int uses in new[] { 1, 4 })
                {
                    var work = new DirectWriterWorkload(count, DirectEdit.None, density);
                    var read = new EagerReadWorkload(work.Input, uses);
                    Require(read.FullCached() == read.Eager() && read.LazyCached() == read.Eager() &&
                        read.PriceCached() == read.PriceEager(), "Cached read digest");
                }
        CheckRows("100=0|", []);
        CheckRows("100=1|101=A|", [new("A", false, null, false, null)]);
        CheckRows("100=2|101=A|271=0|270=1.25|270=99|101=B|270=0|",
            [new("A", true, 1.25m, true, 0m), new("B", true, 0m, false, null)], [99m, 0m]);
        CheckRows("100=2|101=AÇÃO|270=bad|270=8|271=-2|101=|271=|270=0|",
            [new("AÇÃO", true, null, true, -2m), new("", true, 0m, true, null)], [8m, 0m]);
        // The production scalar parser accepts a valid numeric prefix; do not silently change that contract.
        CheckRows("100=1|101=A|270=1junk|271=999999999999999999999999999999999999999|",
            [new("A", true, 1m, true, null)]);
        foreach (string invalid in new[]
        {
            "100=-1|", "100=1junk|", "100=2147483648|", "100=1|", "100=0|101=A|",
            "100=2|101=A|", "100=1|101=A|101=B|", "100=1|270=1|101=A|",
            "100=1|101=A|999=1|", "100=1|101=A|49=outside|", "100=1|101=A|270",
            "100=1|101=A|2x70=1|", "100=1|101=A|4294967566=1|",
            "100=2|101=A|270=1|101=B|271",
        })
            CheckInvalid(Encoding.ASCII.GetBytes(("35=UND|" + invalid + "10=000|").Replace('|', '\x01')));
        foreach (string invalid in new[] { "35=X|100=0|10=000|", "100=0|10=000|",
            "35=UND|100=0|10=000", "35=UND|100=1|101=A|10=000|271=8|",
            "35=UND|35=UND|100=0|10=000|", "35=UND|999=0|100=0|10=000|" })
            CheckInvalid(Encoding.ASCII.GetBytes(invalid.Replace('|', '\x01')));
        Console.WriteLine("Generated eager tuple/presence, alias selection, malformed/count/scope/Current checks passed.");
    }

    private sealed record Expected(string Id, bool HasPrice, decimal? Price, bool HasSize, decimal? Size);
    private static void CheckRows(string body, Expected[] expected, decimal?[]? fullReaderPrices = null)
    {
        byte[] input = Encoding.UTF8.GetBytes(("35=UND|" + body + "10=000|").Replace('|', '\x01'));
        int index = 0;
        var iterator = UndEagerValues.Enumerate(input);
        Require(!iterator.Current.HasEntryID && !iterator.Current.HasPrice, "Initial Current");
        while (iterator.MoveNext())
        {
            var row = iterator.Current;
            var e = expected[index++];
            Require(row.HasEntryID && Encoding.UTF8.GetString(row.EntryID) == e.Id &&
                row.HasPrice == e.HasPrice && row.Price == e.Price &&
                row.HasSize == e.HasSize && row.Size == e.Size, "Independent native tuple/presence");
            if (!row.EntryID.IsEmpty) Require(input.AsSpan().Overlaps(row.EntryID), "Borrowed text");
        }
        Require(index == expected.Length && !iterator.Current.HasEntryID && !iterator.MoveNext(), "Final Current/count");
        index = 0;
        foreach (var row in UndPriceValues.Enumerate(input))
        {
            var e = expected[index++];
            Require(row.HasQuote == e.HasPrice && row.Quote == e.Price, "Generated renamed narrow selection");
        }
        index = 0;
        var lazyIterator = new Native.NativeEnvelopeReader(input).NoEntries.GetEnumerator();
        while (lazyIterator.MoveNext())
        {
            var row = new UndLazyValues(lazyIterator.CurrentSpan);
            var e = expected[index++];
            Require(Encoding.UTF8.GetString(row.EntryID) == e.Id && row.Price == e.Price && row.Size == e.Size,
                "Existing FixView first-occurrence/nullable scalar contract");
        }
        Require(index == expected.Length, "Lazy view tuple count");
        index = 0;
        foreach (var row in new Native.NativeEnvelopeReader(input).NoEntries)
        {
            decimal? expectedPrice = fullReaderPrices == null ? expected[index].Price : fullReaderPrices[index];
            var e = expected[index++];
            Require(Encoding.UTF8.GetString(row.EntryID) == e.Id && row.Price == expectedPrice && row.Size == e.Size,
                "Existing full reader is last-occurrence (different from FixView/eager for duplicates)");
        }
        Require(index == expected.Length, "Existing reader tuple count");
    }

    private static void CheckInvalid(byte[] input)
    {
        UndEagerValues.Enumerator iterator;
        try { iterator = UndEagerValues.Enumerate(input); }
        catch (FormatException) { return; }
        try { while (iterator.MoveNext()) { } }
        catch (FormatException)
        {
            Require(!iterator.Current.HasEntryID && !iterator.Current.HasPrice && !iterator.Current.HasSize &&
                !iterator.MoveNext(), "Invalid/dangling lookahead must not expose previous Current");
            return;
        }
        throw new InvalidOperationException("Expected malformed input rejection");
    }

    private static void Require(bool valid, string label) => NativeDtoExperiments.Require(valid, label);

    internal static void Load(bool confirmation = false)
    {
        Check();
        double warmup = confirmation ? 1 : .2, block = confirmation ? 1 : .1;
        var cases = new List<Case>();
        foreach (int count in confirmation ? new[] { 50 } : new[] { 1, 10, 50 })
            foreach (var density in confirmation ? new[] { DirectDensity.Dense, DirectDensity.Sparse } :
                Enum.GetValues<DirectDensity>())
            {
                var direct = new DirectWriterWorkload(count, DirectEdit.None, density);
                foreach (int uses in confirmation ? new[] { 1 } : new[] { 1, 4 })
                {
                    var read = new EagerReadWorkload(direct.Input, uses);
                    cases.Add(new($"UND{count}/{density}/ReadAll/Uses{uses}",
                        ["FullCached", "LazyCached", "Eager"], [read.FullCached, read.LazyCached, read.Eager], direct));
                    if (!confirmation || density == DirectDensity.Sparse)
                        cases.Add(new($"UND{count}/{density}/ReadPrice/Uses{uses}",
                            ["PriceCached", "PriceEager"], [read.PriceCached, read.PriceEager], direct));
                }
            }
        foreach (int count in confirmation ? new[] { 50 } : new[] { 1, 10, 50 })
            foreach (var density in confirmation ? new[] { DirectDensity.Dense, DirectDensity.Mixed } :
                Enum.GetValues<DirectDensity>())
                foreach (var edit in new[] { DirectEdit.None, DirectEdit.FourEdits, DirectEdit.FilterSome })
                {
                    if (confirmation && density == DirectDensity.Mixed && edit != DirectEdit.FilterSome) continue;
                    var work = new DirectWriterWorkload(count, edit, density);
                    cases.Add(edit == DirectEdit.FilterSome
                        ? new($"UND{count}/{density}/{edit}", ["DirectReadWrite", "EagerReadWrite", "EagerPriceCountReadWrite"],
                            [work.DirectReadWrite, work.EagerReadWrite, work.EagerPriceCountReadWrite], work)
                        : new($"UND{count}/{density}/{edit}", ["DirectReadWrite", "EagerReadWrite"],
                            [work.DirectReadWrite, work.EagerReadWrite], work));
                }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Environment = "generated-eager", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ServerGC = System.Runtime.GCSettings.IsServerGC, Stopwatch.Frequency,
            Confirmation = confirmation, WarmupSeconds = warmup, BlockSeconds = block, Rounds = 8,
            Cases = cases.Count, ProcessId = System.Environment.ProcessId,
        }));
        long sink = 0;
        foreach (var item in cases)
            foreach (var action in item.Actions) Measure(action, warmup, ref sink);
        for (int round = 0; round < 8; round++)
            foreach (var item in cases)
                for (int turn = 0; turn < item.Actions.Length; turn++)
                {
                    int variant = (round + turn) % item.Actions.Length;
                    item.Samples[variant].Add(Measure(item.Actions[variant], block, ref sink));
                }
        foreach (var item in cases)
            for (int variant = 0; variant < item.Actions.Length; variant++)
            {
                var samples = item.Samples[variant];
                double[] ordered = samples.Select(s => s.Us).Order().ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Case = item.Name, Method = item.Methods[variant], InputBytes = item.Work.Input.Length,
                    OutputBytes = item.Name.Contains("/Read") ? 0 : item.Work.Expected.Length,
                    InputEntries = item.Work.Count, OutputEntries = item.Name.Contains("/Read") ? 0 : item.Work.Survivors,
                    MedianBlockUs = (ordered[3] + ordered[4]) / 2, MinBlockUs = ordered[0], MaxBlockUs = ordered[^1],
                    Samples = samples,
                    BytesPerOperation = samples.Sum(s => s.Bytes) / (double)samples.Sum(s => s.Operations),
                    Gen0 = samples.Sum(s => s.Gen0), Gen1 = samples.Sum(s => s.Gen1), Gen2 = samples.Sum(s => s.Gen2),
                }));
            }
        Console.WriteLine($"Sink={sink}");
    }

    private static Sample Measure(Func<int> action, double seconds, ref long sink)
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
    private sealed record Sample(double Us, long Operations, long Bytes, int Gen0, int Gen1, int Gen2);
    private sealed class Case(string name, string[] methods, Func<int>[] actions, DirectWriterWorkload work)
    {
        internal string Name => name;
        internal string[] Methods => methods;
        internal Func<int>[] Actions => actions;
        internal DirectWriterWorkload Work => work;
        internal List<Sample>[] Samples { get; } = actions.Select(_ => new List<Sample>(8)).ToArray();
    }
}
