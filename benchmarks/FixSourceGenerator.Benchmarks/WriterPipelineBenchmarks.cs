using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class WriterPipelineBenchmarks
{
    private readonly MarketDataWriterBenchmarks _fixture = new();
    private bool _snapshot;

    [Params(1, 50)]
    public int Entries { get; set; }

    [Params("X", "W")]
    public string Message { get; set; } = "X";

    [GlobalSetup]
    public void Setup()
    {
        _snapshot = Message switch
        {
            "X" => false,
            "W" => true,
            _ => throw new ArgumentException("Expected X or W.", nameof(Message)),
        };
        _fixture.Entries = Entries;
        _fixture.CheckPipelineFrames(_snapshot);
    }

    [Benchmark(Baseline = true)]
    public int Raw() => _fixture.WriteRaw(_snapshot);

    [Benchmark]
    public int StateAndCounts() => _fixture.WriteWithContext(_snapshot);

    [Benchmark]
    public int Scoped() => _snapshot ? _fixture.W_ScaledAndIntegral() : _fixture.X_ScaledAndIntegral();

    [Benchmark]
    public int InPlace() => _fixture.WriteInPlace(_snapshot);
}
