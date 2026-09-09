using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FixSourceGenerator.Tests;

public class WriterContractTests
{
    private const string Driver = """
        using System;
        using System.Buffers.Text;
        using System.Text;
        using Acme.Fix.V44;
        using Acme.Fix.V44.Runtime;

        public static class WriterContractDriver
        {
            private static void WriteValue(ref FixSpanWriter writer, int tag, int kind)
            {
                switch (kind)
                {
                    case 0: writer.WriteField(tag, int.MinValue); break;
                    case 1: writer.WriteField(tag, int.MaxValue); break;
                    case 2: writer.WriteField(tag, 0); break;
                    case 3: writer.WriteField(tag, decimal.MinValue); break;
                    case 4: writer.WriteField(tag, decimal.MaxValue); break;
                    case 5: writer.WriteField(tag, -0.0000000000000000000000000001m); break;
                    case 6: writer.WriteField(tag, 123.4500m); break;
                    case 7: writer.WriteField(tag, true); break;
                    case 8: writer.WriteField(tag, false); break;
                    case 9: writer.WriteField(tag, 'A'); break;
                    case 10: writer.WriteField(tag, "ABC"u8); break;
                    case 11: writer.WriteField(tag, ReadOnlySpan<byte>.Empty); break;
                    case 12: writer.WriteField(tag, new DateTime(2024, 2, 29, 23, 59, 59, 123, DateTimeKind.Utc)); break;
                    case 13: writer.WriteField(tag, DateOnly.MinValue); break;
                    case 14: writer.WriteField(tag, DateOnly.MaxValue); break;
                    case 15: writer.WriteField(tag, TimeOnly.MaxValue); break;
                    case 16: writer.WriteField(tag, long.MinValue); break;
                    case 17: writer.WriteField(tag, long.MaxValue); break;
                    case 18: writer.WriteField(tag, long.MinValue, 18); break;
                    case 19: writer.WriteField(tag, 0L, 18); break;
                    default: throw new ArgumentOutOfRangeException(nameof(kind));
                }
            }

            private static void AssertPoisoned(ref FixSpanWriter writer)
            {
                int position = writer.Position;
                for (int operation = 0; operation < 22; operation++)
                {
                    try
                    {
                        if (operation == 20) writer.BeginMessage("FIX.4.4"u8, "D"u8);
                        else if (operation == 21) writer.Finish();
                        else WriteValue(ref writer, 44, operation);
                    }
                    catch (InvalidOperationException)
                    {
                        if (writer.Position != position)
                            throw new Exception("An invalid writer advanced.");
                        continue;
                    }
                    throw new Exception("An invalid writer accepted operation " + operation);
                }
            }

            public static string Field(int capacity, int tag, int kind)
            {
                var destination = new byte[capacity + 1];
                destination[capacity] = 0xCC;
                var writer = new FixSpanWriter(destination.AsSpan(0, capacity));
                try
                {
                    WriteValue(ref writer, tag, kind);
                }
                catch (ArgumentException exception) when (exception.ParamName == "destination")
                {
                    AssertPoisoned(ref writer);
                    if (destination[capacity] != 0xCC || writer.Position > capacity)
                        throw new Exception("Write exceeded the destination.");
                    return "capacity";
                }
                return Encoding.ASCII.GetString(destination.AsSpan(0, writer.Position));
            }

            public static string Begin(int capacity, bool generated)
            {
                var destination = new byte[capacity];
                if (generated)
                {
                    try { _ = new NewOrderSingleWriter(destination); }
                    catch (ArgumentException exception) when (exception.ParamName == "destination")
                    {
                        return "capacity";
                    }
                    return "ok";
                }
                var writer = new FixSpanWriter(destination);
                try { writer.BeginMessage("FIX.4.4"u8, "D"u8); }
                catch (ArgumentException exception) when (exception.ParamName == "destination")
                {
                    AssertPoisoned(ref writer);
                    return "capacity";
                }
                return "ok";
            }

            public static string Group(int remaining)
            {
                var destination = new byte[24 + remaining];
                var writer = new NewOrderSingleWriter(destination);
                try { writer.WriteNoAllocs(int.MaxValue); }
                catch (ArgumentException exception) when (exception.ParamName == "destination")
                {
                    try { writer.Finish(); }
                    catch (InvalidOperationException) { return "capacity"; }
                    throw new Exception("Failed generated writer finalized.");
                }
                return "ok";
            }

            public static byte[] Frame(int bodyLength, int capacity, bool generated)
            {
                // MsgType is five bytes; 11=<value><SOH> accounts for four more.
                var value = new byte[bodyLength - 9];
                value.AsSpan().Fill((byte)'x');
                var destination = new byte[capacity];
                if (generated)
                {
                    var message = new NewOrderSingleWriter(destination);
                    message.WriteClOrdID(value);
                    try
                    {
                        return destination.AsSpan(0, message.Finish()).ToArray();
                    }
                    catch (ArgumentException exception) when (exception.ParamName == "destination")
                    {
                        try { message.Finish(); }
                        catch (InvalidOperationException) { return Array.Empty<byte>(); }
                        throw new Exception("Failed generated finalization succeeded on retry.");
                    }
                }
                var writer = new FixSpanWriter(destination);
                writer.BeginMessage("FIX.4.4"u8, "D"u8);
                writer.WriteField(11, value);
                int position = writer.Position;
                var beforeFinish = destination.AsSpan().ToArray();
                try
                {
                    return destination.AsSpan(0, writer.Finish()).ToArray();
                }
                catch (ArgumentException exception) when (exception.ParamName == "destination")
                {
                    if (writer.Position != position || !destination.AsSpan().SequenceEqual(beforeFinish))
                        throw new Exception("Failed Finish mutated the frame before checking capacity.");
                    AssertPoisoned(ref writer);
                    return Array.Empty<byte>();
                }
            }

            public static byte[] StackInputs(byte[] destination)
            {
                var writer = new NewOrderSingleWriter(destination.AsSpan());
                Span<byte> scratch = stackalloc byte[20];
                if (!Utf8Formatter.TryFormat(long.MinValue, scratch, out int written))
                    throw new Exception("Numeric ID formatting failed.");
                writer.WriteClOrdID(scratch.Slice(0, written));
                scratch.Fill((byte)'x');
                WriteGroup(ref writer);
                return destination.AsSpan(0, writer.Finish()).ToArray();
            }

            private static void WriteGroup(ref NewOrderSingleWriter writer)
            {
                Span<byte> scratch = stackalloc byte[20];
                if (!Utf8Formatter.TryFormat(long.MaxValue, scratch, out int written))
                    throw new Exception("Group ID formatting failed.");
                writer.WriteNoAllocs(1);
                writer.WriteAllocAccount(scratch.Slice(0, written));
                writer.WriteNoNested(1);
                writer.WriteNestedPartyID(scratch.Slice(0, written));
                scratch.Clear();
            }

            public static byte[] RuntimeStackInputs(byte[] destination)
            {
                var writer = new FixSpanWriter(destination.AsSpan());
                Span<byte> begin = stackalloc byte[] { 70, 73, 88, 46, 52, 46, 52 };
                Span<byte> type = stackalloc byte[] { 68 };
                writer.BeginMessage(begin, type);
                begin.Clear();
                type.Clear();
                WriteRuntimeId(ref writer);
                return destination.AsSpan(0, writer.Finish()).ToArray();
            }

            private static void WriteRuntimeId(ref FixSpanWriter writer)
            {
                Span<byte> scratch = stackalloc byte[20];
                if (!Utf8Formatter.TryFormat(long.MinValue, scratch, out int written))
                    throw new Exception("Numeric ID formatting failed.");
                writer.WriteField(11, scratch.Slice(0, written));
                scratch.Clear();
            }
        }
        """;

    private static readonly Lazy<Type> DriverType = new(() => TestSupport.EmitAndLoad(
        Sources(), "WriterContractAssembly").GetType("WriterContractDriver")!);

    private static string[] Sources()
    {
        // Exercise identical values and capacity failures through the runtime's two prefix paths.
        string prefixDriver = Driver.Replace("class WriterContractDriver", "class ConstantPrefixContractDriver")
            .Replace("writer.WriteField(tag,",
                """writer.WriteField(Encoding.ASCII.GetBytes(tag.ToString(System.Globalization.CultureInfo.InvariantCulture) + "="),""");
        return TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _)
            .Select(f => f.content).Append(Driver).Append(prefixDriver).ToArray();
    }

    private static T Call<T>(string method, params object[] args) =>
        (T)DriverType.Value.GetMethod(method)!.Invoke(null, args)!;

    private static string CallPrefix(int capacity, int tag, int kind) =>
        (string)DriverType.Value.Assembly.GetType("ConstantPrefixContractDriver")!
            .GetMethod("Field")!.Invoke(null, new object[] { capacity, tag, kind })!;

    [Theory]
    [InlineData(0, "-2147483648")]
    [InlineData(1, "2147483647")]
    [InlineData(2, "0")]
    [InlineData(3, "-79228162514264337593543950335")]
    [InlineData(4, "79228162514264337593543950335")]
    [InlineData(5, "-0.0000000000000000000000000001")]
    [InlineData(6, "123.4500")]
    [InlineData(7, "Y")]
    [InlineData(8, "N")]
    [InlineData(9, "A")]
    [InlineData(10, "ABC")]
    [InlineData(11, "")]
    [InlineData(12, "20240229-23:59:59.123")]
    [InlineData(13, "00010101")]
    [InlineData(14, "99991231")]
    [InlineData(15, "23:59:59.999")]
    [InlineData(16, "-9223372036854775808")]
    [InlineData(17, "9223372036854775807")]
    [InlineData(18, "-9.223372036854775808")]
    [InlineData(19, "0.000000000000000000")]
    public void EveryField_RejectsEveryTruncation_AndPoisonsWriter(int kind, string value)
    {
        foreach (int tag in new[] { 1, 44, 123456, int.MaxValue })
        {
            string expected = tag.ToString(CultureInfo.InvariantCulture) + "=" + value + "\x01";
            for (int capacity = 0; capacity < expected.Length; capacity++)
            {
                Assert.Equal("capacity", Call<string>("Field", capacity, tag, kind));
                Assert.Equal("capacity", CallPrefix(capacity, tag, kind));
            }

            Assert.Equal(expected, Call<string>("Field", expected.Length, tag, kind));
            Assert.Equal(expected, Call<string>("Field", expected.Length + 10, tag, kind));
            Assert.Equal(expected, CallPrefix(expected.Length, tag, kind));
            Assert.Equal(expected, CallPrefix(expected.Length + 10, tag, kind));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Envelope_RejectsEveryTruncation(bool generated)
    {
        for (int capacity = 0; capacity < 24; capacity++)
            Assert.Equal("capacity", Call<string>("Begin", capacity, generated));
        Assert.Equal("ok", Call<string>("Begin", 24, generated));
    }

    [Fact]
    public void GeneratedGroupCounter_RejectsTruncatedNumericValue_AndPoisonsWriter()
    {
        for (int remaining = 0; remaining < "78=2147483647\x01".Length; remaining++)
            Assert.Equal("capacity", Call<string>("Group", remaining));
        Assert.Equal("ok", Call<string>("Group", "78=2147483647\x01".Length));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(999)]
    [InlineData(1000)]
    [InlineData(99999)]
    [InlineData(100000)]
    [InlineData(999999)]
    [InlineData(1000000)]
    public void Finish_AccountsForBodyLengthShiftAndChecksum_BeforeMutating(int bodyLength)
    {
        int digitCount = bodyLength.ToString(CultureInfo.InvariantCulture).Length;
        int beforeFinishLength = 19 + bodyLength;
        int finalLength = 20 + digitCount + bodyLength;
        foreach (bool generated in new[] { false, true })
        {
            for (int capacity = beforeFinishLength; capacity < finalLength; capacity++)
                Assert.Empty(Call<byte[]>("Frame", bodyLength, capacity, generated));
            byte[] frame = Call<byte[]>("Frame", bodyLength, finalLength, generated);
            Assert.Equal(finalLength, frame.Length);
            AssertFrame(frame, "35=D\x01" + "11=" + new string('x', bodyLength - 9) + "\x01");
        }
    }

    [Fact]
    public void StackInputs_AreAcceptedAndCopied_ThroughRefHelpersAndNestedGroups()
    {
        AssertFrame(Call<byte[]>("StackInputs", new byte[256]),
            "35=D\x01" + "11=-9223372036854775808\x01" + "78=1\x01" +
            "79=9223372036854775807\x01" + "756=1\x01" + "757=9223372036854775807\x01");
        AssertFrame(Call<byte[]>("RuntimeStackInputs", new byte[256]),
            "35=D\x01" + "11=-9223372036854775808\x01");
    }

    [Fact]
    public void GeneratedRuntimeAndStackInputs_CompileWithCSharp11()
    {
        var compilation = TestSupport.Compile(Sources());
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp11);
        compilation = compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(
            Sources().Select(source => CSharpSyntaxTree.ParseText(source, parseOptions)));
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);
        Assert.Empty(errors);
    }

    [Fact]
    public void DestinationLifetime_IsStillRetained()
    {
        const string invalidConsumer = """
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class InvalidDestinationLifetime
            {
                public static NewOrderSingleWriter Message()
                {
                    Span<byte> destination = stackalloc byte[256];
                    return new NewOrderSingleWriter(destination);
                }
                public static FixSpanWriter Runtime()
                {
                    Span<byte> destination = stackalloc byte[256];
                    return new FixSpanWriter(destination);
                }
            }
            """;
        var errors = TestSupport.Compile(Sources().Append(invalidConsumer)).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Equal(2, errors.Count(d => d.Id == "CS8352"));
        Assert.Equal(2, errors.Count(d => d.Id == "CS8347"));
    }

    private static void AssertFrame(byte[] actual, string body)
    {
        string prefix = "8=FIX.4.4\x01" + "9=" +
            body.Length.ToString(CultureInfo.InvariantCulture) + "\x01" + body;
        int checksum = Encoding.ASCII.GetBytes(prefix).Sum(b => (int)b) & 0xFF;
        Assert.Equal(Encoding.ASCII.GetBytes(prefix + "10=" +
            checksum.ToString("D3", CultureInfo.InvariantCulture) + "\x01"), actual);
    }
}
