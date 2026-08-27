using System;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V44;
using QuickFix.FIX44;

namespace FixSourceGenerator.Benchmarks;

/// <summary>
/// CPU/allocation hot-path benchmarks for the generated reader/writer (docs/CONTRACT.md's
/// allocation-minimal premise), using a NewOrderSingle with a component (Instrument), an
/// enumerated CHAR (Side) and a repeating group (NoPartyIDs) — the same shape exercised by
/// tests/FixSourceGenerator.Tests/QuickFixNConformanceTests.cs. QuickFIX/n is included as a
/// baseline reference implementation (mature, widely deployed, generic/reflection-based engine),
/// never as a target the generator itself depends on.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class ReaderWriterBenchmarks
{
    // 8=FIX.4.4|9=...|35=D|49=SENDER|56=TARGET|34=7|52=20240115-10:30:05.000|
    // 11=ORD-1|55=MSFT|54=1|38=100|40=2|44=101.25|
    // 453=2|448=PARTY-1|447=1|452=1|448=PARTY-2|447=1|452=3|10=...
    private static readonly byte[] Wire = BuildWireMessage();

    // Pre-encoded once: WriteXxx(ReadOnlySpan<byte>) takes bytes, not string — encoding a string
    // to bytes is the caller's concern, not something to measure inside the writer's own hot path.
    private static readonly byte[] SenderCompId = Ascii("SENDER");
    private static readonly byte[] TargetCompId = Ascii("TARGET");
    private static readonly byte[] ClOrdIdBytes = Ascii("ORD-1");
    private static readonly byte[] SymbolBytes = Ascii("MSFT");
    private static readonly byte[] PartyId1 = Ascii("PARTY-1");
    private static readonly byte[] PartyId2 = Ascii("PARTY-2");
    private static readonly byte[] EncodeDestination = new byte[512];

    private static readonly QuickFix.DataDictionary.DataDictionary Dictionary =
        new QuickFix.DataDictionary.DataDictionary(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Schema", "FIX44-quickfixn-dictionary.xml"));

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

    // ---- Decode ----

    /// <summary>Decodes every scalar/component/group field, mirroring a typical order-book handler.</summary>
    [Benchmark(Baseline = true)]
    public decimal Decode_Generated()
    {
        var reader = new NewOrderSingleReader(Wire);
        decimal total = 0m;
        _ = reader.ClOrdID.Length;
        _ = reader.Instrument.Symbol.Length;
        total += reader.Side == Side.Buy ? 1 : 0;
        total += reader.OrderQty;
        total += reader.Price ?? 0m;

        foreach (var party in reader.NoPartyIDs)
        {
            _ = party.PartyID.Length;
            total += party.PartyRole ?? 0;
        }

        return total;
    }

    [Benchmark]
    public decimal Decode_QuickFixN()
    {
        var msg = new NewOrderSingle();
        msg.FromString(Encoding.ASCII.GetString(Wire), false, Dictionary, Dictionary, new MessageFactory());

        decimal total = 0m;
        _ = msg.GetString(QuickFix.Fields.Tags.ClOrdID);
        _ = msg.GetString(QuickFix.Fields.Tags.Symbol);
        total += msg.GetChar(QuickFix.Fields.Tags.Side) == QuickFix.Fields.Side.BUY ? 1 : 0;
        total += msg.GetDecimal(QuickFix.Fields.Tags.OrderQty);
        if (msg.IsSetField(QuickFix.Fields.Tags.Price))
        {
            total += msg.GetDecimal(QuickFix.Fields.Tags.Price);
        }

        int count = msg.IsSetField(QuickFix.Fields.Tags.NoPartyIDs)
            ? msg.GetInt(QuickFix.Fields.Tags.NoPartyIDs)
            : 0;
        for (int i = 1; i <= count; i++)
        {
            var group = new NewOrderSingle.NoPartyIDsGroup();
            msg.GetGroup(i, group);
            _ = group.GetString(QuickFix.Fields.Tags.PartyID);
            if (group.IsSetField(QuickFix.Fields.Tags.PartyRole))
            {
                total += group.GetInt(QuickFix.Fields.Tags.PartyRole);
            }
        }

        return total;
    }

    // ---- Encode ----

    [Benchmark]
    public int Encode_Generated()
    {
        var w = new NewOrderSingleWriter(EncodeDestination);
        w.WriteSenderCompID(SenderCompId);
        w.WriteTargetCompID(TargetCompId);
        w.WriteMsgSeqNum(7);
        w.WriteSendingTime(new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc));
        w.WriteClOrdID(ClOrdIdBytes);
        w.WriteSymbol(SymbolBytes);
        w.WriteSide(Side.Buy);
        w.WriteOrderQty(100m);
        w.WriteOrdType(OrdType.Limit);
        w.WritePrice(101.25m);
        w.WriteNoPartyIDs(2);
        w.WritePartyID(PartyId1);
        w.WritePartyIDSource('1');
        w.WritePartyRole(1);
        w.WritePartyID(PartyId2);
        w.WritePartyIDSource('1');
        w.WritePartyRole(3);
        return w.Finish();
    }

    [Benchmark]
    public string Encode_QuickFixN()
    {
        var order = new NewOrderSingle(
            new QuickFix.Fields.ClOrdID("ORD-1"),
            new QuickFix.Fields.Symbol("MSFT"),
            new QuickFix.Fields.Side(QuickFix.Fields.Side.BUY),
            new QuickFix.Fields.TransactTime(DateTime.UtcNow),
            new QuickFix.Fields.OrdType(QuickFix.Fields.OrdType.LIMIT));
        order.Set(new QuickFix.Fields.OrderQty(100m));
        order.Set(new QuickFix.Fields.Price(101.25m));
        order.Header.SetField(new QuickFix.Fields.SenderCompID("SENDER"));
        order.Header.SetField(new QuickFix.Fields.TargetCompID("TARGET"));
        order.Header.SetField(new QuickFix.Fields.MsgSeqNum(7));
        order.Header.SetField(new QuickFix.Fields.SendingTime(new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc)));

        var group1 = new NewOrderSingle.NoPartyIDsGroup();
        group1.Set(new QuickFix.Fields.PartyID("PARTY-1"));
        group1.Set(new QuickFix.Fields.PartyIDSource('1'));
        group1.Set(new QuickFix.Fields.PartyRole(1));
        order.AddGroup(group1);

        var group2 = new NewOrderSingle.NoPartyIDsGroup();
        group2.Set(new QuickFix.Fields.PartyID("PARTY-2"));
        group2.Set(new QuickFix.Fields.PartyIDSource('1'));
        group2.Set(new QuickFix.Fields.PartyRole(3));
        order.AddGroup(group2);

        return order.ConstructString();
    }
}
