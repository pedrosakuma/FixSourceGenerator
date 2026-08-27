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
        var additionalText = new InMemoryAdditionalText(
            Path.Combine(AppContext.BaseDirectory, "TestData", "FIX44-mini.xml"),
            LoadTestData("FIX44-mini.xml"));

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

        // The group must NOT participate in the scanning constructor's early-exit switch/count —
        // only ClOrdID (1 field) should be tracked by `remaining`.
        Assert.Contains("int remaining = 1;", combinedSource);
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
