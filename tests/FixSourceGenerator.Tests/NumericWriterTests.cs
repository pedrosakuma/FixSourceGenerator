using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Tests;

public class NumericWriterTests
{
    private const string Driver = """
        using System;
        using System.Text;
        using Acme.Fix.V44;
        using Acme.Fix.V44.Runtime;

        public static class NumericDriver
        {
            public static string Scaled(long mantissa, int scale, int capacity)
            {
                var destination = new byte[capacity];
                var writer = new FixSpanWriter(destination);
                try { writer.WriteField(270, mantissa, scale); }
                catch (ArgumentException ex) when (ex.ParamName == "destination")
                {
                    CheckFailed(ref writer);
                    return "capacity";
                }
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Position));
            }

            public static string Integral(long value, int capacity)
            {
                var destination = new byte[capacity];
                var writer = new FixSpanWriter(destination);
                try { writer.WriteField(271, value); }
                catch (ArgumentException ex) when (ex.ParamName == "destination")
                {
                    CheckFailed(ref writer);
                    return "capacity";
                }
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Position));
            }

            public static string Decimal(decimal value, int tag)
            {
                var destination = new byte[128];
                var writer = new FixSpanWriter(destination);
                writer.WriteField(tag, value);
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Position));
            }

            public static string InvalidScale(int scale)
            {
                var writer = new FixSpanWriter(Span<byte>.Empty);
                try { writer.WriteField(270, long.MinValue, scale); }
                catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "scale")
                {
                    if (writer.Position != 0) throw new Exception("Invalid scale advanced writer.");
                    CheckFailed(ref writer);
                    return "scale";
                }
                throw new Exception("Unsupported scale was accepted.");
            }

            private static void CheckFailed(ref FixSpanWriter writer)
            {
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        if (i == 0) writer.WriteField(271, 1L);
                        else if (i == 1) writer.WriteField(270, 123L, 2);
                        else writer.Finish();
                    }
                    catch (InvalidOperationException) { continue; }
                    throw new Exception("Failed writer accepted a numeric operation.");
                }
            }

            public static string Generated(int integral, long quantity, decimal fractional, long mantissa, int scale)
            {
                var destination = new byte[512];
                var writer = new NewOrderSingleWriter(destination);
                writer.WritePrice(integral);
                writer.WriteOrderQty(quantity);
                writer.WriteNoAllocs(1);
                writer.WriteAllocQty(fractional);
                writer.WritePrice(mantissa, scale);
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Finish()));
            }
        }
        """;

    private static readonly Lazy<Type> DriverType = new(() => TestSupport.EmitAndLoad(
        TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _).Select(f => f.content).Append(Driver),
        "NumericWriterAssembly").GetType("NumericDriver")!);

    private static T Call<T>(string method, params object[] args) =>
        (T)DriverType.Value.GetMethod(method)!.Invoke(null, args)!;

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("ar-SA")]
    public void ScaledAndIntegral_MatchExactDecimalBytes_AtAllScales(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            long[] values = { long.MinValue, long.MaxValue, int.MinValue, int.MaxValue,
                0, 1, -1, 10, -10, 100, -100, 123456700, -123456700 };
            foreach (long value in values)
            {
                Assert.Equal(Call<string>("Decimal", (decimal)value, 271),
                    Call<string>("Integral", value, 128));
                for (byte scale = 0; scale <= 18; scale++)
                {
                    ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1 : (ulong)value;
                    var exact = new decimal(unchecked((int)magnitude), (int)(magnitude >> 32),
                        0, value < 0, scale);
                    string expected = Call<string>("Decimal", exact, 270);
                    Assert.Equal(expected, Call<string>("Scaled", value, (int)scale, 128));
                    for (int capacity = 0; capacity < expected.Length; capacity++)
                        Assert.Equal("capacity", Call<string>("Scaled", value, (int)scale, capacity));
                    Assert.Equal(expected, Call<string>("Scaled", value, (int)scale, expected.Length));
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0L, 0, "0")]
    [InlineData(0L, 18, "0.000000000000000000")]
    [InlineData(1234500L, 4, "123.4500")]
    [InlineData(-1234500L, 4, "-123.4500")]
    [InlineData(1L, 18, "0.000000000000000001")]
    [InlineData(-1L, 18, "-0.000000000000000001")]
    [InlineData(long.MinValue, 0, "-9223372036854775808")]
    [InlineData(long.MinValue, 18, "-9.223372036854775808")]
    [InlineData(long.MaxValue, 18, "9.223372036854775807")]
    public void ScaledWireFixtures_PreserveSignAndTrailingZeros(long mantissa, int scale, string value)
    {
        Assert.Equal("270=" + value + "\x01", Call<string>("Scaled", mantissa, scale, 128));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    public void Integral_RejectsEveryTruncation(long value)
    {
        string expected = "271=" + value.ToString(CultureInfo.InvariantCulture) + "\x01";
        for (int capacity = 0; capacity < expected.Length; capacity++)
            Assert.Equal("capacity", Call<string>("Integral", value, capacity));
        Assert.Equal(expected, Call<string>("Integral", value, expected.Length));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(19)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnsupportedScale_IsExplicitAndPoisonsWriter(int scale)
    {
        Assert.Equal("scale", Call<string>("InvalidScale", scale));
    }

    [Fact]
    public void ExistingIntLongAndDecimalCallSites_CompileAndKeepWireValues()
    {
        string frame = Call<string>("Generated", int.MinValue, long.MaxValue, 123.4500m, long.MinValue, 18);
        Assert.Contains("44=-2147483648\x01", frame);
        Assert.Contains("38=9223372036854775807\x01", frame);
        Assert.Contains("80=123.4500\x01", frame);
        Assert.Contains("44=-9.223372036854775808\x01", frame);
    }

    [Theory]
    [InlineData("FLOAT", true)]
    [InlineData("PRICE", true)]
    [InlineData("PRICEOFFSET", true)]
    [InlineData("QTY", true)]
    [InlineData("AMT", true)]
    [InlineData("PERCENTAGE", true)]
    [InlineData("INT", false)]
    [InlineData("NUMINGROUP", false)]
    [InlineData("STRING", false)]
    [InlineData("DATA", false)]
    public void OnlyDecimalSchemaTypes_GainIntegralAndScaledSetters(string fixType, bool numeric)
    {
        var field = new FixFieldDef(5001, "VendorValue", fixType, new List<FixValueDef>());
        var fields = new Dictionary<string, FixFieldDef> { [field.Name] = field };
        var message = new FixMessageDef("Numbers", "U", "app",
            new List<FixEntry> { new FixFieldRef(field, required: true) });
        var dictionary = TestSupport.BuildDiffDictionary(new[] { message },
            new Dictionary<string, FixComponentDef>(), fields);
        string source = TestSupport.Generate(dictionary, out _).Single(f => f.hintName.EndsWith("Numbers.g.cs")).content;
        Assert.Equal(numeric, source.Contains("WriteVendorValue(long value)"));
        Assert.Equal(numeric, source.Contains("WriteVendorValue(long mantissa, int scale)"));
        Assert.Contains("""_writer.WriteField("5001="u8, value)""", source);
        if (numeric)
        {
            Assert.Contains("WriteVendorValue(decimal value)", source);
            Assert.Contains("public decimal VendorValue", source);
        }
    }
}
