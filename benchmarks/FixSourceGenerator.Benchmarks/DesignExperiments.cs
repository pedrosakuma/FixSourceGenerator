using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FixSourceGenerator.Attributes;
using Nested = FixSourceGenerator.Benchmarks.Generated.Fix.V43;

namespace FixSourceGenerator.Benchmarks;

internal static class DesignExperiments
{
    private static readonly string[] Scenarios = ["Small", "X1", "X10", "X50", "W1", "W10", "W50"];
    private static readonly string[] Changes = ["None", "Equal1", "Grow1", "Shrink1", "Equal8", "Grow8", "Shrink8"];

    internal static void Check()
    {
        foreach (string scenario in Scenarios)
        {
            byte[] original = new CombinedCodecWorkload(scenario).Frame.ToArray();
            foreach (int uses in new[] { 1, 4 })
            {
                var benchmark = new DesignReadBenchmarks { Scenario = scenario, Uses = uses };
                benchmark.Initialize(original);
                if (scenario != "Small")
                    foreach (EntryFieldOrder order in Enum.GetValues<EntryFieldOrder>())
                        benchmark.Initialize(MarketDataOrderBenchmarks.Reorder(original,
                            scenario[0] == 'X' ? 279 : 269, int.Parse(scenario[1..]), order));
            }
            foreach (string change in Changes)
                foreach (string placement in new[] { "First", "Last", "Spread" })
                    new DesignEditBenchmarks { Scenario = scenario, Change = change, Placement = placement }.Initialize(original);
        }
        CheckNested();
        NativeDtoExperiments.Check();
        DirectWriterExperiments.Check();
        EagerProjectionExperiments.Check();
        Console.WriteLine("Design fixtures agree: selected values, field orders, nested boundaries and edited envelopes.");
    }

    private static byte[] NestedFrame(string body)
    {
        body = "35=UDE|" + body;
        string prefix = $"8=FIX.4.3|9={Encoding.ASCII.GetByteCount(body)}|" + body;
        prefix = prefix.Replace('|', '\x01');
        int checksum = Encoding.ASCII.GetBytes(prefix).Sum(value => value) & 255;
        return Encoding.ASCII.GetBytes(prefix + "10=" + checksum.ToString("D3",
            System.Globalization.CultureInfo.InvariantCulture) + "\x01");
    }

    private static void CheckNested()
    {
        var membership = DesignMembership.From(typeof(Nested.DesignEnvelopeReader.NoEntriesGroupReader), 100, 101);
        (string Body, decimal Expected)[] cases =
        [
            ("100=0|", 0m),
            ("100=-1|", 0m),
            ("100=1|", 0m),
            ("100=1|101=P|271=2|", 2m),
            ("100=1|101=P|270=0|271=2|", 2m),
            ("100=1|101=P|270=1|270=999|271=2|", 3m),
            ("100=2|101=P|270=1|271=2|9999=stop|101=Q|270=3|271=4|", 3m),
            ("100=1|101=P|270=1|271=2|200=1|270=900|", 0m),
            ("100=2|101=P1|270=1|271=2|200=2|101=C1|270=900|271=901|" +
             "300=1|101=G|270=9999|271=9999|101=C2|270=902|271=903|" +
             "101=P2|270=3|271=4|9999=preserve|", 10m),
        ];
        foreach (var item in cases)
        {
            byte[] frame = NestedFrame(item.Body);
            decimal generated = 0;
            var generatedRows = new List<(decimal? Price, decimal? Size)>();
            var iterator = new Nested.DesignEnvelopeReader(frame).NoEntries.GetEnumerator();
            while (iterator.MoveNext())
            {
                var row = new DesignNestedView(iterator.CurrentSpan);
                generated += (row.MDEntryPx ?? 0m) + (row.MDEntrySize ?? 0m);
                generatedRows.Add((row.MDEntryPx, row.MDEntrySize));
            }
            foreach (bool visitor in new[] { false, true })
            {
                var fused = new DesignFusedEnumerator(frame, membership, 270, 271, visitor, countWork: true);
                decimal actual = 0;
                int index = 0;
                while (fused.MoveNext())
                {
                    actual += visitor ? fused.VisitedTotal : (fused.Current.Price ?? 0m) + (fused.Current.Size ?? 0m);
                    if (index >= generatedRows.Count ||
                        generatedRows[index] != (fused.Current.Price, fused.Current.Size))
                        throw new InvalidOperationException("Nested presence/value mismatch.");
                    index++;
                }
                if (generated != item.Expected || actual != generated || index != generatedRows.Count)
                    throw new InvalidOperationException($"Nested design mismatch: expected={item.Expected}, generated={generated}, fused={actual}.");
            }
            foreach (string change in Changes)
                if (item.Body.Contains("101=", StringComparison.Ordinal))
                    new DesignEditBenchmarks { Change = change }.Initialize(frame);
        }
        foreach (int bodyLength in new[] { 99, 100, 999, 1000 })
        {
            const string fields = "100=1|101=ABCDEFGHIJKLMNOP|270=1|271=2|9999=";
            int padding = bodyLength - Encoding.ASCII.GetByteCount("35=UDE|" + fields + "|");
            byte[] frame = NestedFrame(fields + new string('Z', padding) + "|");
            foreach (string change in Changes)
                new DesignEditBenchmarks { Change = change }.Initialize(frame);
        }
    }

    internal static void Load(string kind)
    {
        Check();
        var cases = new List<MeasurementCase>();
        foreach (string scenario in Scenarios)
        {
            if (kind == "read")
            {
                foreach (int uses in new[] { 1, 4 })
                {
                    var benchmark = new DesignReadBenchmarks { Scenario = scenario, Uses = uses };
                    benchmark.Setup();
                    if (uses == 1)
                    {
                        var work = benchmark.CountWork();
                        Console.WriteLine(JsonSerializer.Serialize(new { Work = scenario, work.Rows, work.Fields,
                            work.OuterBytes, work.NestedSkippedBytes }));
                    }
                    cases.Add(new($"{scenario}/uses{uses}",
                        ["GeneratedLazy", "GeneratedCached", "FusedLazy", "FusedTyped", "Visitor"],
                        [benchmark.GeneratedLazy, benchmark.GeneratedCached, benchmark.FusedLazy, benchmark.FusedTyped, benchmark.Visitor]));
                }
            }
            else if (kind == "edit")
            {
                foreach (string change in Changes)
                {
                    var benchmark = new DesignEditBenchmarks { Scenario = scenario, Change = change };
                    benchmark.Setup();
                    foreach (var operation in new (string Name, Func<int> Run)[]
                    {
                        ("ShiftEach", benchmark.ShiftEach), ("RebuildIndexed", benchmark.RebuildIndexed),
                        ("ScanAndRebuild", benchmark.ScanAndRebuild), ("ScanAndShift", benchmark.ScanAndShift),
                    })
                    {
                        int length = operation.Run();
                        Console.WriteLine(JsonSerializer.Serialize(new
                        {
                            Work = $"{scenario}/{change}/{operation.Name}", benchmark.InputBytes, OutputBytes = length,
                            benchmark.CopiedBytes, benchmark.IndexedBytes, benchmark.DistinctEditedFields,
                            benchmark.OutputCapacity, benchmark.RetainedPayloadBytes,
                        }));
                    }
                    cases.Add(new($"{scenario}/{change}", ["ShiftEach", "RebuildIndexed", "ScanAndRebuild", "ScanAndShift"],
                        [() => benchmark.ShiftEach(), () => benchmark.RebuildIndexed(),
                            () => benchmark.ScanAndRebuild(), () => benchmark.ScanAndShift()]));
                }
            }
            else
                throw new ArgumentException("Expected read or edit.", nameof(kind));
        }
        RunCases(cases);
    }

    internal static void Positions()
    {
        Check();
        var cases = new List<MeasurementCase>();
        foreach (string scenario in new[] { "Small", "X50", "W50" })
            foreach (string change in new[] { "Equal8", "Grow1", "Grow8", "Shrink8" })
                foreach (string placement in new[] { "First", "Last", "Spread" })
                {
                    var edit = new DesignEditBenchmarks { Scenario = scenario, Change = change, Placement = placement };
                    edit.Setup();
                    edit.ShiftEach();
                    long shiftedBytes = edit.CopiedBytes;
                    edit.RebuildIndexed();
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        Work = $"{scenario}/{change}/{placement}", ShiftCopiedBytes = shiftedBytes,
                        RebuildCopiedBytes = edit.CopiedBytes,
                    }));
                    cases.Add(new($"{scenario}/{change}/{placement}",
                        ["ShiftEach", "RebuildIndexed", "ScanAndShift", "ScanAndRebuild"],
                        [() => edit.ShiftEach(), () => edit.RebuildIndexed(), () => edit.ScanAndShift(), () => edit.ScanAndRebuild()]));
                }
        RunCases(cases, warmupSeconds: .1, blockSeconds: .05);
    }

    private static void RunCases(List<MeasurementCase> cases, double warmupSeconds = .2, double blockSeconds = .1)
    {
        decimal sink = 0;
        foreach (var item in cases)
            foreach (var action in item.Actions)
                Measure(action, warmupSeconds, ref sink);
        for (int round = 0; round < 8; round++)
            foreach (var item in cases)
                for (int turn = 0; turn < item.Actions.Length; turn++)
                {
                    // Rotate the starting variant; reverse direction on alternating rounds.
                    int variant = (round + (round % 2 == 0 ? turn : item.Actions.Length - 1 - turn)) % item.Actions.Length;
                    var result = Measure(item.Actions[variant], blockSeconds, ref sink);
                    item.Samples[variant].Add(result.Us);
                    item.Bytes[variant] += result.Bytes;
                }
        foreach (var item in cases)
            for (int variant = 0; variant < item.Actions.Length; variant++)
            {
                var sorted = item.Samples[variant].Order().ToArray();
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Case = item.Name, Method = item.Methods[variant], MedianBlockUs = (sorted[3] + sorted[4]) / 2,
                    MinBlockUs = sorted[0], MaxBlockUs = sorted[^1], SamplesUs = item.Samples[variant],
                    AllocatedBytes = item.Bytes[variant],
                }));
            }
        Console.WriteLine($"Sink={sink}");
    }

    internal static void Tail()
    {
        Check();
        foreach (int uses in new[] { 1, 4 })
        {
            var reader = new DesignReadBenchmarks { Scenario = "X50", Uses = uses };
            reader.Setup();
            Sample($"X50/uses{uses}/GeneratedCached", reader.GeneratedCached);
            Sample($"X50/uses{uses}/FusedTyped", reader.FusedTyped);
            Sample($"X50/uses{uses}/Visitor", reader.Visitor);
        }
        foreach (string change in new[] { "None", "Equal8", "Grow8", "Shrink8" })
        {
            var edit = new DesignEditBenchmarks { Scenario = "X50", Change = change };
            edit.Setup();
            Sample($"X50/{change}/ShiftEach", () => edit.ShiftEach());
            Sample($"X50/{change}/RebuildIndexed", () => edit.RebuildIndexed());
            Sample($"X50/{change}/ScanAndShift", () => edit.ScanAndShift());
            Sample($"X50/{change}/ScanAndRebuild", () => edit.ScanAndRebuild());
        }
    }

    private static void Sample(string name, Func<decimal> action)
    {
        decimal sink = 0;
        Measure(action, .5, ref sink);
        var samples = new long[8192];
        int retained = 0, next = 0;
        long operations = 0, allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp(), deadline = start + 3 * Stopwatch.Frequency;
        while (true)
        {
            bool sample = (operations & 63) == 0;
            long before = sample ? Stopwatch.GetTimestamp() : 0;
            if (sample && before >= deadline)
                break;
            sink += action();
            if (sample)
            {
                samples[next] = Stopwatch.GetTimestamp() - before;
                next = (next + 1) % samples.Length;
                retained = Math.Min(retained + 1, samples.Length);
            }
            operations++;
        }
        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (retained == 0)
            throw new InvalidOperationException("No latency samples.");
        Array.Sort(samples, 0, retained);
        double Percentile(double p) => samples[Math.Max(0, (int)Math.Ceiling(retained * p) - 1)] * 1e6 / Stopwatch.Frequency;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Case = name, Seconds = seconds, Operations = operations, Samples = retained,
            OpsPerSecond = operations / seconds, P50Us = Percentile(.5), P95Us = Percentile(.95),
            P99Us = Percentile(.99), AllocatedBytes = bytes, Sink = sink,
        }));
    }

    private static (double Us, long Bytes) Measure(Func<decimal> action, double seconds, ref decimal sink)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp(), count = 0;
        do
        {
            for (int i = 0; i < 32; i++)
                sink += action();
            count += 32;
        } while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds);
        return (Stopwatch.GetElapsedTime(start).TotalSeconds * 1e6 / count,
            GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private sealed class MeasurementCase(string name, string[] methods, Func<decimal>[] actions)
    {
        internal string Name => name;
        internal string[] Methods => methods;
        internal Func<decimal>[] Actions => actions;
        internal List<double>[] Samples { get; } = actions.Select(_ => new List<double>()).ToArray();
        internal long[] Bytes { get; } = new long[actions.Length];
    }
}

[FixView("DesignEnvelope.NoEntries")]
public readonly ref partial struct DesignNestedView
{
    public partial decimal? MDEntryPx { get; }
    public partial decimal? MDEntrySize { get; }
}
