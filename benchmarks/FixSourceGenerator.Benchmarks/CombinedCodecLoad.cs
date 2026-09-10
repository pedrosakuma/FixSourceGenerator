using System.Diagnostics;
using System.Globalization;

namespace FixSourceGenerator.Benchmarks;

internal static class CombinedCodecLoad
{
    internal static void Check()
    {
        foreach (string scenario in new[] { "Small", "X1", "X10", "X50", "W1", "W10", "W50" })
        {
            var workload = new CombinedCodecWorkload(scenario);
            for (int i = 0; i < 2048; i++)
                _ = workload.Run((i & 1) == 0);
        }
        Console.WriteLine("Combined codec frames/digests agree across changing prices and reused state.");
    }

    internal static void Run(string scenario, string secondsText, string mode)
    {
        if (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
            seconds < 1 || seconds > 600)
            throw new ArgumentException("Duration must be 1-600 seconds.", nameof(secondsText));
        bool optimized = mode switch
        {
            "full" => false,
            "projected" => true,
            _ => throw new ArgumentException("Expected full or projected.", nameof(mode)),
        };
        var workload = new CombinedCodecWorkload(scenario);
        long warmupEnd = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < warmupEnd)
            _ = workload.Run(optimized);

        var samples = new long[65536];
        long operations = 0;
        int retained = 0;
        int nextSample = 0;
        Console.WriteLine($"Ready PID={Environment.ProcessId} scenario={scenario} mode={mode} generation={workload.Generation}");
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        long deadline = start + seconds * (long)Stopwatch.Frequency;
        try
        {
            while (true)
            {
                bool sample = (operations & 63) == 0;
                long sampleStart = sample ? Stopwatch.GetTimestamp() : 0;
                if (sample && sampleStart >= deadline)
                    break;
                _ = workload.Run(optimized);
                if (sample)
                {
                    samples[nextSample] = Stopwatch.GetTimestamp() - sampleStart;
                    nextSample = (nextSample + 1) % samples.Length;
                    retained = Math.Min(retained + 1, samples.Length);
                }
                operations++;
            }
        }
        catch (InvalidOperationException)
        {
            Console.Error.WriteLine($"FAILED scenario={scenario} operations={operations} generation={workload.Generation}");
            throw;
        }
        long end = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        if (retained == 0)
            throw new InvalidOperationException("No operations were sampled during the load window.");
        Array.Sort(samples, 0, retained);
        double elapsed = (end - start) / (double)Stopwatch.Frequency;
        double Percentile(double p) => samples[Math.Max(0, (int)Math.Ceiling(retained * p) - 1)] *
            1_000_000d / Stopwatch.Frequency;
        Console.WriteLine(FormattableString.Invariant(
            $"scenario={scenario} mode={mode} operations={operations} seconds={elapsed:F3} ops/s={operations / elapsed:F1} p50-us={Percentile(.50):F3} p95-us={Percentile(.95):F3} p99-us={Percentile(.99):F3} samples={retained} allocated-bytes={allocated} generation={workload.Generation} frame-bytes={workload.FrameLength}"));
    }
}
