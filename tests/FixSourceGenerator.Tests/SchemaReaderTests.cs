using System.IO;
using FixSourceGenerator.Schema;
using Microsoft.CodeAnalysis;
using Xunit;

namespace FixSourceGenerator.Tests
{
    public class SchemaReaderTests
    {
        private static string LoadTestData(string fileName) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

        private static (FixDictionary? Dictionary, List<Diagnostic> Diagnostics) ParseFixture(string fileName)
        {
            var diagnostics = new List<Diagnostic>();
            var dictionary = SchemaReader.Parse(LoadTestData(fileName), fileName, diagnostics.Add);
            return (dictionary, diagnostics);
        }

        [Fact]
        public void Parses_version_token_and_counts()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX44-mini.xml");

            Assert.NotNull(dictionary);
            Assert.Empty(diagnostics);
            Assert.Equal("V44", dictionary!.VersionToken);
            Assert.Equal(4, dictionary.Major);
            Assert.Equal(4, dictionary.Minor);
            Assert.Equal(0, dictionary.ServicePack);
            Assert.Equal(7, dictionary.Header.Count);
            Assert.Single(dictionary.Trailer);
            Assert.Single(dictionary.Messages);
        }

        [Fact]
        public void Resolves_component_reference_inside_message()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX44-mini.xml");

            Assert.Empty(diagnostics);
            var message = dictionary!.Messages[0];
            Assert.Equal("NewOrderSingle", message.Name);
            Assert.Equal("D", message.MsgType);

            var componentRef = Assert.IsType<FixComponentRef>(message.Entries[1]);
            Assert.Equal("Instrument", componentRef.Component.Name);
            Assert.Equal(2, componentRef.Component.Entries.Count);
            Assert.True(componentRef.Required);
        }

        [Fact]
        public void Resolves_group_with_counter_field_and_nested_entries()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX44-mini.xml");

            Assert.Empty(diagnostics);
            var message = dictionary!.Messages[0];
            var groupRef = Assert.IsType<FixGroupRef>(message.Entries[^1]);

            Assert.Equal("NoPartyIDs", groupRef.Name);
            Assert.Equal("NUMINGROUP", groupRef.CounterField.Type);
            Assert.Equal(453, groupRef.CounterField.Number);
            Assert.Equal(3, groupRef.Entries.Count);
            Assert.False(groupRef.Required);
        }

        [Fact]
        public void Resolves_field_enum_values()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX44-mini.xml");

            Assert.Empty(diagnostics);
            var ordType = dictionary!.FieldsByName["OrdType"];
            Assert.True(ordType.HasValues);
            Assert.Equal(2, ordType.Values.Count);
            Assert.Equal("MARKET", ordType.Values[0].Description);
        }

        [Fact]
        public void Reports_circular_component_reference()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX-circular-component.xml");

            Assert.NotNull(dictionary);
            Assert.Contains(diagnostics, d => d.Id == "FIX008");
        }

        [Fact]
        public void Reports_unresolved_field_reference()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX-unresolved-reference.xml");

            Assert.NotNull(dictionary);
            Assert.Contains(diagnostics, d => d.Id == "FIX005");
        }

        [Fact]
        public void Reports_duplicate_field_definition()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX-duplicate-field.xml");

            Assert.NotNull(dictionary);
            Assert.Contains(diagnostics, d => d.Id == "FIX004");
        }

        [Fact]
        public void Reports_missing_group_counter_field()
        {
            var (dictionary, diagnostics) = ParseFixture("FIX-missing-group-counter.xml");

            Assert.NotNull(dictionary);
            Assert.Contains(diagnostics, d => d.Id == "FIX007");
        }

        [Fact]
        public void Returns_null_for_malformed_xml()
        {
            var diagnostics = new List<Diagnostic>();
            var dictionary = SchemaReader.Parse("<not-fix></not-fix>", "bad.xml", diagnostics.Add);

            Assert.Null(dictionary);
            Assert.Contains(diagnostics, d => d.Id == "FIX002");
        }
    }
}
