using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FixSourceGenerator.Tests;

/// <summary>
/// End-to-end tests: generate → compile → load → exercise the generated reader/writer via a small
/// compiled driver (ref structs cannot be invoked directly through reflection, so a plain driver
/// class bridges to boxable return types).
/// </summary>
public class RoundTripTests
{
    private const string Driver = @"
using System;
using System.Collections.Generic;
using System.Text;
using Acme.Fix.V44;

public static class FixTestDriver
{
    private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
    private static string S(ReadOnlySpan<byte> v) => Encoding.ASCII.GetString(v);

    public static string ReadClOrdID(byte[] b) => S(new NewOrderSingleReader(b).ClOrdID);
    public static bool SideIsBuy(byte[] b) => new NewOrderSingleReader(b).Side == Side.Buy;
    public static bool SideStrictOk(byte[] b) => new NewOrderSingleReader(b).TryGetSideStrict(out _);
    public static int ReadSideRaw(byte[] b) => (int)new NewOrderSingleReader(b).Side;
    public static decimal ReadOrderQty(byte[] b) => new NewOrderSingleReader(b).OrderQty;
    public static bool PriceHasValue(byte[] b) => new NewOrderSingleReader(b).Price.HasValue;
    public static decimal ReadPrice(byte[] b) => new NewOrderSingleReader(b).Price!.Value;
    public static string ReadSymbol(byte[] b) => S(new NewOrderSingleReader(b).Instrument.Symbol);
    public static string ReadSecurityID(byte[] b)
    {
        var r = new NewOrderSingleReader(b);
        return r.Instrument.TryGetSecurityID(out var v) ? S(v) : null;
    }
    public static long ReadTransactTimeTicks(byte[] b) => new NewOrderSingleReader(b).TransactTime!.Value.Ticks;

    public static string[] ReadExecInstValues(byte[] b)
    {
        var list = new List<string>();
        foreach (var token in new NewOrderSingleReader(b).ExecInstValues)
            list.Add(S(token));
        return list.ToArray();
    }

    public static bool ExecInstHasValue(byte[] b) => new NewOrderSingleReader(b).TryGetExecInst(out _);

    public static int NoAllocsCount(byte[] b) => new NewOrderSingleReader(b).NoAllocs.Count;

    public static string[] ReadAllocAccounts(byte[] b)
    {
        var list = new List<string>();
        foreach (var e in new NewOrderSingleReader(b).NoAllocs)
            list.Add(S(e.AllocAccount));
        return list.ToArray();
    }

    public static decimal ReadAllocQty(byte[] b, int idx)
    {
        int i = 0;
        foreach (var e in new NewOrderSingleReader(b).NoAllocs)
        {
            if (i == idx) return e.AllocQty;
            i++;
        }
        return -1m;
    }

    public static int NestedCount(byte[] b, int idx)
    {
        int i = 0;
        foreach (var e in new NewOrderSingleReader(b).NoAllocs)
        {
            if (i == idx) return e.NoNested.Count;
            i++;
        }
        return -1;
    }

    public static string[] NestedPartyIds(byte[] b, int idx)
    {
        int i = 0;
        foreach (var e in new NewOrderSingleReader(b).NoAllocs)
        {
            if (i == idx)
            {
                var list = new List<string>();
                foreach (var n in e.NoNested)
                    list.Add(S(n.NestedPartyID));
                return list.ToArray();
            }
            i++;
        }
        return Array.Empty<string>();
    }

    public static byte[] EncodeMinimal()
    {
        var dest = new byte[256];
        var w = new NewOrderSingleWriter(dest);
        w.WriteClOrdID(A(""ABC""));
        w.WriteSide(Side.Buy);
        w.WriteOrderQty(100m);
        int len = w.Finish();
        var result = new byte[len];
        Array.Copy(dest, result, len);
        return result;
    }

    public static byte[] EncodeWithGroup()
    {
        var dest = new byte[512];
        var w = new NewOrderSingleWriter(dest);
        w.WriteClOrdID(A(""ORD1""));
        w.WriteSymbol(A(""MSFT""));
        w.WriteSide(Side.Sell);
        w.WriteOrderQty(50m);
        w.WriteNoAllocs(2);
        w.WriteAllocAccount(A(""ACC1""));
        w.WriteAllocQty(10m);
        w.WriteAllocAccount(A(""ACC2""));
        w.WriteAllocQty(20m);
        int len = w.Finish();
        var result = new byte[len];
        Array.Copy(dest, result, len);
        return result;
    }
}
";

    private static (Assembly asm, System.Type driver) Build()
    {
        var files = TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _);
        var sources = files.Select(f => f.content).Append(Driver);
        var asm = TestSupport.EmitAndLoad(sources);
        return (asm, asm.GetType("FixTestDriver")!);
    }

    private static T Call<T>(System.Type driver, string method, params object[] args)
        => (T)driver.GetMethod(method)!.Invoke(null, args)!;

    [Fact]
    public void Reader_DecodesFields_Components_AndNestedGroups()
    {
        var (_, driver) = Build();

        var buffer = TestSupport.Fix(
            "8=FIX.4.4", "9=000", "35=D",
            "11=ORDER123", "55=AAPL", "48=SEC1", "54=1", "38=100", "44=12.34",
            "60=20240115-10:30:00.000",
            "78=2",
            "79=ACC1", "80=60", "756=1", "757=P1",
            "79=ACC2", "80=40", "756=2", "757=P2", "757=P3",
            "10=000");

        Assert.Equal("ORDER123", Call<string>(driver, "ReadClOrdID", buffer));
        Assert.True(Call<bool>(driver, "SideIsBuy", buffer));
        Assert.Equal(100m, Call<decimal>(driver, "ReadOrderQty", buffer));
        Assert.True(Call<bool>(driver, "PriceHasValue", buffer));
        Assert.Equal(12.34m, Call<decimal>(driver, "ReadPrice", buffer));
        Assert.Equal("AAPL", Call<string>(driver, "ReadSymbol", buffer));
        Assert.Equal("SEC1", Call<string>(driver, "ReadSecurityID", buffer));

        var expectedTicks = new System.DateTime(2024, 1, 15, 10, 30, 0, System.DateTimeKind.Utc).Ticks;
        Assert.Equal(expectedTicks, Call<long>(driver, "ReadTransactTimeTicks", buffer));

        // Repeating group: exactly two entries.
        Assert.Equal(2, Call<int>(driver, "NoAllocsCount", buffer));
        Assert.Equal(new[] { "ACC1", "ACC2" }, Call<string[]>(driver, "ReadAllocAccounts", buffer));
        Assert.Equal(60m, Call<decimal>(driver, "ReadAllocQty", buffer, 0));
        Assert.Equal(40m, Call<decimal>(driver, "ReadAllocQty", buffer, 1));

        // Nested group-in-group.
        Assert.Equal(1, Call<int>(driver, "NestedCount", buffer, 0));
        Assert.Equal(2, Call<int>(driver, "NestedCount", buffer, 1));
        Assert.Equal(new[] { "P1" }, Call<string[]>(driver, "NestedPartyIds", buffer, 0));
        Assert.Equal(new[] { "P2", "P3" }, Call<string[]>(driver, "NestedPartyIds", buffer, 1));
    }

    [Fact]
    public void TryGetFieldStrict_AcceptsKnownEnumValue_RejectsUnknownOne()
    {
        // Issue: enum domain validation (docs/CONTRACT.md §10) — TryGet{Field}Strict lets callers
        // opt into rejecting a wire value that isn't one of the schema's declared <value>s, unlike
        // the plain property (which always casts, silently producing an "unnamed" enum member).
        var (_, driver) = Build();

        var knownSide = TestSupport.Fix(
            "8=FIX.4.4", "9=000", "35=D",
            "11=ORDER1", "54=1", "38=1", "10=000");
        Assert.True(Call<bool>(driver, "SideStrictOk", knownSide));

        var unknownSide = TestSupport.Fix(
            "8=FIX.4.4", "9=000", "35=D",
            "11=ORDER2", "54=9", "38=1", "10=000");
        Assert.False(Call<bool>(driver, "SideStrictOk", unknownSide));
        // The plain (non-strict) property still decodes the out-of-domain value rather than throwing.
        Assert.Equal((int)'9', Call<int>(driver, "ReadSideRaw", unknownSide));
    }

    [Fact]
    public void MultiValueField_EnumeratesSpaceDelimitedTokens_WithoutAllocatingAnArray()
    {
        // Issue: typed MULTIPLEVALUESTRING/MULTIPLECHARVALUE parsing (docs/CONTRACT.md §10) —
        // {Field}Values exposes a forward-only enumerator over each space-delimited token as a
        // sub-span, instead of forcing callers to split the raw span themselves.
        var (_, driver) = Build();

        var buffer = TestSupport.Fix(
            "8=FIX.4.4", "9=000", "35=D",
            "11=ORDER1", "54=1", "38=1", "18=2 6 G", "10=000");

        Assert.True(Call<bool>(driver, "ExecInstHasValue", buffer));
        Assert.Equal(new[] { "2", "6", "G" }, Call<string[]>(driver, "ReadExecInstValues", buffer));
    }

    [Fact]
    public void MultiValueField_AbsentField_EnumeratesNoTokens()
    {
        var (_, driver) = Build();

        var buffer = TestSupport.Fix(
            "8=FIX.4.4", "9=000", "35=D",
            "11=ORDER1", "54=1", "38=1", "10=000");

        Assert.False(Call<bool>(driver, "ExecInstHasValue", buffer));
        Assert.Empty(Call<string[]>(driver, "ReadExecInstValues", buffer));
    }

    [Fact]
    public void Writer_ProducesExactBodyLengthAndCheckSum()
    {
        var (_, driver) = Build();
        var actual = Call<byte[]>(driver, "EncodeMinimal");

        // Independently hand-construct the expected wire message.
        string body = "35=D\u000111=ABC\u000154=1\u000138=100\u0001";
        int bodyLength = body.Length; // all ASCII, 1 byte each
        string head = "8=FIX.4.4\u00019=" + bodyLength + "\u0001";
        string preChecksum = head + body;

        int sum = 0;
        foreach (char c in preChecksum)
        {
            sum += (byte)c;
        }

        sum &= 0xFF;
        string expected = preChecksum + "10=" + sum.ToString("D3") + "\u0001";
        var expectedBytes = expected.Select(c => (byte)c).ToArray();

        Assert.Equal(expectedBytes, actual);

        // Sanity: BodyLength value and CheckSum value are the specific expected numbers.
        Assert.Equal(24, bodyLength);
    }

    [Fact]
    public void Writer_RoundTripsThroughReader()
    {
        var (_, driver) = Build();
        var encoded = Call<byte[]>(driver, "EncodeWithGroup");

        Assert.Equal("ORD1", Call<string>(driver, "ReadClOrdID", encoded));
        Assert.Equal("MSFT", Call<string>(driver, "ReadSymbol", encoded));
        Assert.False(Call<bool>(driver, "SideIsBuy", encoded));
        Assert.Equal(50m, Call<decimal>(driver, "ReadOrderQty", encoded));
        Assert.Equal(2, Call<int>(driver, "NoAllocsCount", encoded));
        Assert.Equal(new[] { "ACC1", "ACC2" }, Call<string[]>(driver, "ReadAllocAccounts", encoded));
        Assert.Equal(10m, Call<decimal>(driver, "ReadAllocQty", encoded, 0));
        Assert.Equal(20m, Call<decimal>(driver, "ReadAllocQty", encoded, 1));
    }
}
