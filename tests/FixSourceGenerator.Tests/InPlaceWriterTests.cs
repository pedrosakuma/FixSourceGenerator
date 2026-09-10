using System;
using System.Linq;

namespace FixSourceGenerator.Tests;

public class InPlaceWriterTests
{
    private static readonly string[] Sources = TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _)
        .Select(file => file.content).ToArray();

    [Theory]
    [InlineData("message.SetPrice(1m)")]
    [InlineData("message.SetClOrdID(\"ALT\"u8)")]
    public void Set_does_not_bypass_required_inputs_or_required_scopes(string operation)
    {
        string driver = $$"""
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static void Run()
                {
                    Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                    NewOrderSingleWriter.InitializeState(state);
                    var message = new NewOrderSingleWriter(new byte[128], state, "ORD"u8);
                    {{operation}};
                }
            }
            """;
        var errors = TestSupport.Compile(Sources.Append(driver)).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
        Assert.Equal("CS1061", Assert.Single(errors).Id);
    }

    [Theory]
    [InlineData("7.25m", "7.25")]
    [InlineData("7L", "7")]
    [InlineData("725L, 2", "7.25")]
    public void Set_keeps_current_handle_live_and_invalidates_copies(string value, string expectedPrice)
    {
        string driver = $$"""
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static byte[] Run()
                {
                    Span<byte> buffer = stackalloc byte[512];
                    Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                    NewOrderSingleWriter.InitializeState(state);
                    var message = new NewOrderSingleWriter(buffer, state, "ORD"u8);
                    var instrument = message.BeginInstrument("SYM"u8);
                    var oldInstrument = instrument;
                    Span<byte> id = stackalloc byte[] { 65, 66 };
                    instrument.SetSecurityID(id);
                    id.Clear();
                    try { oldInstrument.SetSecurityID("BAD"u8); throw new Exception("Copy remained live."); }
                    catch (InvalidOperationException) { }
                    var tail = instrument.EndInstrument(Side.Buy, 100m);
                    var oldTail = tail;
                    tail.SetPrice({{value}});
                    try { oldTail.SetExecInst(ReadOnlySpan<byte>.Empty); throw new Exception("Copy remained live."); }
                    catch (InvalidOperationException) { }
                    tail.SetTransactTime(new DateTime(2024, 1, 1));
                    var end = tail.WriteExecInst("A"u8);
                    try { _ = tail.Finish(); throw new Exception("Fluent source remained live."); }
                    catch (InvalidOperationException) { }
                    return buffer.Slice(0, end.Finish()).ToArray();
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(Sources.Append(driver));
        var frame = (byte[])assembly.GetType("Driver")!.GetMethod("Run")!.Invoke(null, null)!;
        string[] fields = System.Text.Encoding.ASCII.GetString(frame).Split('\x01');
        Assert.Contains("48=AB", fields);
        Assert.Single(fields, field => field == "44=" + expectedPrice);
    }

    [Theory]
    [InlineData("tail.SetPrice(1L, 19)", "ArgumentOutOfRangeException")]
    [InlineData("tail.SetExecInst(ReadOnlySpan<byte>.Empty)", "ArgumentException")]
    [InlineData("tail.SetExecInst(new byte[4096])", "ArgumentException")]
    [InlineData("tail.SetPrice(1m); tail.SetPrice(2m)", "InvalidOperationException")]
    [InlineData("tail.SetTransactTime(new DateTime(2024, 1, 1)); tail.SetPrice(1m)", "InvalidOperationException")]
    public void Invalid_in_place_writes_poison_the_owner(string operation, string exceptionType)
    {
        string driver = $$"""
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static bool Run()
                {
                    Span<byte> buffer = stackalloc byte[512];
                    Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                    NewOrderSingleWriter.InitializeState(state);
                    var message = new NewOrderSingleWriter(buffer, state, "ORD"u8);
                    var tail = message.BeginInstrument("SYM"u8).EndInstrument(Side.Buy, 100m);
                    var copy = tail;
                    try { {{operation}}; return false; }
                    catch ({{exceptionType}}) { }
                    try { _ = tail.Finish(); return false; }
                    catch (InvalidOperationException) { }
                    try { _ = copy.Finish(); return false; }
                    catch (InvalidOperationException) { return true; }
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(Sources.Append(driver));
        Assert.True((bool)assembly.GetType("Driver")!.GetMethod("Run")!.Invoke(null, null)!);
    }
}
