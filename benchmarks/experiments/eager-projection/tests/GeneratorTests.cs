using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Xml.Linq;
using EagerProjection.Experiment;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace EagerProjection.Tests;

public sealed class GeneratorTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
    private static string Dictionary => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "NativeDto.xml"));
    private const string Declaration = """
        using System;
        using EagerProjection.Experiment;
        namespace Consumer;
        [ExperimentalEager("NativeDto.xml", "NativeEnvelope", "NoEntries", "NativeDto.Fix.V42.Runtime")]
        public readonly ref partial struct Values
        {
            public partial decimal? Size { get; }
            [ExperimentalField("Price")] public partial decimal? Quote { get; }
            public partial ReadOnlySpan<byte> EntryID { get; }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_dictionary_and_declaration_generate_compilable_executable_native_values(bool renamed)
    {
        string xml = Dictionary, source = Declaration;
        int count = 100, delimiter = 101, price = 270, size = 271;
        if (renamed)
        {
            xml = xml.Replace("number=\"100\"", "number=\"1900\"").Replace("number=\"101\"", "number=\"1901\"")
                .Replace("number=\"270\"", "number=\"1902\"").Replace("number=\"271\"", "number=\"1903\"")
                .Replace("name=\"Price\"", "name=\"DifferentPrice\"");
            source = source.Replace("ExperimentalField(\"Price\")", "ExperimentalField(\"DifferentPrice\")");
            count = 1900; delimiter = 1901; price = 1902; size = 1903;
        }
        var document = XDocument.Parse(xml);
        var group = document.Descendants("group").Single();
        var priceMember = group.Elements("field").ElementAt(1);
        priceMember.Remove();
        group.Add(priceMember);
        group.Add(new XElement("field", new XAttribute("name", "Unused"), new XAttribute("required", "N")));
        document.Root!.Element("fields")!.Add(new XElement("field", new XAttribute("name", "Unused"),
            new XAttribute("number", "8001"), new XAttribute("type", "STRING")));
        source += $$"""

            public static class Runner
            {
                public static string Run()
                {
                    byte[] input = System.Text.Encoding.ASCII.GetBytes(
                        "35=UND|{{count}}=2|{{delimiter}}=A|8001=ignored|{{price}}=bad|{{price}}=99|{{size}}=0|{{delimiter}}=B|{{price}}=2.5|10=000|".Replace('|', '\x01'));
                    string result = "";
                    var iterator = Values.Enumerate(input);
                    while (iterator.MoveNext())
                    {
                        var row = iterator.Current;
                        if (!row.HasEntryID || !input.AsSpan().Overlaps(row.EntryID))
                            throw new Exception("Text is not borrowed");
                        result += System.Text.Encoding.ASCII.GetString(row.EntryID) + ":" +
                            row.HasQuote + ":" + (row.Quote?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") +
                            ":" + row.HasSize + ":" + (row.Size?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null") + ";";
                    }
                    if (iterator.Current.HasEntryID) throw new Exception("Stale current");
                    return result;
                }
            }
            """;
        var (result, output) = Generate(source, document.ToString(), production: true);
        Assert.Empty(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        string generated = result.Results.Single(r => r.Generator is EagerProjectionGenerator).GeneratedSources
            .Single(s => s.HintName.EndsWith(".Eager.g.cs")).SourceText.ToString();
        Assert.Contains($"case {price}:", generated);
        Assert.Contains("private readonly decimal?", generated);
        Assert.DoesNotContain("valueStart", generated);
        using var stream = new MemoryStream();
        var emitted = output.Emit(stream);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
        stream.Position = 0;
        var context = new AssemblyLoadContext("eager-test", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            Assert.Equal("A:True:null:True:0;B:True:2.5:False:null;",
                assembly.GetType("Consumer.Runner")!.GetMethod("Run")!.Invoke(null, null));
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("component")]
    [InlineData("root-field")]
    [InlineData("sibling")]
    [InlineData("overlap")]
    [InlineData("duplicate-tag")]
    [InlineData("duplicate-name")]
    [InlineData("required-number")]
    [InlineData("optional-delimiter")]
    [InlineData("data")]
    [InlineData("unresolved")]
    [InlineData("bad-xml")]
    [InlineData("unknown-message")]
    [InlineData("unknown-group")]
    [InlineData("unknown-selection")]
    [InlineData("wrong-type")]
    [InlineData("mutable")]
    [InlineData("nonpartial")]
    [InlineData("collision")]
    [InlineData("dictionary-missing")]
    [InlineData("duplicate-dictionary")]
    [InlineData("record")]
    [InlineData("invalid-attribute")]
    public void Unsupported_inputs_report_error_instead_of_emitting_a_scanner(string kind)
    {
        string xml = Dictionary, declaration = Declaration;
        var document = XDocument.Parse(xml);
        var group = document.Descendants("group").Single();
        switch (kind)
        {
            case "nested":
                group.Add(new XElement("group", new XAttribute("name", "NoEntries"),
                    new XAttribute("required", "N"), new XElement(group.Elements().First()))); break;
            case "component": group.Add(new XElement("component", new XAttribute("name", "Unknown"))); break;
            case "root-field": group.Parent!.Add(new XElement(group.Elements().First())); break;
            case "sibling": group.Parent!.Add(new XElement(group)); break;
            case "overlap": document.Root!.Element("header")!.Add(new XElement(group.Elements().First())); break;
            case "duplicate-tag":
                document.Root!.Element("fields")!.Elements().Last().SetAttributeValue("number", "270"); break;
            case "duplicate-name":
                document.Root!.Element("fields")!.Elements().Last().SetAttributeValue("name", "Price"); break;
            case "required-number": group.Elements().ElementAt(1).SetAttributeValue("required", "Y"); break;
            case "optional-delimiter": group.Elements().First().SetAttributeValue("required", "N"); break;
            case "data": document.Root!.Element("fields")!.Elements().Last().SetAttributeValue("type", "DATA"); break;
            case "unresolved": group.Elements().Last().SetAttributeValue("name", "Unknown"); break;
            case "unknown-message": declaration = declaration.Replace("\"NativeEnvelope\"", "\"Other\""); break;
            case "unknown-group": declaration = declaration.Replace("\"NoEntries\"", "\"Other\""); break;
            case "unknown-selection": declaration = declaration.Replace("ExperimentalField(\"Price\")", "ExperimentalField(\"Other\")"); break;
            case "wrong-type": declaration = declaration.Replace("decimal? Quote", "int? Quote"); break;
            case "mutable": declaration = declaration.Replace("readonly ref", "ref"); break;
            case "nonpartial": declaration = declaration.Replace("ref partial struct", "ref struct"); break;
            case "collision": declaration = declaration.Replace("decimal? Quote", "decimal? Enumerate"); break;
            case "dictionary-missing": declaration = declaration.Replace("\"NativeDto.xml\"", "\"Missing.xml\""); break;
            case "record": declaration = declaration.Replace("readonly ref partial struct", "readonly partial record struct"); break;
            case "invalid-attribute":
                declaration = declaration.Replace("\"NativeDto.xml\", \"NativeEnvelope\", \"NoEntries\", \"NativeDto.Fix.V42.Runtime\"", ""); break;
        }
        var (result, _) = Generate(declaration, kind == "bad-xml" ? "<fix" : document.ToString(),
            duplicateDictionary: kind == "duplicate-dictionary");
        Assert.Contains(result.Diagnostics, d => d.Id == "EAGER001" && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(result.Diagnostics, d => d.Id == "CS8785");
        Assert.DoesNotContain(result.Results.SelectMany(r => r.GeneratedSources), s => s.HintName.EndsWith(".Eager.g.cs"));
    }

    private static (GeneratorDriverRunResult Result, Compilation Output) Generate(string source, string xml,
        bool production = false, bool duplicateDictionary = false)
    {
        var compilation = CSharpCompilation.Create("EagerProbe" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(source, ParseOptions)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        ISourceGenerator[] generators = production
            ? [new EagerProjectionGenerator(), new global::FixSourceGenerator.FixSourceGenerator().AsSourceGenerator()]
            : [new EagerProjectionGenerator()];
        AdditionalText[] files = duplicateDictionary
            ? [new DictionaryText("NativeDto.xml", xml), new DictionaryText("other/NativeDto.xml", xml)]
            : [new DictionaryText("NativeDto.xml", xml)];
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generators, files, ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (driver.GetRunResult(), output);
    }

    private sealed class DictionaryText(string path, string text) : AdditionalText
    {
        public override string Path => path;
        public override SourceText GetText(CancellationToken cancellationToken = default) => SourceText.From(text, Encoding.UTF8);
    }
}
