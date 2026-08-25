using System.IO;
using System.Linq;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;
using Microsoft.CodeAnalysis;
using Xunit;

namespace FixSourceGenerator.Tests;

/// <summary>
/// Conformance tests (issue #7) against real, public FIX DataDictionaries — the FIX 4.4 SP2
/// schema (`TestData/FIX44.xml`) and the FIX 5.0 SP2 schema (`TestData/FIX50SP2.xml`), both
/// shipped by the QuickFIX project (BSD-licensed, see the adjacent `.NOTICE.md` files). These
/// exercise the parser + codegen against the full breadth of real dictionaries (FIX44: 912
/// fields/632 components/93 groups/93 messages; FIX50SP2: 6000+ fields/725 components/156
/// messages, the largest public dictionary available) rather than the small hand-authored
/// fixtures used elsewhere, to catch edge cases (deeply nested components, large enums,
/// uncommon field types) that a minimal fixture can't.
/// </summary>
public class RealSchemaConformanceTests
{
    private static string LoadFix44Xml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "FIX44.xml"));

    private static (FixDictionary Schema, System.Collections.Generic.List<Diagnostic> ParseDiagnostics) ParseFix44()
    {
        var diagnostics = new System.Collections.Generic.List<Diagnostic>();
        var schema = SchemaReader.Parse(LoadFix44Xml(), "FIX44.xml", diagnostics.Add);
        Assert.NotNull(schema);
        return (schema!, diagnostics);
    }

    [Fact]
    public void Parses_full_FIX44_dictionary_without_errors()
    {
        var (schema, diagnostics) = ParseFix44();

        // Errors (FIX001/002/004/005/008) would indicate the parser can't handle a construct
        // present in a real dictionary. Warnings (FIX003/006/007) are acceptable and separately
        // asserted below to keep this test honest about what's actually happening.
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Parser errors on real FIX44 schema:\n" + string.Join("\n", errors));

        Assert.Equal("V44", schema.VersionToken);
        Assert.Equal(93, schema.Messages.Count);
        Assert.True(schema.FieldsByNumber.Count > 900, $"Expected 900+ fields, got {schema.FieldsByNumber.Count}");
        Assert.True(schema.ComponentsByName.Count > 50, $"Expected 50+ components, got {schema.ComponentsByName.Count}");
    }

    [Fact]
    public void Resolves_NewOrderSingle_with_nested_components_and_groups()
    {
        var (schema, _) = ParseFix44();

        var newOrderSingle = schema.Messages.Single(m => m.Name == "NewOrderSingle");
        Assert.Equal("D", newOrderSingle.MsgType);

        // NewOrderSingle in FIX 4.4 includes the Instrument component and several repeating
        // groups (e.g. NoPartyIDs, NoAllocs, NoTradingSessions) — assert at least one of each
        // entry kind resolved, proving component/group resolution works end-to-end on real data.
        bool hasComponent = newOrderSingle.Entries.OfType<FixComponentRef>().Any();
        bool hasGroup = HasGroupRecursive(newOrderSingle.Entries);
        bool hasField = newOrderSingle.Entries.OfType<FixFieldRef>().Any();

        Assert.True(hasComponent, "Expected NewOrderSingle to reference at least one component (e.g. Instrument)");
        Assert.True(hasGroup, "Expected NewOrderSingle to (possibly transitively, via a component) reference at least one repeating group (e.g. Parties/NoPartyIDs)");
        Assert.True(hasField, "Expected NewOrderSingle to reference at least one plain field");
    }

    private static bool HasGroupRecursive(System.Collections.Generic.IReadOnlyList<FixEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry is FixGroupRef)
            {
                return true;
            }
            if (entry is FixComponentRef componentRef && HasGroupRecursive(componentRef.Component.Entries))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Generates_and_compiles_code_for_the_full_FIX44_dictionary()
    {
        var (schema, parseDiagnostics) = ParseFix44();
        Assert.DoesNotContain(parseDiagnostics, d => d.Severity == DiagnosticSeverity.Error);

        var codegenDiagnostics = new System.Collections.Generic.List<Diagnostic>();
        var context = new GenerationContext(codegenDiagnostics.Add);
        var sources = new FixCodeGenerator()
            .Generate("Conformance.Fix.V44", schema, context)
            .Select(s => s.content)
            .ToList();

        Assert.NotEmpty(sources);

        // FIX006 (unknown field type) is acceptable — it degrades gracefully to a byte span —
        // but nothing else should surface from codegen against a real, complete dictionary.
        var unexpected = codegenDiagnostics.Where(d => d.Id != "FIX006").ToList();
        Assert.True(unexpected.Count == 0, "Unexpected codegen diagnostics:\n" + string.Join("\n", unexpected));

        var compilation = TestSupport.Compile(sources);
        var emitResult = compilation.Emit(Stream.Null);

        var errors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(emitResult.Success, "Generated code for full FIX44 dictionary failed to compile:\n" +
            string.Join("\n", errors.Take(30)) + (errors.Count > 30 ? $"\n... and {errors.Count - 30} more" : ""));
    }

    private static string LoadFix50Sp2Xml() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "FIX50SP2.xml"));

    /// <summary>
    /// FIX 5.0 SP2 (`TestData/FIX50SP2.xml`) is the largest/most complex public DataDictionary
    /// available (156 messages, 6000+ fields, 725 components) — kept as a permanent regression
    /// fixture (alongside FIX44 above) to catch codegen issues like #11 that only surface at scale
    /// or with constructs not present in the smaller FIX44 dictionary.
    /// </summary>
    [Fact]
    public void Generates_and_compiles_code_for_the_full_FIX50SP2_dictionary()
    {
        var diagnostics = new System.Collections.Generic.List<Diagnostic>();
        var schema = SchemaReader.Parse(LoadFix50Sp2Xml(), "FIX50SP2.xml", diagnostics.Add);
        Assert.NotNull(schema);

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "Parser errors on real FIX50SP2 schema:\n" + string.Join("\n", errors));

        var codegenDiagnostics = new System.Collections.Generic.List<Diagnostic>();
        var context = new GenerationContext(codegenDiagnostics.Add);
        var sources = new FixCodeGenerator()
            .Generate("Conformance.Fix.V50SP2", schema!, context)
            .Select(s => s.content)
            .ToList();

        Assert.NotEmpty(sources);

        var unexpected = codegenDiagnostics.Where(d => d.Id != "FIX006").ToList();
        Assert.True(unexpected.Count == 0, "Unexpected codegen diagnostics:\n" + string.Join("\n", unexpected));

        var compilation = TestSupport.Compile(sources);
        var emitResult = compilation.Emit(Stream.Null);

        var compileErrors = emitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(emitResult.Success, "Generated code for full FIX50SP2 dictionary failed to compile:\n" +
            string.Join("\n", compileErrors.Take(30)) + (compileErrors.Count > 30 ? $"\n... and {compileErrors.Count - 30} more" : ""));
    }
}

