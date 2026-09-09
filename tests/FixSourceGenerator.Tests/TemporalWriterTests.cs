using System;
using System.Globalization;
using System.Linq;

namespace FixSourceGenerator.Tests;

public class TemporalWriterTests
{
    private const string Driver = """
        using System;
        using System.Text;
        using Acme.Fix.V44;
        using Acme.Fix.V44.Runtime;
        public static class TemporalDriver
        {
            public static string Write(long ticks, int kind, int type, int capacity)
            {
                var value = new DateTime(ticks, (DateTimeKind)kind);
                var destination = new byte[capacity];
                var writer = new FixSpanWriter(destination);
                try
                {
                    if (type == 0) writer.WriteField(60, value);
                    else if (type == 1) writer.WriteField(60, DateOnly.FromDateTime(value));
                    else writer.WriteField(60, TimeOnly.FromDateTime(value));
                }
                catch (ArgumentException ex) when (ex.ParamName == "destination")
                {
                    try { writer.Finish(); }
                    catch (InvalidOperationException) { return "capacity"; }
                    throw new Exception("Failed temporal writer finalized.");
                }
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Position));
            }

            public static string Generated(DateTime value)
            {
                var destination = new byte[128];
                var writer = new NewOrderSingleWriter(destination);
                writer.WriteTransactTime(value);
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Finish()));
            }
        }
        """;

    private static readonly Lazy<Type> DriverType = new(() => TestSupport.EmitAndLoad(
        TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _).Select(f => f.content).Append(Driver),
        "TemporalWriterAssembly").GetType("TemporalDriver")!);

    private static string Call(string method, params object[] args) =>
        (string)DriverType.Value.GetMethod(method)!.Invoke(null, args)!;

    [Theory]
    [InlineData("en-US")]
    [InlineData("pt-BR")]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public void DirectAscii_MatchesLegacyInvariantFormatting_AcrossBoundariesAndKinds(string culture)
    {
        DateTime[] dates =
        {
            DateTime.MinValue, DateTime.MaxValue,
            new(4, 2, 29), new(100, 3, 1), new(400, 2, 29),
            new(1900, 2, 28, 23, 59, 59, 999),
            new(1900, 3, 1), new(2000, 2, 29),
            new DateTime(2024, 2, 29).AddTicks(-1),
            new(2024, 2, 29),
            new DateTime(2024, 3, 1).AddTicks(-1),
            new(2024, 3, 1),
            new DateTime(2025, 1, 1).AddTicks(-1),
            new(2025, 1, 1),
            new DateTime(2024, 1, 2, 3, 4, 5, 6).AddTicks(9876),
            new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(9999),
            new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(10000),
        };
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (var date in dates)
            foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Local, DateTimeKind.Unspecified })
            {
                var value = DateTime.SpecifyKind(date, kind);
                string[] legacy =
                {
                    value.ToString("yyyyMMdd-HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    DateOnly.FromDateTime(value).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                    TimeOnly.FromDateTime(value).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                };
                for (int type = 0; type < legacy.Length; type++)
                {
                    string expected = "60=" + legacy[type] + "\x01";
                    Assert.Equal(expected, Call("Write", value.Ticks, (int)kind, type, expected.Length));
                    for (int capacity = 0; capacity < expected.Length; capacity++)
                        Assert.Equal("capacity", Call("Write", value.Ticks, (int)kind, type, capacity));
                }
                Assert.Contains("60=" + legacy[0] + "\x01", Call("Generated", value));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void DateFormatter_MatchesLegacyAcrossAFullGregorianLeapCycle()
    {
        var start = new DateOnly(2000, 1, 1);
        var end = new DateOnly(2400, 1, 1);
        for (var date = start; date < end; date = date.AddDays(1))
        {
            Assert.Equal("60=" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "\x01",
                Call("Write", date.ToDateTime(TimeOnly.MinValue).Ticks, 0, 1, 12));
        }
    }
}
