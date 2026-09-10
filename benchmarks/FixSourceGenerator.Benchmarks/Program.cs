using BenchmarkDotNet.Running;
using FixSourceGenerator.Benchmarks;

if (args is ["--writer-codegen"])
{
    var writer = new MarketDataWriterBenchmarks { Entries = 10 };
    writer.CheckEquivalentFrames();
    Console.WriteLine($"X={writer.X_ScaledAndIntegral()} W={writer.W_ScaledAndIntegral()}");
}
else if (args is ["--writer-pipeline-check"])
{
    foreach (int entries in new[] { 1, 10, 50 })
        foreach (string message in new[] { "X", "W" })
            new WriterPipelineBenchmarks { Entries = entries, Message = message }.Setup();
    Console.WriteLine("Raw, checked-context, scoped and in-place writer frames agree for X/W at 1/10/50 entries.");
}
else if (args is ["--reader-order-check"])
{
    foreach (int entries in new[] { 1, 10, 50 })
        foreach (var order in Enum.GetValues<EntryFieldOrder>())
            new MarketDataOrderBenchmarks { Entries = entries, Order = order }.Setup();
    Console.WriteLine("Reader order fixtures agree at 1/10/50 entries for all permutations and both decoding paths.");
}
else if (args is ["--reader-process-check"])
{
    foreach (int entries in new[] { 1, 10, 50 })
        foreach (var order in Enum.GetValues<EntryFieldOrder>())
            foreach (string message in new[] { "X", "W" })
                new MarketDataProcessBenchmarks { Entries = entries, Order = order, Message = message }.Setup();
    Console.WriteLine("Reader process projections agree at 1/10/50 entries for X/W and all field orders.");
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

internal partial class Program
{
}
