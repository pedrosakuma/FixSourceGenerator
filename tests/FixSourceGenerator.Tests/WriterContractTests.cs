using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FixSourceGenerator.Tests;

public class WriterContractTests
{
    [Fact]
    public void Shared_scope_support_names_do_not_collide_with_FIX_definitions()
    {
        const string xml = """
            <fix type="FIX" major="4" minor="4" servicepack="0">
              <header/><trailer/>
              <messages>
                <message name="WriterScopes" msgtype="U1" msgcat="app">
                  <field name="FixWriterContinuation1" required="Y"/>
                  <component name="Detail" required="Y"/>
                </message>
              </messages>
              <components>
                <component name="Detail"><field name="FixWriterScopeExtensions" required="Y"/></component>
              </components>
              <fields>
                <field number="1001" name="FixWriterContinuation1" type="CHAR">
                  <value enum="1" description="ONE"/>
                </field>
                <field number="1002" name="FixWriterScopeExtensions" type="CHAR">
                  <value enum="1" description="ONE"/>
                </field>
              </fields>
            </fix>
            """;
        var schema = global::FixSourceGenerator.Schema.SchemaReader.Parse(xml, "names.xml", _ => { });
        Assert.NotNull(schema);
        var sources = TestSupport.Generate(schema!, out var diagnostics).ToArray();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(sources.Length, sources.Select(file => file.hintName).Distinct().Count());
        const string driver = """
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class NamingDriver
            {
                public static int Encode()
                {
                    Span<byte> destination = stackalloc byte[128];
                    Span<FixWriterState> state = stackalloc FixWriterState[WriterScopesWriter.RequiredStateLength];
                    WriterScopesWriter.InitializeState(state);
                    var message = new WriterScopesWriter(destination, state, (FixWriterContinuation1)'1');
                    return message.BeginDetail((FixWriterScopeExtensions)'1').EndDetail().Finish();
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(sources.Select(file => file.content).Append(driver));
        Assert.True((int)assembly.GetType("NamingDriver")!.GetMethod("Encode")!.Invoke(null, null)! > 0);
    }

    [Fact]
    public void Shared_templates_preserve_nested_continuations_and_definition_identity()
    {
        const string xml = """
            <fix type="FIX" major="4" minor="4" servicepack="0">
              <header/><trailer/>
              <messages>
                <message name="FirstMessage" msgtype="U1" msgcat="app">
                  <field name="FirstID" required="Y"/>
                  <component name="Shared" required="Y"/>
                  <field name="AfterShared" required="Y"/>
                  <group name="NoRows" required="N"><field name="FirstRowID" required="Y"/></group>
                </message>
                <message name="SecondMessage" msgtype="U2" msgcat="app">
                  <field name="SecondID" required="Y"/>
                  <component name="Shared" required="Y"/>
                  <field name="AfterShared" required="Y"/>
                  <group name="NoRows" required="N"><field name="SecondRowID" required="Y"/></group>
                </message>
              </messages>
              <components>
                <component name="Shared">
                  <field name="SharedID" required="Y"/>
                  <component name="Inner" required="Y"/>
                  <field name="AfterInner" required="Y"/>
                  <field name="SharedText" required="N"/>
                </component>
                <component name="Inner"><field name="InnerID" required="Y"/></component>
              </components>
              <fields>
                <field number="1000" name="FirstID" type="STRING"/>
                <field number="1001" name="SecondID" type="STRING"/>
                <field number="1002" name="SharedID" type="STRING"/>
                <field number="1003" name="InnerID" type="STRING"/>
                <field number="1004" name="AfterInner" type="INT"/>
                <field number="1005" name="SharedText" type="STRING"/>
                <field number="1006" name="AfterShared" type="INT"/>
                <field number="1100" name="NoRows" type="NUMINGROUP"/>
                <field number="1101" name="FirstRowID" type="STRING"/>
                <field number="1102" name="SecondRowID" type="STRING"/>
              </fields>
            </fix>
            """;
        var schema = global::FixSourceGenerator.Schema.SchemaReader.Parse(xml, "shared-writers.xml", _ => { });
        Assert.NotNull(schema);
        var generated = TestSupport.Generate(schema!, out var diagnostics);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Single(generated, file => file.hintName.Contains(".SharedWriterScope", StringComparison.Ordinal));
        Assert.Equal(2, generated.Count(file => file.hintName.Contains(".NoRowsWriterGroup", StringComparison.Ordinal)));

        string support = Assert.Single(
            generated,
            file => file.hintName.EndsWith(".WriterScopes.Support.g.cs", StringComparison.Ordinal)).content;
        Assert.Contains("struct FixWriterContinuation", support);
        Assert.Contains("<TOuter>", support);

        string driver = """
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;

            public static class SharedWriterDriver
            {
                public static string Run()
                {
                    Span<byte> firstDestination = stackalloc byte[256];
                    Span<FixWriterState> firstState = stackalloc FixWriterState[FirstMessageWriter.RequiredStateLength];
                    FirstMessageWriter.InitializeState(firstState);
                    var first = new FirstMessageWriter(firstDestination, firstState, "A"u8);
                    var sharedTerminal = first.BeginShared("S"u8)
                        .BeginInner("I"u8)
                        .EndInner(7)
                        .SkipSharedText();
                    var alias = sharedTerminal;
                    var firstGroup = sharedTerminal.EndShared(8).BeginNoRows(1);
                    try { _ = alias.EndShared(9); }
                    catch (InvalidOperationException)
                    {
                        firstGroup = firstGroup.BeginEntry("R1"u8).EndEntry();
                        _ = firstGroup.EndGroup().Finish();

                        var secondDestination = new byte[256];
                        var secondState = new FixWriterState[SecondMessageWriter.RequiredStateLength];
                        SecondMessageWriter.InitializeState(secondState);
                        var secondGroup = new SecondMessageWriter(secondDestination, secondState, "B"u8)
                            .BeginShared("S2"u8)
                            .BeginInner("I2"u8)
                            .EndInner(10)
                            .SkipSharedText()
                            .EndShared(11)
                            .BeginNoRows(1);
                        secondGroup = secondGroup.BeginEntry("R2"u8).EndEntry();
                        _ = secondGroup.EndGroup().Finish();
                        return "shared";
                    }
                    return "alias-live";
                }
            }
            """;
        var sources = generated.Select(file => file.content).Append(driver).ToArray();
        var assembly = TestSupport.EmitAndLoad(sources, "SharedWriterAssembly");
        Assert.Equal("shared", assembly.GetType("SharedWriterDriver")!.GetMethod("Run")!.Invoke(null, null));

        string missingInputs = """
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class MissingContinuationInputs
            {
                public static void Invalid()
                {
                    Span<byte> destination = stackalloc byte[256];
                    Span<FixWriterState> state = stackalloc FixWriterState[FirstMessageWriter.RequiredStateLength];
                    FirstMessageWriter.InitializeState(state);
                    _ = new FirstMessageWriter(destination, state, "A"u8)
                        .BeginShared("S"u8).BeginInner("I"u8).EndInner();
                }
            }
            """;
        Assert.Contains(
            TestSupport.Compile(generated.Select(file => file.content).Append(missingInputs)).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS7036");
    }

    [Theory]
    [InlineData("WriteValue(7m)", "7")]
    [InlineData("WriteValue(7L)", "7")]
    [InlineData("WriteValue(70L, 1)", "7.0")]
    [InlineData("SkipValue()", null)]
    [InlineData("SkipDetails()", null)]
    [InlineData("BeginDetails().EndDetails()", null)]
    [InlineData("SkipNoNested()", null)]
    [InlineData("BeginNoNested(0).EndGroup()", null)]
    public void Optional_tail_does_not_reinvoke_required_int_constructor(string operation, string? expectedValue)
    {
        const string xml = """
            <fix type="FIX" major="4" minor="4" servicepack="0">
              <header/><trailer/>
              <messages>
                <message name="IntegerEntryMessage" msgtype="U1" msgcat="app">
                  <group name="NoRows" required="Y">
                    <field name="EntryID" required="Y"/>
                    <field name="Value" required="N"/>
                    <component name="Details" required="N"/>
                    <group name="NoNested" required="N"><field name="NestedID" required="Y"/></group>
                  </group>
                </message>
              </messages>
              <components>
                <component name="Details"><field name="Text" required="N"/></component>
              </components>
              <fields>
                <field number="1000" name="NoRows" type="NUMINGROUP"/>
                <field number="1001" name="EntryID" type="INT"/>
                <field number="1002" name="Value" type="PRICE"/>
                <field number="1003" name="Text" type="STRING"/>
                <field number="2000" name="NoNested" type="NUMINGROUP"/>
                <field number="2001" name="NestedID" type="STRING"/>
              </fields>
            </fix>
            """;
        var schema = global::FixSourceGenerator.Schema.SchemaReader.Parse(xml, "integer-entry.xml", _ => { });
        Assert.NotNull(schema);
        var sources = TestSupport.Generate(schema!, out var diagnostics).Select(file => file.content).ToArray();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        string driver = """
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class IntegerEntryDriver
            {
                public static string Encode()
                {
                    Span<byte> destination = stackalloc byte[256];
                    Span<FixWriterState> state = stackalloc FixWriterState[IntegerEntryMessageWriter.RequiredStateLength];
                    IntegerEntryMessageWriter.InitializeState(state);
                    var message = new IntegerEntryMessageWriter(destination, state);
                    var group = message.BeginNoRows(1);
            """ + "group = group.BeginEntry(42)." + operation + ".EndEntry();" + """
                    int length = group.EndGroup().Finish();
                    return System.Text.Encoding.ASCII.GetString(destination.Slice(0, length));
                }

                public static bool RejectOutOfOrder()
                {
                    Span<byte> destination = stackalloc byte[256];
                    Span<FixWriterState> state = stackalloc FixWriterState[IntegerEntryMessageWriter.RequiredStateLength];
                    IntegerEntryMessageWriter.InitializeState(state);
                    var message = new IntegerEntryMessageWriter(destination, state);
                    var group = message.BeginNoRows(1);
            """ + "var entry = group.BeginEntry(42)." + operation + ";" + """
                    try { _ = entry.WriteValue(8m); return false; }
                    catch (InvalidOperationException) { }
                    try { _ = entry.EndEntry(); return false; }
                    catch (InvalidOperationException) { return true; }
                }
            }
            """;
        var assembly = TestSupport.EmitAndLoad(sources.Append(driver));
        string wire = (string)assembly.GetType("IntegerEntryDriver")!.GetMethod("Encode")!.Invoke(null, null)!;
        Assert.Equal(expectedValue is not null ? new[] { "1001=42", "1002=" + expectedValue } : new[] { "1001=42" },
            wire.Split('\x01').Where(field => field.StartsWith("1001=", StringComparison.Ordinal) ||
                field.StartsWith("1002=", StringComparison.Ordinal)).ToArray());
        Assert.True((bool)assembly.GetType("IntegerEntryDriver")!.GetMethod("RejectOutOfOrder")!.Invoke(null, null)!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Component_led_group_cannot_finalize_without_its_delimiter(bool writeOtherField)
    {
        const string xml = """
            <fix type="FIX" major="4" minor="4" servicepack="0">
              <header/><trailer/>
              <messages>
                <message name="ComponentEntryMessage" msgtype="U1" msgcat="app">
                  <group name="NoRows" required="Y">
                    <component name="Leading" required="N"/>
                    <field name="Value" required="N"/>
                  </group>
                </message>
              </messages>
              <components>
                <component name="Leading"><field name="Symbol" required="N"/></component>
              </components>
              <fields>
                <field number="1000" name="NoRows" type="NUMINGROUP"/>
                <field number="55" name="Symbol" type="STRING"/>
                <field number="1002" name="Value" type="INT"/>
              </fields>
            </fix>
            """;
        var schema = global::FixSourceGenerator.Schema.SchemaReader.Parse(xml, "component-entry.xml", _ => { });
        Assert.NotNull(schema);
        var sources = TestSupport.Generate(schema!, out var diagnostics).Select(file => file.content).ToArray();
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(TestSupport.Compile(sources).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        string transition = writeOtherField
            ? "group = group.BeginEntry().SkipLeading().WriteValue(1).EndEntry();"
            : "group = group.BeginEntry().EndEntry();";
        string driver = """
            using System;
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class MissingDelimiterDriver
            {
                public static void Attempt()
                {
                    Span<byte> destination = stackalloc byte[256];
                    Span<FixWriterState> state = stackalloc FixWriterState[ComponentEntryMessageWriter.RequiredStateLength];
                    ComponentEntryMessageWriter.InitializeState(state);
                    var message = new ComponentEntryMessageWriter(destination, state);
                    var group = message.BeginNoRows(1);
            """ + transition + """
                    _ = group.EndGroup().Finish();
                }
            }
            """;
        var compilationErrors = TestSupport.Compile(sources.Append(driver)).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (compilationErrors.Length > 0)
        {
            // Either the type phases prohibit the omission or runtime closure must reject it.
            Assert.All(compilationErrors, diagnostic =>
            {
                string message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(
                    diagnostic.Id == "CS1061" && (message.Contains("EndEntry") || message.Contains("SkipLeading")) ||
                    diagnostic.Id == "CS7036" && message.Contains("BeginEntry"),
                    diagnostic.ToString());
            });
            return;
        }

        var assembly = TestSupport.EmitAndLoad(sources.Append(driver));
        var method = assembly.GetType("MissingDelimiterDriver")!.GetMethod("Attempt")!;
        var exception = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, null));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private const string Driver = """
        using System;
        using System.Text;
        using Acme.Fix.V44;
        using Acme.Fix.V44.Runtime;

        public static class WriterContractDriver
        {
            private static Span<FixWriterState> Initialize(FixWriterState[] state)
            {
                NewOrderSingleWriter.InitializeState(state);
                return state;
            }

            public static byte[] Complete(int capacity, bool nested)
            {
                var destination = new byte[capacity];
                var stateArray = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                var message = new NewOrderSingleWriter(destination, Initialize(stateArray), "ORD"u8);
                var instrument = message.BeginInstrument("SYM"u8);
                var tail = instrument.WriteSecurityID("SEC"u8).EndInstrument(Side.Buy, 0m);
                var group = tail.WritePrice(0m).SkipTransactTime().SkipExecInst().BeginNoAllocs(1);
                if (nested)
                {
                    var entry = group.BeginEntry("ACC"u8, 0m);
                    var inner = entry.BeginNoNested(1);
                    inner = inner.BeginEntry("PARTY"u8).EndEntry();
                    var completedEntry = inner.EndGroup();
                    group = completedEntry.EndEntry();
                }
                else
                {
                    group = group.BeginEntry("ACC"u8, 0m).SkipNoNested().EndEntry();
                }
                int length = group.EndGroup().Finish();
                return destination.AsSpan(0, length).ToArray();
            }

            public static string EmptyRequired()
            {
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                try { _ = new NewOrderSingleWriter(new byte[128], state, ReadOnlySpan<byte>.Empty); }
                catch (ArgumentException) { return "empty"; }
                return "accepted";
            }

            public static string EmptyOptionalPoisons()
            {
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                var message = new NewOrderSingleWriter(new byte[256], state, "ORD"u8);
                var instrument = message.BeginInstrument("SYM"u8);
                var copy = instrument;
                try { _ = instrument.WriteSecurityID(ReadOnlySpan<byte>.Empty); }
                catch (ArgumentException)
                {
                    try { _ = copy.SkipSecurityID(); }
                    catch (InvalidOperationException) { return "poisoned"; }
                }
                return "accepted";
            }

            public static string InvalidEnumPoisons()
            {
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                var message = new NewOrderSingleWriter(new byte[256], state, "ORD"u8);
                var instrument = message.BeginInstrument("SYM"u8).SkipSecurityID();
                var copy = instrument;
                try { _ = instrument.EndInstrument((Side)99, 1m); }
                catch (ArgumentOutOfRangeException)
                {
                    try { _ = copy.EndInstrument(Side.Buy, 1m); }
                    catch (InvalidOperationException) { return "poisoned"; }
                }
                return "accepted";
            }

            public static string InvalidRequiredScalePoisons()
            {
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                var message = new NewOrderSingleWriter(new byte[256], state, "ORD"u8);
                var instrument = message.BeginInstrument("SYM"u8).SkipSecurityID();
                var copy = instrument;
                try { _ = instrument.EndInstrument(Side.Buy, FixDecimal.FromScaled(1, 19)); }
                catch (ArgumentOutOfRangeException)
                {
                    try { _ = copy.EndInstrument(Side.Buy, 1m); }
                    catch (InvalidOperationException) { return "poisoned"; }
                }
                return "accepted";
            }

            public static string StaleCopies()
            {
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                var message = new NewOrderSingleWriter(new byte[256], state, "ORD"u8);
                var messageCopy = message;
                var instrument = message.BeginInstrument("SYM"u8);
                try { _ = messageCopy.BeginInstrument("OTHER"u8); }
                catch (InvalidOperationException)
                {
                    var instrumentCopy = instrument;
                    var afterSetter = instrument.WriteSecurityID("SEC"u8);
                    try { _ = instrumentCopy.SkipSecurityID(); }
                    catch (InvalidOperationException)
                    {
                        var tail = afterSetter.EndInstrument(Side.Buy, 1m);
                        var tailCopy = tail;
                        var next = tail.WritePrice(0m);
                        try { _ = tailCopy.SkipPrice(); }
                        catch (InvalidOperationException)
                        {
                            _ = next.SkipTransactTime().SkipExecInst().SkipNoAllocs().Finish();
                            return "stale";
                        }
                    }
                }
                return "accepted";
            }

            public static string DefaultHandle()
            {
                var writer = default(NewOrderSingleWriter);
                try { _ = writer.BeginInstrument("SYM"u8); }
                catch (InvalidOperationException) { return "default"; }
                return "accepted";
            }

            public static string StateReuse()
            {
                var destination = new byte[512];
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                var first = new NewOrderSingleWriter(destination, state, "ONE"u8);
                try { _ = new NewOrderSingleWriter(destination, state, "TWO"u8); }
                catch (InvalidOperationException)
                {
                    var completed = first.BeginInstrument("SYM"u8).SkipSecurityID()
                        .EndInstrument(Side.Buy, 1m).SkipPrice().SkipTransactTime()
                        .SkipExecInst().SkipNoAllocs();
                    var stale = completed;
                    _ = completed.Finish();
                    NewOrderSingleWriter.InitializeState(state);
                    var second = new NewOrderSingleWriter(destination, state, "TWO"u8);
                    try { _ = stale.Finish(); }
                    catch (InvalidOperationException)
                    {
                        _ = second.BeginInstrument("SYM"u8).SkipSecurityID()
                            .EndInstrument(Side.Buy, 1m).SkipPrice().SkipTransactTime()
                            .SkipExecInst().SkipNoAllocs().Finish();
                        return "safe";
                    }
                }
                return "accepted";
            }

            public static string CountMismatch(bool excess)
            {
                var state = new FixWriterState[NewOrderSingleWriter.RequiredStateLength];
                NewOrderSingleWriter.InitializeState(state);
                var message = new NewOrderSingleWriter(new byte[256], state, "ORD"u8);
                var group = message.BeginInstrument("SYM"u8).SkipSecurityID()
                    .EndInstrument(Side.Buy, 1m).SkipPrice().SkipTransactTime()
                    .SkipExecInst().BeginNoAllocs(excess ? 0 : 1);
                var copy = group;
                try
                {
                    if (excess) _ = group.BeginEntry("ACC"u8, 1m);
                    else _ = group.EndGroup();
                }
                catch (InvalidOperationException)
                {
                    try { _ = copy.EndGroup(); }
                    catch (InvalidOperationException) { return "poisoned"; }
                }
                return "accepted";
            }

            public static string Capacity(int capacity)
            {
                try { _ = Complete(capacity, nested: true); }
                catch (ArgumentException ex) when (ex.ParamName == "destination") { return "capacity"; }
                return "ok";
            }

            public static string Overlap()
            {
                var bytes = new byte[256];
                Span<FixWriterState> state = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, FixWriterState>(bytes);
                NewOrderSingleWriter.InitializeState(state);
                try { _ = new NewOrderSingleWriter(bytes, state, "ORD"u8); }
                catch (ArgumentException ex) when (ex.ParamName == "state") { return "overlap"; }
                return "accepted";
            }
        }
        """;

    private static readonly Lazy<Type> DriverType = new(() => TestSupport.EmitAndLoad(
        Sources(), "WriterContractAssembly").GetType("WriterContractDriver")!);

    private static string[] Sources() =>
        TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _)
            .Select(file => file.content).Append(Driver).ToArray();

    private static T Call<T>(string method, params object[] args) =>
        (T)DriverType.Value.GetMethod(method)!.Invoke(null, args)!;

    [Fact]
    public void RequiredAndOptionalValues_AreValidatedAndPoisonAllCopies()
    {
        Assert.Equal("empty", Call<string>("EmptyRequired"));
        Assert.Equal("poisoned", Call<string>("EmptyOptionalPoisons"));
        Assert.Equal("poisoned", Call<string>("InvalidEnumPoisons"));
        Assert.Equal("poisoned", Call<string>("InvalidRequiredScalePoisons"));
    }

    [Fact]
    public void CopiedDefaultAndReusedHandles_CannotMutate()
    {
        Assert.Equal("stale", Call<string>("StaleCopies"));
        Assert.Equal("default", Call<string>("DefaultHandle"));
        Assert.Equal("safe", Call<string>("StateReuse"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpectedCount_RejectsShortfallAndExcess(bool excess)
    {
        Assert.Equal("poisoned", Call<string>("CountMismatch", excess));
    }

    [Fact]
    public void NestedGroups_PreserveOrderAndCountCompletedEntries()
    {
        string frame = Encoding.ASCII.GetString(Call<byte[]>("Complete", 512, true));
        Assert.Contains("11=ORD\x01" + "55=SYM\x01" + "48=SEC\x01" + "54=1\x01" +
            "38=0\x01" + "44=0\x01" + "78=1\x01" + "79=ACC\x01" + "80=0\x01" +
            "756=1\x01" + "757=PARTY\x01", frame);
    }

    [Fact]
    public void ExactCapacitySucceeds_AndEveryInsufficientCapacityFailsExplicitly()
    {
        int exact = Call<byte[]>("Complete", 512, true).Length;
        Assert.Equal("ok", Call<string>("Capacity", exact));
        for (int capacity = 0; capacity < exact; capacity++)
        {
            Assert.Equal("capacity", Call<string>("Capacity", capacity));
        }
    }

    [Fact]
    public void MetadataMustBeInitializedAndMustNotOverlapDestination()
    {
        Assert.Equal("overlap", Call<string>("Overlap"));
    }

    [Fact]
    public void GeneratedRuntimeAndStackInputs_CompileWithCSharp11()
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp11);
        var compilation = TestSupport.Compile(Sources())
            .RemoveAllSyntaxTrees()
            .AddSyntaxTrees(Sources().Select(source => CSharpSyntaxTree.ParseText(source, parseOptions)));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void MissingRequiredScopes_DoNotExposeFinish()
    {
        const string invalid = """
            using Acme.Fix.V44;
            using Acme.Fix.V44.Runtime;
            public static class Invalid
            {
                public static int Encode(byte[] destination, FixWriterState[] state)
                {
                    NewOrderSingleWriter.InitializeState(state);
                    var writer = new NewOrderSingleWriter(destination, state, "ORD"u8);
                    return writer.Finish();
                }
            }
            """;
        var errors = TestSupport.Compile(Sources().Append(invalid)).GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        Assert.Contains(errors, error => error.Id == "CS1061");
    }
}
