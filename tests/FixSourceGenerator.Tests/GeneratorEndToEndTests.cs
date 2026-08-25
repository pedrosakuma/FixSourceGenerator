using System.IO;
using System.Linq;
using FixSourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace FixSourceGenerator.Tests;

/// <summary>
/// End-to-end tests that run the real <see cref="FixSourceGenerator"/> IIncrementalGenerator
/// (entry point wiring SchemaReader → FixCodeGenerator) against the on-disk XML fixtures used by
/// <c>SchemaReaderTests</c>, verifying the full pipeline compiles as a Roslyn source generator.
/// </summary>
public class GeneratorEndToEndTests
{
    private static string LoadTestData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

    private static GeneratorDriverRunResult RunGenerator(string xmlFileName, string? rootNamespace = "Acme")
    {
        var additionalText = new InMemoryAdditionalText(
            Path.Combine(AppContext.BaseDirectory, "TestData", xmlFileName),
            LoadTestData(xmlFileName));

        var optionsProvider = new InMemoryAnalyzerConfigOptionsProvider(rootNamespace);

        var generator = new global::FixSourceGenerator.FixSourceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: new ISourceGenerator[] { generator.AsSourceGenerator() },
            additionalTexts: new AdditionalText[] { additionalText },
            optionsProvider: optionsProvider);

        var compilation = CSharpCompilation.Create("EndToEndTestAssembly");
        driver = driver.RunGenerators(compilation);
        return driver.GetRunResult();
    }

    [Fact]
    public void Generates_reader_writer_and_runtime_for_valid_schema()
    {
        var result = RunGenerator("FIX44-mini.xml");

        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));

        var hintNames = result.Results.Single().GeneratedSources.Select(s => s.HintName).ToArray();
        Assert.Contains(hintNames, n => n.Contains("Runtime"));
        Assert.Contains(hintNames, n => n.Contains("NewOrderSingle"));

        var combinedSource = string.Join("\n", result.Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("Acme.Fix.V44", combinedSource);
        Assert.Contains("NewOrderSingleReader", combinedSource);
        Assert.Contains("NewOrderSingleWriter", combinedSource);
    }

    [Fact]
    public void Reports_diagnostics_without_throwing_for_malformed_schema()
    {
        var result = RunGenerator("FIX-unresolved-reference.xml");

        Assert.Contains(result.Diagnostics, d => d.Id == "FIX005");
    }

    [Fact]
    public void Falls_back_to_file_name_namespace_when_no_root_namespace_configured()
    {
        var result = RunGenerator("FIX44-mini.xml", rootNamespace: null);

        var combinedSource = string.Join("\n", result.Results.Single().GeneratedSources.Select(s => s.SourceText.ToString()));
        Assert.Contains("FIX44.Mini.Fix.V44", combinedSource);
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
