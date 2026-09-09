using System;
using System.Linq;
using System.Reflection;
using System.Text;

namespace FixSourceGenerator.Tests;

public class TemporalReaderTests
{
    private const string Driver = @"
using System;
using System.Globalization;
using System.Text;
using Acme.Fix.V44.Runtime;

public static class TemporalReaderDriver
{
    private static readonly string[] TimestampFormats =
    {
        ""yyyyMMdd-HH:mm:ss.fff"",
        ""yyyyMMdd-HH:mm:ss"",
    };

    private static readonly string[] TimeFormats =
    {
        ""HH:mm:ss.fff"",
        ""HH:mm:ss"",
    };

    public static bool DateMatchesFramework(byte[] value)
    {
        string text = Encoding.Latin1.GetString(value);
        bool expectedSuccess = DateOnly.TryParseExact(
            text, ""yyyyMMdd"", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly expected);
        bool actualSuccess = FixSpanReader.TryParseDateOnly(value, out DateOnly actual);
        return actualSuccess == expectedSuccess && actual == expected;
    }

    public static bool TimeMatchesFramework(byte[] value)
    {
        string text = Encoding.Latin1.GetString(value);
        bool expectedSuccess = TimeOnly.TryParseExact(
            text, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly expected);
        bool actualSuccess = FixSpanReader.TryParseTimeOnly(value, out TimeOnly actual);
        return actualSuccess == expectedSuccess && actual == expected;
    }

    public static bool TimestampMatchesFramework(byte[] value)
    {
        string text = Encoding.Latin1.GetString(value);
        bool expectedSuccess = DateTime.TryParseExact(
            text,
            TimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime expected);
        bool actualSuccess = FixSpanReader.TryParseDateTime(value, out DateTime actual);
        return actualSuccess == expectedSuccess && actual == expected && actual.Kind == expected.Kind;
    }

    public static long ParseTimestampTicks(byte[] value)
    {
        return FixSpanReader.TryParseDateTime(value, out DateTime result) ? result.Ticks : -1;
    }

    public static int ParseTimestampKind(byte[] value)
    {
        return FixSpanReader.TryParseDateTime(value, out DateTime result) ? (int)result.Kind : -1;
    }

    public static long CommonPathsAllocatedBytes(int iterations)
    {
        ReadOnlySpan<byte> date = ""20240229""u8;
        ReadOnlySpan<byte> time = ""23:59:59.999""u8;
        ReadOnlySpan<byte> timestamp = ""20240229-23:59:59.999""u8;

        FixSpanReader.TryParseDateOnly(date, out _);
        FixSpanReader.TryParseTimeOnly(time, out _);
        FixSpanReader.TryParseDateTime(timestamp, out _);

        long before = GC.GetAllocatedBytesForCurrentThread();
        long checksum = 0;
        for (int i = 0; i < iterations; i++)
        {
            if (!FixSpanReader.TryParseDateOnly(date, out DateOnly parsedDate)
                || !FixSpanReader.TryParseTimeOnly(time, out TimeOnly parsedTime)
                || !FixSpanReader.TryParseDateTime(timestamp, out DateTime parsedTimestamp))
            {
                return -1;
            }

            checksum += parsedDate.Day + parsedTime.Millisecond + parsedTimestamp.Millisecond;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        return checksum == iterations * 2027L ? allocated : -1;
    }
}
";

    private static readonly Lazy<(Assembly Assembly, Type Driver)> Generated = new(Build);

    [Theory]
    [InlineData("00010101")]
    [InlineData("00040229")]
    [InlineData("04000229")]
    [InlineData("19000228")]
    [InlineData("20000229")]
    [InlineData("20240229")]
    [InlineData("99991231")]
    [InlineData("")]
    [InlineData("00000000")]
    [InlineData("00010100")]
    [InlineData("00010229")]
    [InlineData("19000229")]
    [InlineData("21000229")]
    [InlineData("20240001")]
    [InlineData("20241301")]
    [InlineData("20240431")]
    [InlineData("2024O101")]
    [InlineData("2024-101")]
    [InlineData("2024010")]
    [InlineData("202401011")]
    public void Date_fast_path_matches_framework_contract(string input)
    {
        Assert.True(Call<bool>("DateMatchesFramework", Encoding.ASCII.GetBytes(input)));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("23:59:59")]
    [InlineData("00:00:00.000")]
    [InlineData("23:59:59.999")]
    [InlineData("")]
    [InlineData("24:00:00")]
    [InlineData("23:60:00")]
    [InlineData("23:59:60")]
    [InlineData("12-34:56")]
    [InlineData("12:34-56")]
    [InlineData("12:34:56,123")]
    [InlineData("12:34:56.12")]
    [InlineData("12:34:56.1234")]
    [InlineData("1X:34:56")]
    [InlineData("12:34:5")]
    public void Time_fast_path_matches_framework_contract(string input)
    {
        Assert.True(Call<bool>("TimeMatchesFramework", Encoding.ASCII.GetBytes(input)));
    }

    [Theory]
    [InlineData("00010101-00:00:00")]
    [InlineData("20000229-23:59:59.999")]
    [InlineData("99991231-23:59:59")]
    [InlineData("")]
    [InlineData("19000229-12:34:56")]
    [InlineData("20240230-12:34:56")]
    [InlineData("20240229_12:34:56")]
    [InlineData("20240229-24:00:00")]
    [InlineData("20240229-23:59:60")]
    [InlineData("20240229-12:34:56,123")]
    [InlineData("20240229-12:34:56.12")]
    [InlineData("20240229-12:34:56.1234")]
    [InlineData("20240229-12:34:5")]
    public void Timestamp_fast_path_matches_framework_contract(string input)
    {
        Assert.True(Call<bool>("TimestampMatchesFramework", Encoding.ASCII.GetBytes(input)));
    }

    [Fact]
    public void Non_ascii_bytes_and_truncations_match_framework_failure_defaults()
    {
        byte[][] inputs =
        {
            new byte[] { 0xFF, (byte)'0', (byte)'0', (byte)'1', (byte)'0', (byte)'1', (byte)'0', (byte)'1' },
            new byte[] { (byte)'2', (byte)'3', 0xB2, (byte)'5', (byte)'9', (byte)':', (byte)'5', (byte)'9' },
            Encoding.ASCII.GetBytes("20240229-23:59:59.999").Select((value, index) => index == 19 ? (byte)0xE9 : value).ToArray(),
        };

        Assert.True(Call<bool>("DateMatchesFramework", inputs[0]));
        Assert.True(Call<bool>("TimeMatchesFramework", inputs[1]));
        Assert.True(Call<bool>("TimestampMatchesFramework", inputs[2]));

        byte[] timestamp = Encoding.ASCII.GetBytes("20240229-23:59:59.999");
        for (int length = 0; length < timestamp.Length; length++)
        {
            Assert.True(Call<bool>("TimestampMatchesFramework", timestamp[..length]));
        }
    }

    [Fact]
    public void Timestamp_fast_path_preserves_value_and_utc_kind()
    {
        byte[] input = Encoding.ASCII.GetBytes("20240229-12:34:56.789");
        var expected = new DateTime(2024, 2, 29, 12, 34, 56, 789, DateTimeKind.Utc);

        Assert.Equal(expected.Ticks, Call<long>("ParseTimestampTicks", input));
        Assert.Equal((int)DateTimeKind.Utc, Call<int>("ParseTimestampKind", input));
    }

    [Fact]
    public void Common_paths_do_not_allocate()
    {
        Assert.Equal(0, Call<long>("CommonPathsAllocatedBytes", 10_000));
    }

    private static (Assembly Assembly, Type Driver) Build()
    {
        var files = TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _);
        var sources = files.Select(file => file.content).Append(Driver);
        Assembly assembly = TestSupport.EmitAndLoad(sources, "TemporalReaderGeneratedAssembly");
        return (assembly, assembly.GetType("TemporalReaderDriver")!);
    }

    private static T Call<T>(string method, params object[] args)
        => (T)Generated.Value.Driver.GetMethod(method)!.Invoke(null, args)!;
}
