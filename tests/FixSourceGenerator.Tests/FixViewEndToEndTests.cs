using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FixSourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace FixSourceGenerator.Tests;

/// <summary>
/// End-to-end tests for the <c>[FixView]</c> selective-projection feature (issue #13): runs the
/// real <see cref="FixSourceGenerator"/> generator against both the FIX44-mini.xml schema AND a
/// consumer-authored <c>partial ref struct</c> annotated with <c>[FixView]</c>, verifying
/// matching, diagnostics and the emitted early-exit constructor/property bodies compile and
/// behave correctly against a real wire buffer.
/// </summary>
public class FixViewEndToEndTests
{
    private static string LoadTestData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

    private static (GeneratorDriverRunResult Result, CSharpCompilation Compilation) RunGenerator(string consumerSource, string? rootNamespace = "Acme")
    {
        return RunGeneratorWithSchema(consumerSource, LoadTestData("FIX44-mini.xml"), rootNamespace);
    }

    private static (GeneratorDriverRunResult Result, CSharpCompilation Compilation) RunGeneratorWithSchema(string consumerSource, string schemaXml, string? rootNamespace = "Acme")
    {
        var additionalText = new InMemoryAdditionalText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "FIX44-mini.xml"),
            schemaXml);

        var optionsProvider = new InMemoryAnalyzerConfigOptionsProvider(rootNamespace);

        var generator = new global::FixSourceGenerator.FixSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { generator.AsSourceGenerator() },
            additionalTexts: new AdditionalText[] { additionalText },
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            optionsProvider: optionsProvider);

        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = tpa
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        var consumerTree = CSharpSyntaxTree.ParseText(consumerSource, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "FixViewTestAssembly",
            new[] { consumerTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        driver = driver.RunGenerators(compilation);
        return (driver.GetRunResult(), compilation);
    }

    private static (GeneratorDriverRunResult Result, CSharpCompilation Compilation) RunGeneratorWithSchemas(
        string consumerSource, (string FileName, string Xml)[] schemas, string? rootNamespace = "Acme")
    {
        var additionalTexts = schemas
            .Select(s => (AdditionalText)new InMemoryAdditionalText(
                Path.Combine(AppContext.BaseDirectory, "TestData", s.FileName), s.Xml))
            .ToArray();

        var optionsProvider = new InMemoryAnalyzerConfigOptionsProvider(rootNamespace);

        var generator = new global::FixSourceGenerator.FixSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { generator.AsSourceGenerator() },
            additionalTexts: additionalTexts,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            optionsProvider: optionsProvider);

        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var references = tpa
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();

        var consumerTree = CSharpSyntaxTree.ParseText(consumerSource, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "FixViewTestAssembly",
            new[] { consumerTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        driver = driver.RunGenerators(compilation);
        return (driver.GetRunResult(), compilation);
    }

    private static Assembly CompileGeneratedAssembly(string consumerSource, string schemaXml)
    {
        var (result, compilation) = RunGeneratorWithSchema(consumerSource, schemaXml);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));
        using var stream = new MemoryStream();
        var emitted = finalCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    private static Assembly CompileGeneratedAssembly(string consumerSource, (string FileName, string Xml)[] schemas)
    {
        var (result, compilation) = RunGeneratorWithSchemas(consumerSource, schemas);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));
        using var stream = new MemoryStream();
        var emitted = finalCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        return Assembly.Load(stream.ToArray());
    }

    private const string ConsumerSource = @"
using FixSourceGenerator.Attributes;

namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct OrderRoutingView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }
        public partial decimal? Price { get; }
    }

    public static class OrderRoutingViewTestHarness
    {
        // A ref struct can't cross a reflection call boundary (can't be boxed into object[]),
        // so the test harness lives inside the compiled consumer assembly itself and only
        // returns plain, reflectable types.
        public static (byte[] ClOrdId, decimal? Price) Read(byte[] buffer)
        {
            var view = new OrderRoutingView(buffer);
            return (view.ClOrdID.ToArray(), view.Price);
        }
    }
}
";

    [Fact]
    public void Matches_properties_by_name_and_emits_early_exit_constructor()
    {
        var (result, _) = RunGenerator(ConsumerSource);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var combinedSource = string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
        Assert.Contains("OrderRoutingView", combinedSource);
        Assert.Contains("int remaining = 2;", combinedSource);
    }

    [Fact]
    public void Generated_view_reads_requested_fields_from_a_real_buffer()
    {
        var (result, compilation) = RunGenerator(ConsumerSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));

        using var ms = new MemoryStream();
        var emitResult = finalCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var harnessType = assembly.GetType("Acme.Views.OrderRoutingViewTestHarness")!;

        byte[] buffer = TestSupport.Fix("11=ABC123", "55=IBM", "54=1", "38=100", "40=2", "44=99.5");

        var readMethod = harnessType.GetMethod("Read")!;
        var tuple = readMethod.Invoke(null, new object[] { buffer })!;
        var tupleType = tuple.GetType();
        var clOrdId = (byte[])tupleType.GetField("Item1")!.GetValue(tuple)!;
        var price = (decimal?)tupleType.GetField("Item2")!.GetValue(tuple);

        Assert.Equal("ABC123", System.Text.Encoding.ASCII.GetString(clOrdId));
        Assert.Equal(99.5m, price);
    }

    [Fact]
    public void Reports_FIX010_when_message_name_not_found()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""DoesNotExist"")]
    public readonly ref partial struct BadView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX010");
    }

    [Fact]
    public void Reports_FIX011_when_struct_not_partial_ref_struct()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public partial struct NotARefStruct
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX011");
    }

    [Fact]
    public void Reports_FIX012_with_suggestion_for_misspelled_property_name()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct TypoView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdId { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        var diag = Assert.Single(result.Diagnostics, d => d.Id == "FIX012");
        Assert.Contains("ClOrdID", diag.GetMessage());
    }

    [Fact]
    public void Reports_FIX013_when_FixField_override_not_found()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct OverrideView
    {
        [FixField(""NoSuchField"")]
        public partial global::System.ReadOnlySpan<byte> Whatever { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX013");
    }

    [Fact]
    public void Reports_FIX014_for_incompatible_type()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct BadTypeView
    {
        public partial int ClOrdID { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX014");
    }

    [Fact]
    public void Reports_FIX015_when_two_properties_target_same_field()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct DuplicateView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }

        [FixField(""ClOrdID"")]
        public partial global::System.ReadOnlySpan<byte> ClOrdIDAlias { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX015");
    }

    private const string GroupConsumerSource = @"
using FixSourceGenerator.Attributes;

namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct OrderWithPartiesView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }
        public partial Acme.Fix.V44.NoPartyIDsGroupReader NoPartyIDs { get; }
    }

    public static class OrderWithPartiesViewTestHarness
    {
        public static (byte[] ClOrdId, int PartyCount, byte[] FirstPartyId) Read(byte[] buffer)
        {
            var view = new OrderWithPartiesView(buffer);
            int count = 0;
            byte[] firstPartyId = System.Array.Empty<byte>();
            foreach (var party in view.NoPartyIDs)
            {
                if (count == 0)
                {
                    firstPartyId = party.PartyID.ToArray();
                }

                count++;
            }

            return (view.ClOrdID.ToArray(), count, firstPartyId);
        }
    }
}
";

    [Fact]
    public void Matches_group_property_by_name_and_exposes_group_reader_type()
    {
        var (result, _) = RunGenerator(GroupConsumerSource);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var combinedSource = string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
        Assert.Contains("NoPartyIDsGroupReader", combinedSource);

        // Both the scalar and the bounded group must be located in their owning scope.
        Assert.Contains("int remaining = 2;", combinedSource);
        Assert.Contains("_NoPartyIDsLength", combinedSource);
    }

    [Fact]
    public void Generated_view_reads_group_entries_from_a_real_buffer()
    {
        var (result, compilation) = RunGenerator(GroupConsumerSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));

        using var ms = new MemoryStream();
        var emitResult = finalCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var harnessType = assembly.GetType("Acme.Views.OrderWithPartiesViewTestHarness")!;

        byte[] buffer = TestSupport.Fix(
            "11=ABC123", "55=IBM", "54=1", "38=100", "40=2", "44=99.5",
            "453=2", "448=PARTY-1", "447=1", "452=1", "448=PARTY-2", "447=1", "452=3");

        var readMethod = harnessType.GetMethod("Read")!;
        var tuple = readMethod.Invoke(null, new object[] { buffer })!;
        var tupleType = tuple.GetType();
        var clOrdId = (byte[])tupleType.GetField("Item1")!.GetValue(tuple)!;
        var partyCount = (int)tupleType.GetField("Item2")!.GetValue(tuple)!;
        var firstPartyId = (byte[])tupleType.GetField("Item3")!.GetValue(tuple)!;

        Assert.Equal("ABC123", System.Text.Encoding.ASCII.GetString(clOrdId));
        Assert.Equal(2, partyCount);
        Assert.Equal("PARTY-1", System.Text.Encoding.ASCII.GetString(firstPartyId));
    }

    [Fact]
    public void Reports_FIX014_for_group_property_with_wrong_type()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct BadGroupTypeView
    {
        public partial int NoPartyIDs { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX014");
    }

    [Fact]
    public void Reports_FIX015_when_two_properties_target_same_group()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct DuplicateGroupView
    {
        public partial Acme.Fix.V44.NoPartyIDsGroupReader NoPartyIDs { get; }

        [FixField(""NoPartyIDs"")]
        public partial Acme.Fix.V44.NoPartyIDsGroupReader NoPartyIDsAlias { get; }
    }
}
";
        var (result, _) = RunGenerator(source);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX015");
    }

    // --- issue #32: selective entry/component projections (component-scoped and group-entry-scoped [FixView]) ---

    private const string ComponentScopedConsumerSource = @"
using FixSourceGenerator.Attributes;

namespace Acme.Views
{
    // Targets the ""Instrument"" *component* directly, instead of the message that embeds it —
    // a selective projection over just Symbol, without the full InstrumentReader's SecurityID slot.
    [FixView(""Instrument"")]
    public readonly ref partial struct SymbolOnlyView
    {
        public partial global::System.ReadOnlySpan<byte> Symbol { get; }
    }

    public static class SymbolOnlyViewTestHarness
    {
        public static byte[] Read(byte[] buffer) => new SymbolOnlyView(buffer).Symbol.ToArray();
    }
}
";

    [Fact]
    public void Component_scoped_FixView_matches_and_emits_early_exit_constructor()
    {
        var (result, _) = RunGenerator(ComponentScopedConsumerSource);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var combinedSource = string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
        Assert.Contains("SymbolOnlyView", combinedSource);
        Assert.Contains("int remaining = 1;", combinedSource);
    }

    [Fact]
    public void Component_scoped_FixView_reads_the_requested_field_from_a_component_sub_span()
    {
        var (result, compilation) = RunGenerator(ComponentScopedConsumerSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));

        using var ms = new MemoryStream();
        var emitResult = finalCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var harnessType = assembly.GetType("Acme.Views.SymbolOnlyViewTestHarness")!;

        // The component's own sub-span: only Instrument's fields (Symbol/SecurityID), same as
        // what the full InstrumentReader would be constructed over.
        byte[] buffer = TestSupport.Fix("55=IBM", "48=SEC-1");

        var readMethod = harnessType.GetMethod("Read")!;
        var symbol = (byte[])readMethod.Invoke(null, new object[] { buffer })!;

        Assert.Equal("IBM", System.Text.Encoding.ASCII.GetString(symbol));
    }

    // A group name reused by two different messages with a *different* entry shape (this
    // happens in real dictionaries, e.g. FIX50SP2's MDFullGrp/MDIncGrp both wrapping a group
    // literally named "NoMDEntries" with different member fields). Resolving [FixView("NoMDEntries")]
    // by picking "the first one found" would silently generate a projection against the wrong
    // occurrence's field set for one of the two messages — instead this must be rejected (FIX016).
    private const string AmbiguousGroupSchema = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<fix type=""FIX"" major=""4"" minor=""4"" servicepack=""0"">
  <header><field name=""MsgType"" required=""Y""/></header>
  <trailer><field name=""CheckSum"" required=""Y""/></trailer>
  <messages>
    <message name=""FirstMessage"" msgtype=""1"" msgcat=""app"">
      <group name=""NoEntries"" required=""N"">
        <field name=""EntryID"" required=""Y""/>
      </group>
    </message>
    <message name=""SecondMessage"" msgtype=""2"" msgcat=""app"">
      <group name=""NoEntries"" required=""N"">
        <field name=""EntryID"" required=""Y""/>
        <field name=""EntryType"" required=""N""/>
      </group>
    </message>
  </messages>
  <components></components>
  <fields>
    <field number=""35"" name=""MsgType"" type=""STRING""/>
    <field number=""10"" name=""CheckSum"" type=""STRING""/>
    <field number=""73"" name=""NoEntries"" type=""NUMINGROUP""/>
    <field number=""1001"" name=""EntryID"" type=""STRING""/>
    <field number=""1002"" name=""EntryType"" type=""CHAR""/>
  </fields>
</fix>
";

    [Fact]
    public void Reports_FIX016_when_group_scope_name_is_ambiguous_across_messages()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NoEntries"")]
    public readonly ref partial struct AmbiguousEntryView
    {
        public partial global::System.ReadOnlySpan<byte> EntryID { get; }
    }
}
";
        var (result, _) = RunGeneratorWithSchema(source, AmbiguousGroupSchema);
        Assert.Contains(result.Diagnostics, d => d.Id == "FIX016");
    }

    [Fact]
    public void Qualified_group_scopes_resolve_distinct_occurrences()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""FirstMessage.NoEntries"")]
    public readonly ref partial struct FirstEntriesView
    {
        public partial global::System.ReadOnlySpan<byte> EntryID { get; }
    }
    [FixView(""SecondMessage.NoEntries"")]
    public readonly ref partial struct SecondEntriesView
    {
        public partial char? EntryType { get; }
    }
}
";
        var (result, compilation) = RunGeneratorWithSchema(source, AmbiguousGroupSchema);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));
        using var stream = new MemoryStream();
        var emitted = finalCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }

    private const string NestedScopeSchema = @"<fix type=""FIX"" major=""4"" minor=""4"" servicepack=""0"">
  <header><field name=""MsgType"" required=""Y""/></header>
  <trailer><field name=""CheckSum"" required=""Y""/></trailer>
  <messages>
    <message name=""Envelope"" msgtype=""U1"" msgcat=""app"">
      <group name=""NoOuter"" required=""N"">
        <field name=""OuterID"" required=""Y""/>
        <field name=""SharedValue"" required=""N""/>
        <group name=""NoInner"" required=""N"">
          <field name=""InnerID"" required=""Y""/>
          <field name=""SharedValue"" required=""N""/>
        </group>
        <field name=""AfterValue"" required=""N""/>
      </group>
    </message>
  </messages>
  <components/>
  <fields>
    <field number=""35"" name=""MsgType"" type=""STRING""/>
    <field number=""10"" name=""CheckSum"" type=""STRING""/>
    <field number=""1000"" name=""NoOuter"" type=""NUMINGROUP""/>
    <field number=""1001"" name=""OuterID"" type=""STRING""/>
    <field number=""1002"" name=""SharedValue"" type=""INT""/>
    <field number=""2000"" name=""NoInner"" type=""NUMINGROUP""/>
    <field number=""2001"" name=""InnerID"" type=""STRING""/>
    <field number=""1003"" name=""AfterValue"" type=""INT""/>
  </fields>
</fix>";

    [Fact]
    public void Entry_projection_does_not_read_a_nested_value_as_an_absent_parent_value()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NoOuter"")]
    public readonly ref partial struct OuterValueView
    {
        public partial int? SharedValue { get; }
    }
    public static class OuterValueHarness
    {
        public static int? Read(byte[] entry) => new OuterValueView(entry).SharedValue;
    }
}
";
        var (result, compilation) = RunGeneratorWithSchema(source, NestedScopeSchema);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));
        using var stream = new MemoryStream();
        var emitted = finalCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());
        var read = assembly.GetType("Acme.Views.OuterValueHarness")!.GetMethod("Read")!;
        byte[] entry = TestSupport.Fix("1001=OUTER", "2000=1", "2001=INNER", "1002=7");
        Assert.Null(read.Invoke(null, new object[] { entry }));
        byte[] present = TestSupport.Fix("1001=OUTER", "1002=3", "2000=1", "2001=INNER", "1002=7");
        Assert.Equal(3, read.Invoke(null, new object[] { present }));
    }

    [Fact]
    public void Entry_projection_honors_nested_group_boundaries_counts_orders_and_duplicates()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct OuterValueView
    {
        public partial global::System.ReadOnlySpan<byte> OuterID { get; }
        public partial int? AfterValue { get; }
        public partial Acme.Fix.V44.NoInnerGroupReader NoInner { get; }
    }
    public static class BoundaryHarness
    {
        public static int? Read(byte[] entry) => new OuterValueView(entry).AfterValue;
        public static int InnerCount(byte[] entry) => new OuterValueView(entry).NoInner.Count;
    }
}
";
        var assembly = CompileGeneratedAssembly(source, NestedScopeSchema);
        var harness = assembly.GetType("Acme.Views.BoundaryHarness")!;
        var read = harness.GetMethod("Read")!;
        var count = harness.GetMethod("InnerCount")!;

        Assert.Equal(9, read.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=1", "2001=INNER", "1002=7", "1003=9") }));
        Assert.Equal(9, read.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=0", "1003=9") }));
        Assert.Equal(5, read.Invoke(null, new object[] { TestSupport.Fix("1003=5", "2000=1", "2001=INNER", "1003=9") }));
        Assert.Equal(9, read.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=1", "2001=INNER", "9999=X", "1003=9") }));
        Assert.Null(read.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=2", "2001=INNER", "1003=9") }));
        Assert.Null(read.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=1", "1003=7") }));
        Assert.Null(read.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=1", "2001=A", "2001=B", "1003=9") }));
        Assert.Equal(1, count.Invoke(null, new object[] { TestSupport.Fix("1001=OUTER", "2000=1", "2001=INNER", "1003=9") }));
    }

    [Fact]
    public void Required_fields_in_optional_components_are_contextually_optional_and_spans_expose_presence()
    {
        const string schema = @"<fix type=""FIX"" major=""4"" minor=""4"" servicepack=""0"">
  <header><field name=""MsgType"" required=""Y""/></header>
  <trailer><field name=""CheckSum"" required=""Y""/></trailer>
  <messages>
    <message name=""Envelope"" msgtype=""U1"" msgcat=""app"">
      <group name=""NoEntries"" required=""N"">
        <field name=""EntryID"" required=""Y""/>
        <component name=""OptionalDetails"" required=""N""/>
      </group>
    </message>
  </messages>
  <components>
    <component name=""OptionalDetails"">
      <field name=""Code"" required=""Y""/>
      <field name=""Quantity"" required=""Y""/>
    </component>
  </components>
  <fields>
    <field number=""35"" name=""MsgType"" type=""STRING""/>
    <field number=""10"" name=""CheckSum"" type=""STRING""/>
    <field number=""1000"" name=""NoEntries"" type=""NUMINGROUP""/>
    <field number=""1001"" name=""EntryID"" type=""STRING""/>
    <field number=""1002"" name=""Code"" type=""STRING""/>
    <field number=""1003"" name=""Quantity"" type=""INT""/>
  </fields>
</fix>";
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoEntries"")]
    public readonly ref partial struct EntryView
    {
        public partial global::System.ReadOnlySpan<byte> EntryID { get; }
        public partial global::System.ReadOnlySpan<byte> Code { get; }
        public partial int? Quantity { get; }
    }
    public static class OptionalHarness
    {
        public static bool HasCode(byte[] entry) => new EntryView(entry).TryGetCode(out _);
        public static int CodeLength(byte[] entry)
        {
            var view = new EntryView(entry);
            return view.TryGetCode(out var code) ? code.Length : -1;
        }
        public static int? Quantity(byte[] entry) => new EntryView(entry).Quantity;
    }
}
";
        var assembly = CompileGeneratedAssembly(source, schema);
        var harness = assembly.GetType("Acme.Views.OptionalHarness")!;
        Assert.False((bool)harness.GetMethod("HasCode")!.Invoke(null, new object[] { TestSupport.Fix("1001=A") })!);
        Assert.Equal(-1, harness.GetMethod("CodeLength")!.Invoke(null, new object[] { TestSupport.Fix("1001=A") }));
        Assert.True((bool)harness.GetMethod("HasCode")!.Invoke(null, new object[] { TestSupport.Fix("1001=A", "1002=") })!);
        Assert.Equal(0, harness.GetMethod("CodeLength")!.Invoke(null, new object[] { TestSupport.Fix("1001=A", "1002=") }));
        Assert.Null(harness.GetMethod("Quantity")!.Invoke(null, new object[] { TestSupport.Fix("1001=A") }));
        Assert.Equal(0, harness.GetMethod("Quantity")!.Invoke(null, new object[] { TestSupport.Fix("1001=A", "1003=0") }));
    }

    [Fact]
    public void Group_projection_does_not_find_a_counter_in_an_unrelated_child_scope()
    {
        string schema = NestedScopeSchema.Replace(
            "<field name=\"SharedValue\" required=\"N\"/>\n        <group",
            "<field name=\"SharedValue\" required=\"N\"/>\n" +
            "<group name=\"NoBefore\" required=\"N\"><field name=\"BeforeID\" required=\"Y\"/>" +
            "<group name=\"NoInner\" required=\"N\"><field name=\"InnerID\" required=\"Y\"/>" +
            "<field name=\"SharedValue\" required=\"N\"/></group></group>\n        <group")
            .Replace("<fields>", "<fields><field number=\"3000\" name=\"NoBefore\" type=\"NUMINGROUP\"/>" +
                "<field number=\"3001\" name=\"BeforeID\" type=\"STRING\"/>");
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct GroupView
    {
        public partial Acme.Fix.V44.NoInnerGroupReader NoInner { get; }
    }
    public static class GroupHarness
    {
        public static int Count(byte[] entry) => new GroupView(entry).NoInner.Count;
    }
}
";
        var assembly = CompileGeneratedAssembly(source, schema);
        var count = assembly.GetType("Acme.Views.GroupHarness")!.GetMethod("Count")!;
        Assert.Equal(0, count.Invoke(null, new object[] {
            TestSupport.Fix("1001=OUTER", "3000=1", "3001=BEFORE", "2000=1", "2001=INNER") }));
        Assert.Equal(0, count.Invoke(null, new object[] {
            TestSupport.Fix("1001=OUTER", "3000=1", "3001=BEFORE", "2000=1", "2001=INNER", "2000=0") }));
        Assert.Equal(2, count.Invoke(null, new object[] {
            TestSupport.Fix("1001=OUTER", "3000=1", "3001=BEFORE", "2000=1", "2001=INNER",
                "2000=2", "2001=OWN-A", "2001=OWN-B") }));
    }

    [Fact]
    public void CurrentSpan_preserves_nested_entries_reusing_the_parent_delimiter()
    {
        string schema = NestedScopeSchema.Replace(
            "<field name=\"InnerID\" required=\"Y\"/>", "<field name=\"OuterID\" required=\"Y\"/>");
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct AfterView
    {
        public partial int? AfterValue { get; }
    }
    public static class AfterHarness
    {
        public static int? Read(byte[] buffer)
        {
            var entries = new Acme.Fix.V44.NoOuterGroupReader(buffer).GetEnumerator();
            if (!entries.MoveNext()) throw new System.InvalidOperationException();
            return new AfterView(entries.CurrentSpan).AfterValue;
        }
        public static string[] Spans(byte[] buffer)
        {
            var result = new System.Collections.Generic.List<string>();
            var entries = new Acme.Fix.V44.NoOuterGroupReader(buffer).GetEnumerator();
            while (entries.MoveNext())
                result.Add(System.Text.Encoding.ASCII.GetString(entries.CurrentSpan));
            return result.ToArray();
        }
        public static long Allocated(byte[] buffer)
        {
            for (int i = 0; i < 32; i++) Read(buffer);
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            int sum = 0;
            for (int i = 0; i < 1000; i++) sum += Read(buffer) ?? 0;
            return sum == 9000 ? System.GC.GetAllocatedBytesForCurrentThread() - before : -1;
        }
    }
}
";
        var assembly = CompileGeneratedAssembly(source, schema);
        var read = assembly.GetType("Acme.Views.AfterHarness")!.GetMethod("Read")!;
        Assert.Equal(9, read.Invoke(null, new object[] {
            TestSupport.Fix("1000=1", "1001=OUTER", "2000=1", "1001=INNER", "1003=9") }));
        var spans = (string[])assembly.GetType("Acme.Views.AfterHarness")!.GetMethod("Spans")!
            .Invoke(null, new object[] { TestSupport.Fix("1000=2", "1001=A", "2000=1", "1001=CHILD-A",
                "1001=B", "2000=1", "1001=CHILD-B") })!;
        Assert.Equal(new[] { "1001=A\u00012000=1\u00011001=CHILD-A\u0001",
            "1001=B\u00012000=1\u00011001=CHILD-B\u0001" }, spans);
        Assert.Equal(0L, assembly.GetType("Acme.Views.AfterHarness")!.GetMethod("Allocated")!
            .Invoke(null, new object[] {
                TestSupport.Fix("1000=1", "1001=OUTER", "2000=1", "1001=INNER", "1003=9") }));
    }

    [Fact]
    public void CurrentSpan_handles_a_nested_counter_as_the_first_entry_field()
    {
        string schema = NestedScopeSchema.Replace("\n        <field name=\"OuterID\" required=\"Y\"/>\n", "\n")
            .Replace("\n        <field name=\"SharedValue\" required=\"N\"/>\n", "\n");
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct AfterView
    {
        public partial int? AfterValue { get; }
    }
    public static class CounterHarness
    {
        public static int Sum(byte[] buffer)
        {
            int sum = 0;
            var entries = new Acme.Fix.V44.NoOuterGroupReader(buffer).GetEnumerator();
            while (entries.MoveNext()) sum += new AfterView(entries.CurrentSpan).AfterValue ?? 0;
            return sum;
        }
    }
}
";
        var assembly = CompileGeneratedAssembly(source, schema);
        Assert.Equal(12, assembly.GetType("Acme.Views.CounterHarness")!.GetMethod("Sum")!
            .Invoke(null, new object[] {
                TestSupport.Fix("1000=2", "2000=1", "2001=A", "1003=3", "2000=1", "2001=B", "1003=9") }));
    }

    [Fact]
    public void Sparse_and_dense_views_emit_state_only_for_requested_fields()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct SparseView
    {
        public partial global::System.ReadOnlySpan<byte> OuterID { get; }
    }
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct DenseView
    {
        public partial global::System.ReadOnlySpan<byte> OuterID { get; }
        public partial int? SharedValue { get; }
        public partial int? AfterValue { get; }
    }
}
";
        var assembly = CompileGeneratedAssembly(source, NestedScopeSchema);
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.Equal(3, assembly.GetType("Acme.Views.SparseView")!.GetFields(fields).Length);
        Assert.Equal(9, assembly.GetType("Acme.Views.DenseView")!.GetFields(fields).Length);
    }

    [Fact]
    public void Helper_container_does_not_collide_with_a_FIX_message_name()
    {
        const string source = """
            using FixSourceGenerator.Attributes;
            namespace Acme.Views
            {
                [FixView("FixViewGroupSkipHelpers.NoOuter")]
                public readonly ref partial struct Projection
                {
                    public partial int? AfterValue { get; }
                }
                public static class Harness
                {
                    public static int? Read(byte[] buffer) => new Projection(buffer).AfterValue;
                }
            }
            """;
        var assembly = CompileGeneratedAssembly(
            source, NestedScopeSchema.Replace("Envelope", "FixViewGroupSkipHelpers"));
        Assert.Equal(9, assembly.GetType("Acme.Views.Harness")!.GetMethod("Read")!.Invoke(null,
            new object[] { TestSupport.Fix("1001=OUTER", "2000=1", "2001=INNER", "1003=9") }));
        Assert.NotNull(assembly.GetType("Acme.Fix.V44.Runtime.FixViewGroupSkipHelpers"));
    }

    [Fact]
    public void Group_helper_container_is_shared_across_multiple_views_of_the_same_topology()
    {
        // Both views target "Envelope.NoOuter", which reaches the same NoInner group topology
        // (issue #32 follow-up): the nested-group skip helper must be emitted once for this
        // schema and referenced by both views, not duplicated per struct.
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct FirstView
    {
        public partial global::System.ReadOnlySpan<byte> OuterID { get; }
    }
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct SecondView
    {
        public partial int? AfterValue { get; }
    }
    public static class SharedHelperHarness
    {
        public static byte[] FirstOuterId(byte[] buffer) => new FirstView(buffer).OuterID.ToArray();
        public static int? SecondAfterValue(byte[] buffer) => new SecondView(buffer).AfterValue;
    }
}
";
        var (result, compilation) = RunGeneratorWithSchema(source, NestedScopeSchema);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var generated = result.Results.SelectMany(r => r.GeneratedSources).ToArray();

        // Exactly one shared container for this schema, however many views target its topology.
        var helperFiles = generated.Where(s => s.HintName.Contains("FixViewGroupSkipHelpers")).ToArray();
        Assert.Single(helperFiles);

        // Neither view's own generated file declares a TrySkip method anymore — both call into
        // the shared container instead.
        foreach (var viewFile in generated.Where(s => s.HintName.Contains("FirstView") || s.HintName.Contains("SecondView")))
        {
            Assert.DoesNotContain("bool TrySkip", viewFile.SourceText.ToString());
            Assert.Contains("FixViewGroupSkipHelpers.TrySkip", viewFile.SourceText.ToString());
        }

        // The shared container declares the NoInner skip helper exactly once (not once per view).
        string helperSource = helperFiles[0].SourceText.ToString();
        int declarationCount = helperSource.Split(new[] { "internal static bool TrySkip" }, StringSplitOptions.None).Length - 1;
        Assert.Equal(1, declarationCount);

        var finalCompilation = compilation.AddSyntaxTrees(generated.Select(s => s.SyntaxTree));
        using var stream = new MemoryStream();
        var emitted = finalCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());

        byte[] buffer = TestSupport.Fix("1001=OUTER", "2000=1", "2001=INNER", "1003=9");
        var harness = assembly.GetType("Acme.Views.SharedHelperHarness")!;
        var outerId = (byte[])harness.GetMethod("FirstOuterId")!.Invoke(null, new object[] { buffer })!;
        Assert.Equal("OUTER", System.Text.Encoding.ASCII.GetString(outerId));
        Assert.Equal(9, harness.GetMethod("SecondAfterValue")!.Invoke(null, new object[] { buffer }));
    }

    // Same group short names ("NoOuter"/"NoInner") as NestedScopeSchema, under a differently
    // named message and a different FIX minor version — deliberately reusing the same field
    // numbers so a registry keyed only by short name (instead of schema + topology) would
    // silently cross-wire the two schemas' skip helpers.
    private const string NestedScopeSchemaOtherVersion = @"<fix type=""FIX"" major=""4"" minor=""3"" servicepack=""0"">
  <header><field name=""MsgType"" required=""Y""/></header>
  <trailer><field name=""CheckSum"" required=""Y""/></trailer>
  <messages>
    <message name=""EnvelopeAlt"" msgtype=""U2"" msgcat=""app"">
      <group name=""NoOuter"" required=""N"">
        <field name=""OuterID"" required=""Y""/>
        <group name=""NoInner"" required=""N"">
          <field name=""InnerID"" required=""Y""/>
        </group>
        <field name=""AfterValue"" required=""N""/>
      </group>
    </message>
  </messages>
  <components/>
  <fields>
    <field number=""35"" name=""MsgType"" type=""STRING""/>
    <field number=""10"" name=""CheckSum"" type=""STRING""/>
    <field number=""1000"" name=""NoOuter"" type=""NUMINGROUP""/>
    <field number=""1001"" name=""OuterID"" type=""STRING""/>
    <field number=""2000"" name=""NoInner"" type=""NUMINGROUP""/>
    <field number=""2001"" name=""InnerID"" type=""STRING""/>
    <field number=""1003"" name=""AfterValue"" type=""INT""/>
  </fields>
</fix>";

    [Fact]
    public void Group_helper_containers_stay_isolated_across_different_schemas()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""Envelope.NoOuter"")]
    public readonly ref partial struct MainView
    {
        public partial global::System.ReadOnlySpan<byte> OuterID { get; }
        public partial int? AfterValue { get; }
    }
    [FixView(""EnvelopeAlt.NoOuter"")]
    public readonly ref partial struct AltView
    {
        public partial global::System.ReadOnlySpan<byte> OuterID { get; }
        public partial int? AfterValue { get; }
    }
    public static class MultiSchemaHarness
    {
        public static (byte[] OuterId, int? AfterValue) ReadMain(byte[] buffer)
        {
            var v = new MainView(buffer);
            return (v.OuterID.ToArray(), v.AfterValue);
        }
        public static (byte[] OuterId, int? AfterValue) ReadAlt(byte[] buffer)
        {
            var v = new AltView(buffer);
            return (v.OuterID.ToArray(), v.AfterValue);
        }
    }
}
";
        var (result, compilation) = RunGeneratorWithSchemas(source, new[]
        {
            ("FIX44-nested.xml", NestedScopeSchema),
            ("FIX43-nested.xml", NestedScopeSchemaOtherVersion),
        });
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var generated = result.Results.SelectMany(r => r.GeneratedSources).ToArray();
        var helperFiles = generated.Where(s => s.HintName.Contains("FixViewGroupSkipHelpers")).ToArray();
        // One shared container PER schema — never a single one shared across schemas.
        Assert.Equal(2, helperFiles.Length);
        Assert.Contains(helperFiles, f => f.HintName.StartsWith("Acme.Fix.V44."));
        Assert.Contains(helperFiles, f => f.HintName.StartsWith("Acme.Fix.V43."));

        var finalCompilation = compilation.AddSyntaxTrees(generated.Select(s => s.SyntaxTree));
        using var stream = new MemoryStream();
        var emitted = finalCompilation.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        var assembly = Assembly.Load(stream.ToArray());

        var harness = assembly.GetType("Acme.Views.MultiSchemaHarness")!;
        byte[] mainBuffer = TestSupport.Fix("1001=MAIN", "2000=1", "2001=INNER", "1002=7", "1003=9");
        var main = ((byte[] OuterId, int? AfterValue))harness.GetMethod("ReadMain")!.Invoke(null, new object[] { mainBuffer })!;
        Assert.Equal("MAIN", System.Text.Encoding.ASCII.GetString(main.OuterId));
        Assert.Equal(9, main.AfterValue);

        byte[] altBuffer = TestSupport.Fix("1001=ALT", "2000=1", "2001=INNER", "1003=5");
        var alt = ((byte[] OuterId, int? AfterValue))harness.GetMethod("ReadAlt")!.Invoke(null, new object[] { altBuffer })!;
        Assert.Equal("ALT", System.Text.Encoding.ASCII.GetString(alt.OuterId));
        Assert.Equal(5, alt.AfterValue);
    }

    [Fact]
    public void Duplicate_after_early_exit_does_not_replace_the_first_occurrence()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct IdView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }
    }
    public static class IdHarness
    {
        public static byte[] Read(byte[] buffer) => new IdView(buffer).ClOrdID.ToArray();
    }
}
";
        var assembly = CompileGeneratedAssembly(source, LoadTestData("FIX44-mini.xml"));
        var read = assembly.GetType("Acme.Views.IdHarness")!.GetMethod("Read")!;
        var value = (byte[])read.Invoke(null, new object[] { TestSupport.Fix("11=FIRST", "11=SECOND") })!;
        Assert.Equal("FIRST", System.Text.Encoding.ASCII.GetString(value));
    }

    private const string GroupEntryScopedConsumerSource = @"
using FixSourceGenerator.Attributes;

namespace Acme.Views
{
    // Targets the ""NoPartyIDs"" *group*'s own entry scope directly (issue #32) — a selective
    // projection reading only PartyID out of each entry, fed the entry's raw span via
    // {Group}GroupReader.Enumerator.CurrentSpan instead of the full {Group}EntryReader.
    [FixView(""NoPartyIDs"")]
    public readonly ref partial struct PartyIdOnlyEntryView
    {
        public partial global::System.ReadOnlySpan<byte> PartyID { get; }
    }

    public static class PartyIdOnlyEntryViewTestHarness
    {
        public static byte[][] ReadAllPartyIds(byte[] buffer)
        {
            var group = new Acme.Fix.V44.NoPartyIDsGroupReader(buffer);
            var results = new System.Collections.Generic.List<byte[]>();
            var e = group.GetEnumerator();
            while (e.MoveNext())
            {
                var view = new PartyIdOnlyEntryView(e.CurrentSpan);
                results.Add(view.PartyID.ToArray());
            }

            return results.ToArray();
        }
    }
}
";

    [Fact]
    public void Group_entry_scoped_FixView_matches_and_emits_early_exit_constructor()
    {
        var (result, _) = RunGenerator(GroupEntryScopedConsumerSource);

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var combinedSource = string.Join("\n", result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SourceText.ToString()));
        Assert.Contains("PartyIdOnlyEntryView", combinedSource);
        Assert.Contains("int remaining = 1;", combinedSource);
    }

    [Fact]
    public void Group_entry_scoped_FixView_reads_PartyID_from_each_entrys_CurrentSpan()
    {
        var (result, compilation) = RunGenerator(GroupEntryScopedConsumerSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));

        using var ms = new MemoryStream();
        var emitResult = finalCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var harnessType = assembly.GetType("Acme.Views.PartyIdOnlyEntryViewTestHarness")!;

        byte[] buffer = TestSupport.Fix(
            "11=ABC123", "55=IBM", "54=1", "38=100", "40=2", "44=99.5",
            "453=2", "448=PARTY-1", "447=1", "452=1", "448=PARTY-2", "447=1", "452=3");

        var readMethod = harnessType.GetMethod("ReadAllPartyIds")!;
        var partyIds = (byte[][])readMethod.Invoke(null, new object[] { buffer })!;

        Assert.Equal(2, partyIds.Length);
        Assert.Equal("PARTY-1", System.Text.Encoding.ASCII.GetString(partyIds[0]));
        Assert.Equal("PARTY-2", System.Text.Encoding.ASCII.GetString(partyIds[1]));
    }

    [Fact]
    public void Retained_entry_view_remains_valid_after_enumerator_moves_next()
    {
        const string source = @"
using FixSourceGenerator.Attributes;
namespace Acme.Views
{
    [FixView(""NewOrderSingle.NoPartyIDs"")]
    public readonly ref partial struct PartyView
    {
        public partial global::System.ReadOnlySpan<byte> PartyID { get; }
    }
    public static class RetainedHarness
    {
        public static byte[] ReadFirstAfterMoveNext(byte[] buffer)
        {
            var e = new Acme.Fix.V44.NoPartyIDsGroupReader(buffer).GetEnumerator();
            if (!e.MoveNext()) return global::System.Array.Empty<byte>();
            var first = new PartyView(e.CurrentSpan);
            if (!e.MoveNext()) return global::System.Array.Empty<byte>();
            return first.PartyID.ToArray();
        }
    }
}
";
        var assembly = CompileGeneratedAssembly(source, LoadTestData("FIX44-mini.xml"));
        byte[] buffer = TestSupport.Fix(
            "453=2", "448=PARTY-1", "447=1", "452=1", "448=PARTY-2", "447=1", "452=3");
        var method = assembly.GetType("Acme.Views.RetainedHarness")!.GetMethod("ReadFirstAfterMoveNext")!;
        var first = (byte[])method.Invoke(null, new object[] { buffer })!;
        Assert.Equal("PARTY-1", System.Text.Encoding.ASCII.GetString(first));
    }

    private const string DuplicateTagConsumerSource = @"
using FixSourceGenerator.Attributes;

namespace Acme.Views
{
    // 3 requested fields so the early-exit scan doesn't reach the end of the buffer regardless —
    // proves the duplicate ClOrdID doesn't disturb locating OrderQty/OrdType afterwards.
    [FixView(""NewOrderSingle"")]
    public readonly ref partial struct FirstOccurrenceView
    {
        public partial global::System.ReadOnlySpan<byte> ClOrdID { get; }
        public partial decimal OrderQty { get; }

        // OrdType (40) has <value> children in the schema, so it's enum-eligible: the accepted
        // escape-hatch type is the CHAR category's underlying wire representation (byte), not
        // char itself (see FixViewTypeCompatibility).
        public partial byte OrdType { get; }
    }

    public static class FirstOccurrenceViewTestHarness
    {
        public static (byte[] ClOrdId, decimal OrderQty, byte OrdType) Read(byte[] buffer)
        {
            var view = new FirstOccurrenceView(buffer);
            return (view.ClOrdID.ToArray(), view.OrderQty, view.OrdType);
        }
    }
}
";

    [Fact]
    public void Duplicate_tag_before_early_exit_boundary_does_not_prevent_locating_later_fields()
    {
        var (result, compilation) = RunGenerator(DuplicateTagConsumerSource);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var finalCompilation = compilation.AddSyntaxTrees(
            result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree));

        using var ms = new MemoryStream();
        var emitResult = finalCompilation.Emit(ms);
        Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        ms.Seek(0, SeekOrigin.Begin);
        var assembly = Assembly.Load(ms.ToArray());
        var harnessType = assembly.GetType("Acme.Views.FirstOccurrenceViewTestHarness")!;

        // ClOrdID (11) appears twice before OrderQty(38)/OrdType(40). A naive "decrement remaining
        // on every match" scan would count the duplicate as a second distinct field found and
        // early-exit right after it — before OrderQty/OrdType are ever seen — leaving them at
        // their zero-initialized defaults instead of the real values below.
        byte[] buffer = TestSupport.Fix("11=FIRST", "11=SECOND", "38=250", "40=1", "55=IBM");

        var readMethod = harnessType.GetMethod("Read")!;
        var tuple = readMethod.Invoke(null, new object[] { buffer })!;
        var tupleType = tuple.GetType();
        var clOrdId = (byte[])tupleType.GetField("Item1")!.GetValue(tuple)!;
        var orderQty = (decimal)tupleType.GetField("Item2")!.GetValue(tuple)!;
        var ordType = (byte)tupleType.GetField("Item3")!.GetValue(tuple)!;

        // First occurrence wins: "FIRST", not "SECOND".
        Assert.Equal("FIRST", System.Text.Encoding.ASCII.GetString(clOrdId));
        Assert.Equal(250m, orderQty);
        Assert.Equal((byte)'1', ordType);
    }

    [Fact]
    public void Usage_guide_FixView_declaration_compiles_and_reads_enums_and_groups()
    {
        string guide = LoadTestData("USAGE.md");
        int marker = guide.IndexOf("<!-- fixview-routing-declaration -->", StringComparison.Ordinal);
        Assert.True(marker >= 0);
        int start = guide.IndexOf("```csharp", marker, StringComparison.Ordinal) + "```csharp".Length;
        int end = guide.IndexOf("```", start, StringComparison.Ordinal);
        var assembly = CompileGeneratedAssembly(guide.Substring(start, end - start) + """

            public static class GuideHarness
            {
                public static string Read(byte[] buffer)
                {
                    var view = new OrderRoutingView(buffer);
                    var parties = view.NoPartyIDs.GetEnumerator();
                    return $"{(byte)view.Side}:{view.Price}:{view.NoPartyIDs.Count}:" +
                        (parties.MoveNext() ? System.Text.Encoding.ASCII.GetString(parties.Current.PartyID) : "");
                }
            }
            """, LoadTestData("FIX44-mini.xml"));
        var value = assembly.GetType("GuideHarness")!.GetMethod("Read")!.Invoke(null,
            new object[] { TestSupport.Fix("11=ORDER", "54=1", "44=12", "453=1", "448=PARTY", "447=D", "452=1") });
        Assert.Equal("49:12:1:PARTY", value);
    }

    [Theory]
    [InlineData("using Acme.Fix.V44;", "Side", "NoPartyIDsGroupReader")]
    [InlineData("", "Acme.Fix.V44.Side", "Acme.Fix.V44.NoPartyIDsGroupReader")]
    [InlineData("", "global::Acme.Fix.V44.Side", "global::Acme.Fix.V44.NoPartyIDsGroupReader")]
    [InlineData("using Direction = Acme.Fix.V44.Side; using Parties = Acme.Fix.V44.NoPartyIDsGroupReader;", "Direction", "Parties")]
    [InlineData("using Fix = Acme.Fix.V44;", "Fix.Side", "Fix.NoPartyIDsGroupReader")]
    [InlineData("global using Acme.Fix.V44;", "Side", "NoPartyIDsGroupReader")]
    public void Consumer_type_names_are_resolved_and_emitted_without_consumer_imports(string imports, string side, string group)
    {
        string source = $$"""
            {{imports}}
            using FixSourceGenerator.Attributes;
            namespace Consumer;
            [FixView("NewOrderSingle")]
            public ref partial struct View
            {
                public partial {{side}} Side { get; }
                public partial {{group}} NoPartyIDs { get; }
                public partial System.Nullable<System.Decimal> Price { get; }
            }
            public static class Harness
            {
                public static int Read(byte[] buffer)
                {
                    var view = new View(buffer);
                    return (byte)view.Side + view.NoPartyIDs.Count;
                }
            }
            """;
        var assembly = CompileGeneratedAssembly(source, LoadTestData("FIX44-mini.xml"));
        Assert.Equal(50, assembly.GetType("Consumer.Harness")!.GetMethod("Read")!.Invoke(null,
            new object[] { TestSupport.Fix("54=1", "453=1", "448=PARTY") }));
    }

    [Theory]
    [InlineData("using Wrong;", "Side", "NoPartyIDsGroupReader")]
    [InlineData("using Acme.Fix.V44;", "Wrong.Side", "Wrong.NoPartyIDsGroupReader")]
    [InlineData("using Direction = Wrong.Side; using Parties = Wrong.NoPartyIDsGroupReader;", "Direction", "Parties")]
    public void Same_named_consumer_types_from_wrong_namespace_are_rejected(string imports, string side, string group)
    {
        var (result, _) = RunGenerator($$"""
            {{imports}}
            using FixSourceGenerator.Attributes;
            namespace Wrong { public enum Side { Buy } public ref struct NoPartyIDsGroupReader { } }
            namespace Consumer
            {
                [FixView("NewOrderSingle")]
                public ref partial struct View
                {
                    public partial {{side}} Side { get; }
                    public partial {{group}} NoPartyIDs { get; }
                }
            }
            """);
        var diagnostics = result.Diagnostics.Where(d => d.Id == "FIX014").ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("global::Acme.Fix.V44.Side"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("global::Acme.Fix.V44.NoPartyIDsGroupReader"));
    }

    [Theory]
    [InlineData("NewOrderSingle")]
    [InlineData("NewOrderSingle.NoPartyIDs")]
    [InlineData("Instrument")]
    [InlineData("NoPartyIDs")]
    public void Ambiguous_cross_version_targets_report_same_FIX016_regardless_of_schema_order(string target)
    {
        string v44 = LoadTestData("FIX44-mini.xml");
        string v42 = v44.Replace("minor=\"4\"", "minor=\"2\"").Replace("number=\"44\"", "number=\"144\"");
        string source = $$"""
            using FixSourceGenerator.Attributes;
            [FixView("{{target}}")]
            public ref partial struct View { }
            """;
        var schemas = new[] { ("FIX44.xml", v44), ("FIX42.xml", v42) };
        var first = Assert.Single(RunGeneratorWithSchemas(source, schemas).Result.Diagnostics, d => d.Id == "FIX016");
        var reversed = Assert.Single(RunGeneratorWithSchemas(source, schemas.Reverse().ToArray()).Result.Diagnostics, d => d.Id == "FIX016");
        Assert.Equal(first.GetMessage(), reversed.GetMessage());
        Assert.Contains("Acme.Fix.V42:", first.GetMessage());
        Assert.Contains("Acme.Fix.V44:", first.GetMessage());
    }

    [Fact]
    public void Qualified_path_unique_to_one_version_is_not_rejected()
    {
        string v44 = LoadTestData("FIX44-mini.xml");
        string v42 = v44.Replace("minor=\"4\"", "minor=\"2\"").Replace("NoPartyIDs", "NoOtherParties");
        CompileGeneratedAssembly("""
            using FixSourceGenerator.Attributes;
            [FixView("NewOrderSingle.NoPartyIDs")]
            public ref partial struct View
            {
                public partial System.ReadOnlySpan<byte> PartyID { get; }
            }
            """, new[] { ("FIX44.xml", v44), ("FIX42.xml", v42) });
    }

    [Fact]
    public void Optional_generated_enum_preserves_nullable_behavior_with_relative_namespace_import()
    {
        string schema = LoadTestData("FIX44-mini.xml").Replace(
            "<field name=\"Side\" required=\"Y\"/>", "<field name=\"Side\" required=\"N\"/>");
        var assembly = CompileGeneratedAssembly("""
            using FixSourceGenerator.Attributes;
            namespace Acme.Consumer
            {
                using Fix.V44;
                [FixView("NewOrderSingle")]
                public ref partial struct View
                {
                    public partial Side? Side { get; }
                }
                public static class Harness
                {
                    public static int Read(byte[] buffer)
                    {
                        var view = new View(buffer);
                        return view.Side.HasValue ? (byte)view.Side.Value : -1;
                    }
                }
            }
            """, schema);
        var read = assembly.GetType("Acme.Consumer.Harness")!.GetMethod("Read")!;
        Assert.Equal(-1, read.Invoke(null, new object[] { TestSupport.Fix("11=ORDER") }));
        Assert.Equal(49, read.Invoke(null, new object[] { TestSupport.Fix("54=1") }));
    }

    [Fact]
    public void Fully_qualified_enum_and_group_respect_custom_root_namespace()
    {
        const string source = """
            using FixSourceGenerator.Attributes;
            [FixView("NewOrderSingle")]
            public ref partial struct View
            {
                public partial global::Custom.Trading.Fix.V44.Side Side { get; }
                public partial global::Custom.Trading.Fix.V44.NoPartyIDsGroupReader NoPartyIDs { get; }
            }
            """;
        var (result, compilation) = RunGenerator(source, "Custom.Trading");
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        using var stream = new MemoryStream();
        var emitted = compilation.AddSyntaxTrees(result.Results.SelectMany(r => r.GeneratedSources).Select(s => s.SyntaxTree)).Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
    }

    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string content)
        {
            Path = path;
            _text = SourceText.From(content);
        }

        public override string Path { get; }

        public override SourceText GetText(System.Threading.CancellationToken cancellationToken = default) => _text;
    }

    private sealed class InMemoryAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
    {
        private readonly InMemoryOptions _globalOptions;

        public InMemoryAnalyzerConfigOptionsProvider(string? rootNamespace)
        {
            _globalOptions = new InMemoryOptions(rootNamespace);
        }

        public override AnalyzerConfigOptions GlobalOptions => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _globalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _globalOptions;

        private sealed class InMemoryOptions : AnalyzerConfigOptions
        {
            private readonly string? _rootNamespace;

            public InMemoryOptions(string? rootNamespace)
            {
                _rootNamespace = rootNamespace;
            }

            public override bool TryGetValue(string key, out string value)
            {
                if (key == "build_property.RootNamespace" && _rootNamespace != null)
                {
                    value = _rootNamespace;
                    return true;
                }

                value = string.Empty;
                return false;
            }
        }
    }
}
