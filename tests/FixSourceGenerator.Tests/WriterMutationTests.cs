using System;
using System.Linq;

namespace FixSourceGenerator.Tests;

public class WriterMutationTests
{
    private static readonly string Runtime = TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _)
        .Single(file => file.hintName.EndsWith(".Runtime.FixRuntime.g.cs", StringComparison.Ordinal)).content;

    [Theory]
    [InlineData("\"x\"u8", 27, "ArgumentException")]
    [InlineData("1", 27, "ArgumentException")]
    [InlineData("1L", 27, "ArgumentException")]
    [InlineData("1L, 0", 27, "ArgumentException")]
    [InlineData("1m", 27, "ArgumentException")]
    [InlineData("(FixDecimal)1m", 27, "ArgumentException")]
    [InlineData("true", 27, "ArgumentException")]
    [InlineData("'A'", 27, "ArgumentException")]
    [InlineData("new DateTime(2024, 1, 1)", 27, "ArgumentException")]
    [InlineData("new DateOnly(2024, 1, 1)", 27, "ArgumentException")]
    [InlineData("new TimeOnly(12, 0)", 27, "ArgumentException")]
    [InlineData("1L, 19", 128, "ArgumentOutOfRangeException")]
    public void Failed_mutation_poison_survives_state_reacquisition(string value, int capacity, string exceptionType)
    {
        string driver = $$"""
            using System;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static bool Run()
                {
                    Span<FixWriterState> state = stackalloc FixWriterState[1];
                    FixWriterState.Initialize(state);
                    var context = FixWriterContext.Begin(new byte[{{capacity}}], state, 1, "FIX.4.4"u8, "D"u8);
                    var copy = context;
                    try { context.WriteField("1="u8, {{value}}); return false; }
                    catch ({{exceptionType}}) { }
                    if (!RejectWrite(ref context) || !RejectWrite(ref copy)) return false;
                    try { _ = context.Finish(); return false; }
                    catch (InvalidOperationException) { }

                    var next = FixWriterContext.Begin(new byte[128], state, 1, "FIX.4.4"u8, "D"u8);
                    if (!RejectWrite(ref context) || !RejectWrite(ref copy)) return false;
                    next.WriteField("1="u8, 1);
                    return next.Finish() > 0;
                }
                private static bool RejectWrite(ref FixWriterContext context)
                {
                    try { context.WriteField("2="u8, 1); return false; }
                    catch (InvalidOperationException) { return true; }
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(new[] { Runtime, driver });
        Assert.True((bool)assembly.GetType("Driver")!.GetMethod("Run")!.Invoke(null, null)!);
    }

    [Fact]
    public void Delimiter_parse_overflow_also_poisons_all_handles()
    {
        const string driver = """
            using System;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static bool Run()
                {
                    Span<FixWriterState> state = stackalloc FixWriterState[2];
                    FixWriterState.Initialize(state);
                    var context = FixWriterContext.Begin(new byte[128], state, 2, "FIX.4.4"u8, "D"u8);
                    context.BeginGroup("100="u8, 1);
                    context.BeginEntry(1);
                    var copy = context;
                    try { context.WriteField("999999999999="u8, 1); return false; }
                    catch (OverflowException) { }
                    try { copy.Validate(); return false; }
                    catch (InvalidOperationException) { }
                    try { context.Validate(); return false; }
                    catch (InvalidOperationException) { }
                    var next = FixWriterContext.Begin(new byte[128], state, 2, "FIX.4.4"u8, "D"u8);
                    return next.Finish() > 0;
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(new[] { Runtime, driver });
        Assert.True((bool)assembly.GetType("Driver")!.GetMethod("Run")!.Invoke(null, null)!);
    }

    [Fact]
    public void Rejected_guards_on_stale_handles_do_not_poison_the_live_owner()
    {
        const string driver = """
            using System;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static bool Run()
                {
                    Span<FixWriterState> state = stackalloc FixWriterState[1];
                    FixWriterState.Initialize(state);
                    var stale = FixWriterContext.Begin(new byte[128], state, 1, "FIX.4.4"u8, "D"u8);
                    var live = stale.Transfer();
                    try { stale.ValidateOrder(1, 1); return false; }
                    catch (InvalidOperationException) { }
                    try { stale.ValidateText(ReadOnlySpan<byte>.Empty, "Text", "value"); return false; }
                    catch (InvalidOperationException) { }
                    try { stale.ValidateCode(false, "value"); return false; }
                    catch (InvalidOperationException) { }
                    live.WriteField("1="u8, 1);
                    return live.Finish() > 0;
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(new[] { Runtime, driver });
        Assert.True((bool)assembly.GetType("Driver")!.GetMethod("Run")!.Invoke(null, null)!);
    }

    [Fact]
    public void Transfer_consumes_the_source_and_invalidates_prior_copies()
    {
        const string driver = """
            using System;
            using Acme.Fix.V44.Runtime;
            public static class Driver
            {
                public static bool Run()
                {
                    Span<FixWriterState> state = stackalloc FixWriterState[1];
                    FixWriterState.Initialize(state);
                    var context = FixWriterContext.Begin(new byte[128], state, 1, "FIX.4.4"u8, "D"u8);
                    var copy = context;
                    var next = context.Transfer();
                    try { context.Validate(); return false; }
                    catch (InvalidOperationException) { }
                    try { copy.Validate(); return false; }
                    catch (InvalidOperationException) { }
                    next.WriteField("1="u8, 1);
                    return next.Finish() > 0;
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(new[] { Runtime, driver });
        Assert.True((bool)assembly.GetType("Driver")!.GetMethod("Run")!.Invoke(null, null)!);
    }
}
