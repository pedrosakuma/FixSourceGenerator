using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Attributes;
using FixSourceGenerator.Benchmarks.Generated.Fix.V44;

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
[SimpleJob(RuntimeMoniker.Net90)]
public class FixViewBenchmarks
{
    // Same wire shape as ReaderWriterBenchmarks (NewOrderSingle + Instrument + NoPartyIDs group),
    // built independently here so this benchmark class has no dependency on that one's statics.
    private static readonly byte[] Wire = BuildWireMessage();

    private static byte[] BuildWireMessage()
    {
        var dest = new byte[512];
        var w = new NewOrderSingleWriter(dest);
        w.WriteSenderCompID(Ascii("SENDER"));
        w.WriteTargetCompID(Ascii("TARGET"));
        w.WriteMsgSeqNum(7);
        w.WriteSendingTime(new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc));
        w.WriteClOrdID(Ascii("ORD-1"));
        w.WriteSymbol(Ascii("MSFT"));
        w.WriteSide(Side.Buy);
        w.WriteOrderQty(100m);
        w.WriteOrdType(OrdType.Limit);
        w.WritePrice(101.25m);
        w.WriteNoPartyIDs(2);
        w.WritePartyID(Ascii("PARTY-1"));
        w.WritePartyIDSource((char)'1');
        w.WritePartyRole(1);
        w.WritePartyID(Ascii("PARTY-2"));
        w.WritePartyIDSource((char)'1');
        w.WritePartyRole(3);
        int len = w.Finish();
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
    /// [FixView] exposing the group as a typed property (issue #17), alongside the same 2 scalar
    /// fields. Expected to be roughly on par with (not faster than) the full reader here: the
    /// group property isn't part of the early-exit scan — it's a lazy wrapper over the whole
    /// buffer either way, in both the view and the full reader (see FixViewEmitter.EmitGroupPropertyImpl).
    /// This benchmark exists to confirm that claim empirically, not to show a win.
    /// </summary>
    [Benchmark]
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
