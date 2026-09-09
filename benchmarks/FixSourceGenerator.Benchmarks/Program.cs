using BenchmarkDotNet.Running;
using FixSourceGenerator.Benchmarks;

if (args is ["--writer-codegen"])
{
    var writer = new MarketDataWriterBenchmarks { Entries = 10 };
    writer.CheckEquivalentFrames();
    Console.WriteLine($"X={writer.X_ScaledAndIntegral()} W={writer.W_ScaledAndIntegral()}");
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
else if (args is ["--reader-fixview-check"])
{
    foreach (int entries in new[] { 1, 10, 50 })
    {
        var benchmark = new MarketDataReaderBenchmarks { Entries = entries };
        benchmark.Setup();
        benchmark.CheckProjectedDigests();
    }
    Console.WriteLine("Generated FixView projections agree with full X/W readers at 1/10/50 entries.");
}
else if (args is ["--reader-fixview-code-size"])
{
    Type xFullEntry = typeof(FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.MDIncGrpReader)
        .GetProperty("NoMDEntries")!.PropertyType.GetMethod("GetEnumerator")!.ReturnType
        .GetProperty("Current")!.PropertyType;
    Type wFullEntry = typeof(FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.MDFullGrpReader)
        .GetProperty("NoMDEntries")!.PropertyType.GetMethod("GetEnumerator")!.ReturnType
        .GetProperty("Current")!.PropertyType;
    foreach (Type type in new[]
    {
        typeof(XEntryProjection),
        typeof(WEntryProjection),
        typeof(XRepeatedProjection),
        xFullEntry,
        wFullEntry,
    })
    {
        int fields = type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Length;
        int constructorBytes = type.GetConstructors().Single().GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
        int methodBytes = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Sum(method => method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0);
        Console.WriteLine($"{type.Name}: state-fields={fields}, constructor-il-bytes={constructorBytes}, declared-method-il-bytes={methodBytes}");
    }
}
else
{
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

internal partial class Program
{
}
