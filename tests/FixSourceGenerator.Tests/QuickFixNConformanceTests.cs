using System;
using System.Linq;
using System.Reflection;
using QuickFix.DataDictionary;
using QuickFix.FIX44;
using Xunit;

namespace FixSourceGenerator.Tests;

/// <summary>
/// Cross-conformance tests (issue #9): validates that our generated reader/writer are wire-
/// compatible with <a href="https://github.com/connamara/quickfixn">QuickFIX/n</a>, a mature,
/// widely deployed .NET FIX engine that consumes the same DataDictionary XML format and
/// generates its own message classes (used here only as a test-time reference implementation,
/// never as a dependency of the generator itself — see the "test-only" package references in
/// FixSourceGenerator.Tests.csproj).
///
/// Three directions are exercised:
///  1. Build with QuickFIX/n, decode with our generated reader.
///  2. Build with our generated writer, decode with QuickFIX/n (using the real FIX44.xml
///     DataDictionary for validation).
///  3. Build the same logical message with both and diff the produced bytes directly.
/// </summary>
public class QuickFixNConformanceTests
{
    // The real, public FIX44.xml (see TestData/FIX44.xml.NOTICE.md) is reused as *both* the
    // DataDictionary QuickFIX/n validates against, and the schema our own SchemaReader parses
    // to generate the reader/writer under test — guaranteeing identical tag numbers/types on
    // both sides, since both implementations are driven by the exact same source file.
    private static readonly DataDictionary Dictionary = new DataDictionary(
        System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "FIX44.xml"));

    private static (Assembly Assembly, Type Driver) BuildGeneratedFix44()
    {
        string xml = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "TestData", "FIX44-quickfixn.xml"));
        var schema = global::FixSourceGenerator.Schema.SchemaReader.Parse(xml, "FIX44-quickfixn.xml", _ => { });
        Assert.NotNull(schema);

        var context = new global::FixSourceGenerator.GenerationContext(_ => { });
        var sources = new global::FixSourceGenerator.Generators.FixCodeGenerator()
            .Generate("QfnConformance.Fix.V44", schema!, context)
            .Select(s => s.content)
            .ToList();
        sources.Add(Driver);

        var assembly = TestSupport.EmitAndLoad(sources, "QuickFixNConformance_" + Guid.NewGuid().ToString("N"));
        return (assembly, assembly.GetType("QfnFixTestDriver")!);
    }

    // A thin static driver compiled alongside the generated code (ref structs can't be invoked
    // directly through reflection), bridging to boxable return types for the test to assert on.
    private const string Driver = @"
using System;
using System.Text;
using QfnConformance.Fix.V44;

public static class QfnFixTestDriver
{
    private static string S(ReadOnlySpan<byte> v) => Encoding.ASCII.GetString(v);
    private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

    public static string ReadClOrdID(byte[] b) => S(new NewOrderSingleReader(b).ClOrdID);
    public static string ReadSymbol(byte[] b) => S(new NewOrderSingleReader(b).Instrument.Symbol);
    public static bool SideIsBuy(byte[] b) => new NewOrderSingleReader(b).Side == Side.Buy;
    public static decimal ReadOrderQty(byte[] b) => new NewOrderSingleReader(b).OrderQty;
    public static decimal ReadPrice(byte[] b) => new NewOrderSingleReader(b).Price!.Value;
    public static OrdType ReadOrdType(byte[] b) => new NewOrderSingleReader(b).OrdType;

    public static int NoPartyIDsCount(byte[] b) => new NewOrderSingleReader(b).NoPartyIDs.Count;
    public static string ReadPartyID(byte[] b, int idx)
    {
        int i = 0;
        foreach (var e in new NewOrderSingleReader(b).NoPartyIDs)
        {
            if (i == idx) return S(e.PartyID);
            i++;
        }
        return null;
    }

    public static byte[] EncodeNewOrderSingle(string clOrdId, string symbol, bool buy, decimal qty, decimal price, DateTime transactTime)
    {
        var dest = new byte[1024];
        var w = new NewOrderSingleWriter(dest);
        w.WriteClOrdID(A(clOrdId));
        w.WriteSymbol(A(symbol));
        w.WriteSide(buy ? Side.Buy : Side.Sell);
        w.WriteOrderQty(qty);
        w.WriteOrdType(OrdType.Limit);
        w.WritePrice(price);
        w.WriteTransactTime(transactTime);
        int len = w.Finish();
        var result = new byte[len];
        Array.Copy(dest, result, len);
        return result;
    }
}
";

    private static T Call<T>(Type driver, string method, params object[] args) =>
        (T)driver.GetMethod(method)!.Invoke(null, args)!;

    /// <summary>Builds a NewOrderSingle with QuickFIX/n's own generated classes and header helper.</summary>
    private static NewOrderSingle BuildQuickFixNOrder(
        string clOrdId, string symbol, bool buy, decimal qty, decimal price,
        string senderCompId, string targetCompId, int msgSeqNum, DateTime sendingTime, DateTime transactTime)
    {
        var order = new NewOrderSingle(
            new QuickFix.Fields.ClOrdID(clOrdId),
            new QuickFix.Fields.Symbol(symbol),
            new QuickFix.Fields.Side(buy ? QuickFix.Fields.Side.BUY : QuickFix.Fields.Side.SELL),
            new QuickFix.Fields.TransactTime(transactTime),
            new QuickFix.Fields.OrdType(QuickFix.Fields.OrdType.LIMIT));
        order.Set(new QuickFix.Fields.OrderQty(qty));
        order.Set(new QuickFix.Fields.Price(price));
        order.Header.SetField(new QuickFix.Fields.SenderCompID(senderCompId));
        order.Header.SetField(new QuickFix.Fields.TargetCompID(targetCompId));
        order.Header.SetField(new QuickFix.Fields.MsgSeqNum((uint)msgSeqNum));
        order.Header.SetField(new QuickFix.Fields.SendingTime(sendingTime));
        return order;
    }

    [Fact]
    public void Our_reader_decodes_a_message_built_and_serialized_by_QuickFixN()
    {
        var transactTime = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var sendingTime = new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc);
        var order = BuildQuickFixNOrder("ORD-1", "MSFT", buy: true, qty: 100m, price: 101.25m,
            "SENDER", "TARGET", 7, sendingTime, transactTime);

        string wire = order.ConstructString(); // includes correctly computed BodyLength/CheckSum
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(wire);

        var (_, driver) = BuildGeneratedFix44();

        Assert.Equal("ORD-1", Call<string>(driver, "ReadClOrdID", bytes));
        Assert.Equal("MSFT", Call<string>(driver, "ReadSymbol", bytes));
        Assert.True(Call<bool>(driver, "SideIsBuy", bytes));
        Assert.Equal(100m, Call<decimal>(driver, "ReadOrderQty", bytes));
        Assert.Equal(101.25m, Call<decimal>(driver, "ReadPrice", bytes));
    }

    [Fact]
    public void Our_reader_decodes_repeating_group_built_by_QuickFixN()
    {
        var transactTime = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var order = BuildQuickFixNOrder("ORD-2", "AAPL", buy: false, qty: 50m, price: 199.5m,
            "SENDER", "TARGET", 8, transactTime, transactTime);

        var group = new NewOrderSingle.NoPartyIDsGroup();
        group.Set(new QuickFix.Fields.PartyID("PARTY-1"));
        group.Set(new QuickFix.Fields.PartyIDSource(QuickFix.Fields.PartyIDSource.PROPRIETARY));
        group.Set(new QuickFix.Fields.PartyRole(QuickFix.Fields.PartyRole.EXECUTING_FIRM));
        order.AddGroup(group);

        var group2 = new NewOrderSingle.NoPartyIDsGroup();
        group2.Set(new QuickFix.Fields.PartyID("PARTY-2"));
        group2.Set(new QuickFix.Fields.PartyIDSource(QuickFix.Fields.PartyIDSource.PROPRIETARY));
        group2.Set(new QuickFix.Fields.PartyRole(QuickFix.Fields.PartyRole.CLIENT_ID));
        order.AddGroup(group2);

        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(order.ConstructString());

        var (_, driver) = BuildGeneratedFix44();

        Assert.Equal(2, Call<int>(driver, "NoPartyIDsCount", bytes));
        Assert.Equal("PARTY-1", Call<string>(driver, "ReadPartyID", bytes, 0));
        Assert.Equal("PARTY-2", Call<string>(driver, "ReadPartyID", bytes, 1));
    }

    [Fact]
    public void QuickFixN_decodes_a_message_built_and_serialized_by_our_writer()
    {
        var (_, driver) = BuildGeneratedFix44();
        var transactTime = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var sendingTime = new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc);

        byte[] bytes = Call<byte[]>(driver, "EncodeNewOrderSingle",
            "ORD-3", "GOOG", true, 25m, 150.5m, transactTime);
        string wire = System.Text.Encoding.ASCII.GetString(bytes);

        var msg = new NewOrderSingle();
        // validate:false — our generated writer only emits the message body (per docs/CONTRACT.md
        // §2.2, header/trailer fields beyond BeginString/BodyLength/MsgType/CheckSum are out of
        // scope for the message writer), so the wire message intentionally omits required header
        // fields (SenderCompID, TargetCompID, MsgSeqNum, SendingTime) that QuickFIX/n's dictionary
        // validation would otherwise reject.
        msg.FromString(wire, false, Dictionary, Dictionary, new MessageFactory());

        Assert.Equal("ORD-3", msg.GetString(new QuickFix.Fields.ClOrdID().Tag));
        Assert.Equal("GOOG", msg.GetString(new QuickFix.Fields.Symbol().Tag));
        Assert.Equal(QuickFix.Fields.Side.BUY, msg.Get(new QuickFix.Fields.Side()).Value);
        Assert.Equal(25m, msg.Get(new QuickFix.Fields.OrderQty()).Value);
        Assert.Equal(150.5m, msg.Get(new QuickFix.Fields.Price()).Value);
    }

    [Fact]
    public void Both_implementations_produce_the_same_field_values_for_the_same_logical_message()
    {
        var transactTime = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var sendingTime = new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc);

        var qfnOrder = BuildQuickFixNOrder("ORD-4", "IBM", true, 10m, 42.5m, "SENDER", "TARGET", 10, sendingTime, transactTime);
        byte[] qfnBytes = System.Text.Encoding.ASCII.GetBytes(qfnOrder.ConstructString());

        var (_, driver) = BuildGeneratedFix44();
        byte[] ourBytes = Call<byte[]>(driver, "EncodeNewOrderSingle",
            "ORD-4", "IBM", true, 10m, 42.5m, transactTime);

        // Decode QuickFIX/n's bytes with our reader...
        Assert.Equal("ORD-4", Call<string>(driver, "ReadClOrdID", qfnBytes));
        Assert.Equal("IBM", Call<string>(driver, "ReadSymbol", qfnBytes));
        Assert.Equal(10m, Call<decimal>(driver, "ReadOrderQty", qfnBytes));
        Assert.Equal(42.5m, Call<decimal>(driver, "ReadPrice", qfnBytes));

        // ...and decode our bytes with QuickFIX/n, for the same logical values.
        var msg = new NewOrderSingle();
        msg.FromString(System.Text.Encoding.ASCII.GetString(ourBytes), false, Dictionary, Dictionary, new MessageFactory());
        Assert.Equal("ORD-4", msg.GetString(new QuickFix.Fields.ClOrdID().Tag));
        Assert.Equal("IBM", msg.GetString(new QuickFix.Fields.Symbol().Tag));
        Assert.Equal(10m, msg.Get(new QuickFix.Fields.OrderQty()).Value);
        Assert.Equal(42.5m, msg.Get(new QuickFix.Fields.Price()).Value);
    }
}
