using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using DotnetDiagnostics.BenchmarkDotNet;
using FixSourceGenerator.Attributes;
using FixSourceGenerator.Benchmarks.Generated.Fix.V44;
using FixSourceGenerator.Benchmarks.Generated.Fix.V44.Runtime;

namespace FixSourceGenerator.Benchmarks;

/// <summary>
/// CPU/allocation comparison between the full <c>NewOrderSingleReader</c> (locates every declared
/// field in its single constructor scan, docs/CONTRACT.md §2) and a <c>[FixView]</c> selective
/// projection (issue #13) requesting only 2 of the message's ~7 fields — the scenario
/// <c>[FixView]</c>'s early-exit scanning constructor is meant to help with: the view's scan can
/// stop as soon as both requested tags are found, instead of scanning to the end of the buffer
/// like the full reader always does.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
[DotnetDiagnosticsDiagnoser]
public class FixViewBenchmarks
{
    // Same wire shape as ReaderWriterBenchmarks (NewOrderSingle + Instrument + NoPartyIDs group),
    // built independently here so this benchmark class has no dependency on that one's statics.
    private static readonly byte[] Wire = BuildWireMessage();

    private static byte[] BuildWireMessage()
    {
        var dest = new byte[512];
        Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
        NewOrderSingleWriter.InitializeState(state);
        var message = new NewOrderSingleWriter(dest, state, Ascii("SENDER"), Ascii("TARGET"), 7,
            new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc), Ascii("ORD-1"));
        var instrument = message.BeginInstrument(Ascii("MSFT"));
        var tail = instrument.SkipSecurityID().EndInstrument(Side.Buy, 100m, OrdType.Limit);
        var group = tail.WritePrice(101.25m).BeginNoPartyIDs(2);
        group = group.BeginEntry(Ascii("PARTY-1")).WritePartyIDSource('1').WritePartyRole(1).EndEntry();
        group = group.BeginEntry(Ascii("PARTY-2")).WritePartyIDSource('1').WritePartyRole(3).EndEntry();
        int len = group.EndGroup().Finish();
        var result = new byte[len];
        Array.Copy(dest, result, len);
        return result;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>Baseline: the full reader, reading only the same 2 fields the view below reads (isolates the constructor-scan cost difference, not "read more fields").</summary>
    [Benchmark(Baseline = true)]
    public decimal Decode_FullReader_TwoFields()
    {
        var reader = new NewOrderSingleReader(Wire);
        decimal total = reader.ClOrdID.Length;
        total += reader.Price ?? 0m;
        return total;
    }

    [Benchmark]
    public decimal Decode_FixView_TwoFields()
    {
        var view = new OrderRoutingView(Wire);
        decimal total = view.ClOrdID.Length;
        total += view.Price ?? 0m;
        return total;
    }

    /// <summary>Baseline: the full reader iterating the group, same access pattern as the view below.</summary>
    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.Cpu, DurationSeconds = 8)]
    public decimal Decode_FullReader_TwoFields_PlusGroup()
    {
        var reader = new NewOrderSingleReader(Wire);
        decimal total = reader.ClOrdID.Length;
        total += reader.Price ?? 0m;
        foreach (var party in reader.NoPartyIDs)
        {
            total += party.PartyRole ?? 0;
        }

        return total;
    }

    /// <summary>
    /// Includes the selective view's scoped group-location cost and subsequent enumeration.
    /// Unlike the full reader, the view bounds the group before constructing its group reader.
    /// </summary>
    [Benchmark]
    [DiagnosticKind(BenchmarkDiagnosticKind.Cpu, DurationSeconds = 8)]
    public decimal Decode_FixView_TwoFields_PlusGroup()
    {
        var view = new OrderRoutingWithPartiesView(Wire);
        decimal total = view.ClOrdID.Length;
        total += view.Price ?? 0m;
        foreach (var party in view.NoPartyIDs)
        {
            total += party.PartyRole ?? 0;
        }

        return total;
    }
}

[FixView("NewOrderSingle")]
public readonly ref partial struct OrderRoutingView
{
    public partial ReadOnlySpan<byte> ClOrdID { get; }
    public partial decimal? Price { get; }
}

[FixView("NewOrderSingle")]
public readonly ref partial struct OrderRoutingWithPartiesView
{
    public partial ReadOnlySpan<byte> ClOrdID { get; }
    public partial decimal? Price { get; }
    public partial FixSourceGenerator.Benchmarks.Generated.Fix.V44.NoPartyIDsGroupReader NoPartyIDs { get; }
}
