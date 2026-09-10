using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args is ["--residual", var baselinePath, var candidatePath])
{
    ResidualMembershipProbe.Run(baselinePath, candidatePath);
    return;
}

if (args.Length != 2)
    throw new ArgumentException("Usage: baseline-benchmark.dll experimental-benchmark.dll");
var assemblies = args.Select(path => new VariantContext(path).LoadFromAssemblyPath(Path.GetFullPath(path))).ToArray();
var cases = new List<Case>();
foreach (string message in new[] { "X", "W" })
{
    var instances = assemblies.Select(assembly =>
    {
        var type = assembly.GetType("FixSourceGenerator.Benchmarks.MarketDataProcessBenchmarks", throwOnError: true)!;
        object instance = Activator.CreateInstance(type)!;
        type.GetProperty("Message")!.SetValue(instance, message);
        type.GetProperty("Entries")!.SetValue(instance, 50);
        type.GetMethod("Setup")!.Invoke(instance, null);
        return instance;
    }).ToArray();
    foreach (string method in new[] { "DecodeGenerated", "DecodeGeneratedNoTemporal",
        "ProjectGroupScoped", "ParseTemporalLocated" })
    {
        var actions = instances.Select(instance => instance.GetType().GetMethod(method)!
            .CreateDelegate<Func<decimal>>(instance)).ToArray();
        cases.Add(new Case(message, method, actions));
    }
}
decimal sink = 0;
foreach (var item in cases)
    foreach (var action in item.Actions)
        Measure(action, 1, ref sink);

for (int round = 0; round < 8; round++)
{
    foreach (var item in cases)
    {
        for (int turn = 0; turn < 2; turn++)
        {
            int variant = (round + turn) % 2;
            var result = Measure(item.Actions[variant], 0.25, ref sink);
            item.Samples[variant].Add(result.Microseconds);
            item.Allocations[variant] += result.Allocated;
        }
    }
}
foreach (var item in cases)
{
    var baseline = item.Samples[0].Order().ToArray();
    var experimental = item.Samples[1].Order().ToArray();
    double before = (baseline[3] + baseline[4]) / 2;
    double after = (experimental[3] + experimental[4]) / 2;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        item.Message, item.Method, BaselineMedianUs = before, ExperimentalMedianUs = after,
        Ratio = after / before, BaselineRangeUs = new[] { baseline[0], baseline[^1] },
        ExperimentalRangeUs = new[] { experimental[0], experimental[^1] },
        BaselineAllocated = item.Allocations[0], ExperimentalAllocated = item.Allocations[1],
        BaselineSamplesUs = item.Samples[0], ExperimentalSamplesUs = item.Samples[1],
    }));
}
Console.WriteLine($"Sink={sink}");

static (double Microseconds, long Allocated) Measure(Func<decimal> action, double seconds, ref decimal sink)
{
    long allocated = GC.GetAllocatedBytesForCurrentThread();
    long count = 0, start = Stopwatch.GetTimestamp();
    do
    {
        for (int i = 0; i < 32; i++) sink += action();
        count += 32;
    } while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds);
    double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
    return (elapsed * 1e6 / count, GC.GetAllocatedBytesForCurrentThread() - allocated);
}

sealed class Case(string message, string method, Func<decimal>[] actions)
{
    public string Message { get; } = message;
    public string Method { get; } = method;
    public Func<decimal>[] Actions { get; } = actions;
    public List<double>[] Samples { get; } = [new(), new()];
    public long[] Allocations { get; } = new long[2];
}

sealed class VariantContext(string path) : AssemblyLoadContext(Path.GetFullPath(path))
{
    private readonly AssemblyDependencyResolver _resolver = new(Path.GetFullPath(path));
    protected override Assembly? Load(AssemblyName name)
    {
        string? dependency = _resolver.ResolveAssemblyToPath(name);
        return dependency is null ? null : LoadFromAssemblyPath(dependency);
    }
}
