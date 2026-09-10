using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal static class ResidualMembershipProbe
{
    internal static void Run(string baseline, string candidate)
    {
        var assemblies = new[] { baseline, candidate }.Select(path =>
            new VariantContext(path).LoadFromAssemblyPath(Path.GetFullPath(path))).ToArray();
        for (int variant = 0; variant < assemblies.Length; variant++)
            Metadata(assemblies[variant], variant);

        var cases = new List<(string Name, Func<decimal>[] Actions, List<double>[] Samples, long[] Bytes)>();
        foreach (string scenario in new[] { "Small", "X1", "X10", "X50", "W1", "W10", "W50" })
        {
            foreach (string method in new[] { "FluentFull", "InPlaceProjected" })
            {
                var actions = assemblies.Select(assembly =>
                {
                    var type = assembly.GetType("FixSourceGenerator.Benchmarks.CombinedCodecBenchmarks", true)!;
                    var instance = Activator.CreateInstance(type)!;
                    type.GetProperty("Scenario")!.SetValue(instance, scenario);
                    type.GetMethod("Setup")!.Invoke(instance, null);
                    return type.GetMethod(method)!.CreateDelegate<Func<decimal>>(instance);
                }).ToArray();
                cases.Add(($"{scenario}/{method}", actions, [new(), new()], new long[2]));
            }
        }
        foreach (int entries in new[] { 1, 10, 50 })
        {
            foreach (string method in new[] { "DecodeX", "DecodeW", "DecodeXProjected", "DecodeWProjected",
                "SliceX", "SliceW" })
            {
                var actions = assemblies.Select(assembly =>
                {
                    var type = assembly.GetType("FixSourceGenerator.Benchmarks.MarketDataReaderBenchmarks", true)!;
                    var instance = Activator.CreateInstance(type)!;
                    type.GetProperty("Entries")!.SetValue(instance, entries);
                    type.GetMethod("Setup")!.Invoke(instance, null);
                    var target = type.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!;
                    if (!method.StartsWith("Slice", StringComparison.Ordinal))
                        return target.CreateDelegate<Func<decimal>>(instance);
                    var slice = target.CreateDelegate<Func<int>>(instance);
                    return (Func<decimal>)(() => slice());
                }).ToArray();
                if (actions[0]() != actions[1]())
                    throw new InvalidOperationException($"Variant digests differ: {method}/{entries}");
                cases.Add(($"{method}/{entries}", actions, [new(), new()], new long[2]));
            }
        }

        decimal sink = 0;
        foreach (var item in cases)
            foreach (var action in item.Actions)
                Measure(action, .5, ref sink);
        for (int round = 0; round < 8; round++)
            foreach (var item in cases)
                for (int turn = 0; turn < 2; turn++)
                {
                    int variant = (round + turn) % 2;
                    var result = Measure(item.Actions[variant], .2, ref sink);
                    item.Samples[variant].Add(result.Us);
                    item.Bytes[variant] += result.Bytes;
                }
        foreach (var item in cases)
        {
            double Median(int variant)
            {
                var sorted = item.Samples[variant].Order().ToArray();
                return (sorted[3] + sorted[4]) / 2;
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                item.Name, BaselineUs = Median(0), CandidateUs = Median(1),
                Ratio = Median(1) / Median(0), item.Samples, item.Bytes,
            }));
        }
        Console.WriteLine($"Sink={sink}");
    }

    private static (double Us, long Bytes) Measure(Func<decimal> action, double seconds, ref decimal sink)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long count = 0, start = Stopwatch.GetTimestamp();
        do
        {
            for (int i = 0; i < 32; i++)
                sink += action();
            count += 32;
        } while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds);
        return (Stopwatch.GetElapsedTime(start).TotalSeconds * 1e6 / count,
            GC.GetAllocatedBytesForCurrentThread() - allocated);
    }

    private static void Metadata(Assembly assembly, int variant)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var groups = assembly.GetTypes().Where(type => type.Name.EndsWith("GroupReader", StringComparison.Ordinal))
            .Select(type => (Type: type, Fields: type.GetFields(flags)
                .Where(field => field.Name is "EntryTags" or "EntryTagBits").ToArray()))
            .Where(group => group.Fields.Length != 0).ToArray();
        RuntimeHelpers.RunClassConstructor(typeof(ResidualMembershipProbe).TypeHandle);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var group in groups)
            RuntimeHelpers.RunClassConstructor(group.Type.TypeHandle);
        long initializedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var arrays = groups.SelectMany(group => group.Fields).Select(field => (int[])field.GetValue(null)!)
            .Distinct(ReferenceEqualityComparer.Instance).Cast<int[]>().ToArray();
        long payload = arrays.Sum(array => array.LongLength * sizeof(int));
        long cctorIl = groups.Sum(group => (long)(group.Type.TypeInitializer?.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Variant = variant, Groups = groups.Length, BitmapGroups = groups.Count(group =>
                group.Fields.Any(field => field.Name == "EntryTagBits")),
            InitializedManagedBytes = initializedBytes, RetainedArrays = arrays.Length, ArrayPayloadBytes = payload,
            InitializerIlBytes = cctorIl, AssemblyBytes = new FileInfo(assembly.Location).Length,
        }));
        foreach (var group in groups.Where(group => group.Type.DeclaringType?.Name is "MDIncGrpReader" or "MDFullGrpReader"))
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                Variant = variant, Group = group.Type.FullName,
                PayloadBytes = group.Fields.Sum(field => ((int[])field.GetValue(null)!).LongLength * sizeof(int)),
            }));
    }
}
