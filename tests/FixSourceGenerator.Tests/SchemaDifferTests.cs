using System.Collections.Generic;
using System.Linq;
using FixSourceGenerator.Diff;
using FixSourceGenerator.Schema;
using Xunit;

namespace FixSourceGenerator.Tests
{
    public class SchemaDifferTests
    {
        [Fact]
        public void Diff_identical_schema_returns_empty()
        {
            var schema = TestSupport.BuildDiffDictionary();
            var changes = SchemaDiffer.Diff(schema, schema);
            Assert.Empty(changes);
        }

        [Fact]
        public void Diff_reports_message_removed()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(messages: new List<FixMessageDef>());
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.MessageRemoved, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Breaking, change.Severity);
            Assert.Equal("ExecutionReport", change.Path);
        }

        [Fact]
        public void Diff_reports_message_msgtype_changed()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(messages: new[] { TestSupport.BuildExecutionReport(msgType: "Z") });
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.MessageMsgTypeChanged, change.Kind);
            Assert.Equal("8", change.OldValue);
            Assert.Equal("Z", change.NewValue);
        }

        [Fact]
        public void Diff_reports_component_removed()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(components: new Dictionary<string, FixComponentDef>());
            var changes = SchemaDiffer.Diff(oldSchema, newSchema);
            Assert.Contains(changes, change => change.Kind == SchemaChangeKind.ComponentRemoved && change.Path == "Instrument");
        }

        [Fact]
        public void Diff_reports_required_field_removed_from_message()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(messages: new[] { TestSupport.BuildExecutionReport(includeSymbol: false) });
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.FieldRemoved, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Breaking, change.Severity);
            Assert.Equal("ExecutionReport.Symbol", change.Path);
        }

        [Fact]
        public void Diff_reports_required_field_added_to_message()
        {
            var oldSchema = TestSupport.BuildDiffDictionary(messages: new[] { TestSupport.BuildExecutionReport(includeText: false) });
            var newSchema = TestSupport.BuildDiffDictionary();
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.FieldAdded, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Breaking, change.Severity);
            Assert.Equal("ExecutionReport.Text", change.Path);
        }

        [Fact]
        public void Diff_reports_optional_field_added_as_info()
        {
            var oldSchema = TestSupport.BuildDiffDictionary(messages: new[] { TestSupport.BuildExecutionReport(includeOptionalOrderId: false) });
            var newSchema = TestSupport.BuildDiffDictionary();
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.FieldAdded, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Info, change.Severity);
            Assert.Equal("ExecutionReport.OrderID", change.Path);
        }

        [Fact]
        public void Diff_reports_field_type_changed()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(fields: TestSupport.BuildDiffFields(symbolType: "INT"));
            var changes = SchemaDiffer.Diff(oldSchema, newSchema);
            Assert.Contains(changes, change => change.Kind == SchemaChangeKind.FieldTypeChanged && change.Path == "fields.Symbol");
        }

        [Fact]
        public void Diff_reports_group_structure_changes()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(messages: new[] { TestSupport.BuildExecutionReport(includePartyRole: false) });
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.FieldRemoved, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Warning, change.Severity);
            Assert.Equal("ExecutionReport.NoPartyIDs.PartyRole", change.Path);
        }

        [Fact]
        public void Diff_reports_enum_value_removed()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(fields: TestSupport.BuildDiffFields(sideValues: new List<FixValueDef> { new FixValueDef("1", "BUY") }));
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.EnumValueRemoved, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Breaking, change.Severity);
            Assert.Equal("fields.Side.values.2", change.Path);
        }

        [Fact]
        public void Diff_reports_enum_value_added()
        {
            var oldSchema = TestSupport.BuildDiffDictionary();
            var newSchema = TestSupport.BuildDiffDictionary(fields: TestSupport.BuildDiffFields(sideValues: new List<FixValueDef>
            {
                new FixValueDef("1", "BUY"),
                new FixValueDef("2", "SELL"),
                new FixValueDef("5", "SELL_SHORT")
            }));
            var change = Assert.Single(SchemaDiffer.Diff(oldSchema, newSchema));
            Assert.Equal(SchemaChangeKind.EnumValueAdded, change.Kind);
            Assert.Equal(SchemaDiffSeverity.Info, change.Severity);
            Assert.Equal("fields.Side.values.5", change.Path);
        }

        [Fact]
        public void Report_renders_markdown_grouped_by_severity()
        {
            var report = SchemaDiffReport.ToMarkdown(new[]
            {
                new SchemaChange(SchemaChangeKind.MessageRemoved, SchemaDiffSeverity.Breaking, "ExecutionReport", "Message was removed."),
                new SchemaChange(SchemaChangeKind.FieldAdded, SchemaDiffSeverity.Info, "ExecutionReport.OrderID", "Optional field was added.")
            });

            Assert.Contains("## Breaking changes", report);
            Assert.Contains("## Informational changes", report);
            Assert.Contains("**MessageRemoved** `ExecutionReport`", report);
        }
    }
}
