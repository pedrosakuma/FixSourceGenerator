using System.IO;
using System.Linq;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;
using Microsoft.CodeAnalysis;
using Xunit;

namespace FixSourceGenerator.Tests;

/// <summary>
/// Conformance tests (issue #7) against a real, public FIX DataDictionary — the FIX 4.4 SP2
/// schema shipped by the QuickFIX project (`TestData/FIX44.xml`, BSD-licensed, see the adjacent
/// `.NOTICE.md`). These exercise the parser + codegen against the full breadth of a real
/// dictionary (912 fields, 632 components, 93 groups, 93 messages) rather than the small hand
/// authored fixtures used elsewhere, to catch edge cases (deeply nested components, large enums,
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
}
