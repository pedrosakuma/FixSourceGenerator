using System.IO;
using System.Linq;
using FixSourceGenerator.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace FixSourceGenerator.Tests
{
    /// <summary>
    /// Covers SchemaReader's XML diagnostics remediation: real <see cref="Location"/> spans mapped
    /// from XML line info onto the AdditionalFile's <see cref="SourceText"/> (instead of
    /// <see cref="Location.None"/>), and FIX005's owner slot naming the actual enclosing
    /// message/component/group/header/trailer rather than the raw XML element tag.
    /// </summary>
    public class SchemaReaderLocationTests
    {
        private static string LoadTestData(string fileName) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));

        private static (FixDictionary? Dictionary, List<Diagnostic> Diagnostics, SourceText SourceText) ParseFixtureWithLocations(string fileName)
        {
            string xmlContent = LoadTestData(fileName);
            var sourceText = SourceText.From(xmlContent);
            var diagnostics = new List<Diagnostic>();
            var dictionary = SchemaReader.Parse(xmlContent, fileName, diagnostics.Add);
            return (dictionary, diagnostics, sourceText);
        }

        /// <summary>
        /// Asserts that <paramref name="location"/> points at the (1-based occurrence)-th
        /// occurrence of <paramref name="token"/> in <paramref name="sourceText"/>, in the file
        /// identified by <paramref name="schemaPath"/>, with a span exactly the width of the token.
        /// </summary>
        private static void AssertLocationAtToken(Location location, SourceText sourceText, string schemaPath, string token, int occurrence = 1)
        {
            Assert.Equal(LocationKind.ExternalFile, location.Kind);

            string text = sourceText.ToString();
            int index = -1;
            for (int i = 0; i < occurrence; i++)
            {
                index = text.IndexOf(token, index + 1, System.StringComparison.Ordinal);
                Assert.True(index >= 0, $"Expected to find occurrence #{occurrence} of '{token}' in the fixture.");
            }

            var expectedStart = sourceText.Lines.GetLinePosition(index);
            var expectedEnd = sourceText.Lines.GetLinePosition(index + token.Length);

            var lineSpan = location.GetLineSpan();
            Assert.Equal(schemaPath, lineSpan.Path);
            Assert.Equal(expectedStart, lineSpan.StartLinePosition);
            Assert.Equal(expectedEnd, lineSpan.EndLinePosition);

            Assert.Equal(index, location.SourceSpan.Start);
            Assert.Equal(index + token.Length, location.SourceSpan.End);
        }

        [Fact]
        public void Duplicate_field_number_location_points_at_the_offending_number_attribute()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-duplicate-field.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX004");

            // "number=\"1\"" appears twice (Account, then AccountDup); the diagnostic is attached
            // to the second (duplicate) field's number attribute.
            AssertLocationAtToken(diag.Location, sourceText, "FIX-duplicate-field.xml", "number=\"1\"", occurrence: 2);
        }

        [Fact]
        public void Invalid_major_attribute_location_points_at_the_major_attribute_value()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-invalid-major.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX009");
            Assert.Equal("Element 'fix' has attribute 'major' with invalid value 'four'; expected an integer", diag.GetMessage());

            AssertLocationAtToken(diag.Location, sourceText, "FIX-invalid-major.xml", "major=\"four\"");
        }

        [Fact]
        public void Circular_component_reference_location_points_at_the_component_name_attribute()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-circular-component.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX008");

            // Component "B" is visited first (dictionary iteration order for this fixture starts
            // with "A", which recurses into "B", which recurses back into "A" — the cycle is
            // detected on the second, in-progress, visit of "A").
            Assert.Contains("'A'", diag.GetMessage());
            AssertLocationAtToken(diag.Location, sourceText, "FIX-circular-component.xml", "name=\"A\"", occurrence: 1);
        }

        [Fact]
        public void Missing_group_counter_field_location_points_at_the_group_name_attribute()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-missing-group-counter.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX007");

            AssertLocationAtToken(diag.Location, sourceText, "FIX-missing-group-counter.xml", "name=\"NoPartyIDs\"");
        }

        [Fact]
        public void Unresolved_field_reference_reports_the_owning_message_name_not_the_element_tag()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-unresolved-reference.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX005");

            // Owner must be the message's *name* ("Heartbeat"), not the raw element tag ("message"),
            // and must not be confused with the undefined field's own name.
            Assert.Equal("message 'Heartbeat' references undefined field 'DoesNotExist'", diag.GetMessage());
            AssertLocationAtToken(diag.Location, sourceText, "FIX-unresolved-reference.xml", "name=\"DoesNotExist\"");
        }

        [Fact]
        public void Unresolved_field_reference_inside_a_component_reports_the_component_as_owner()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-unresolved-field-in-component.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX005");

            Assert.Equal("component 'Instrument' references undefined field 'MissingField'", diag.GetMessage());
            AssertLocationAtToken(diag.Location, sourceText, "FIX-unresolved-field-in-component.xml", "name=\"MissingField\"");
        }

        [Fact]
        public void Unresolved_field_reference_inside_a_group_reports_the_group_as_owner_not_the_containing_message()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-unresolved-field-in-group.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX005");

            // The immediate owner is the <group name="NoPartyIDs">, not the enclosing
            // <message name="NewOrderSingle">.
            Assert.Equal("group 'NoPartyIDs' references undefined field 'MissingField'", diag.GetMessage());
            AssertLocationAtToken(diag.Location, sourceText, "FIX-unresolved-field-in-group.xml", "name=\"MissingField\"");
        }

        [Fact]
        public void Unresolved_component_reference_reports_the_owning_message_and_the_component_kind()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-unresolved-component-reference.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX005");

            Assert.Equal("message 'NewOrderSingle' references undefined component 'MissingComponent'", diag.GetMessage());
            AssertLocationAtToken(diag.Location, sourceText, "FIX-unresolved-component-reference.xml", "name=\"MissingComponent\"");
        }

        [Fact]
        public void Unresolved_field_reference_inside_the_header_reports_header_as_owner()
        {
            var (dictionary, diagnostics, sourceText) = ParseFixtureWithLocations("FIX-unresolved-field-in-header.xml");

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX005");

            Assert.Equal("header 'header' references undefined field 'MissingHeaderField'", diag.GetMessage());
            AssertLocationAtToken(diag.Location, sourceText, "FIX-unresolved-field-in-header.xml", "name=\"MissingHeaderField\"");
        }

        [Fact]
        public void Malformed_nested_xml_reports_FIX002_at_the_xml_exception_location()
        {
            var (dictionary, diagnostics, _) = ParseFixtureWithLocations("FIX-malformed-nested.xml");

            Assert.Null(dictionary);
            var diag = Assert.Single(diagnostics);
            Assert.Equal("FIX002", diag.Id);
            Assert.Equal("FIX-malformed-nested.xml", diag.Location.GetLineSpan().Path);
            var exception = Assert.Throws<System.Xml.XmlException>(() => System.Xml.Linq.XDocument.Parse(LoadTestData("FIX-malformed-nested.xml")));
            Assert.Equal(exception.LineNumber - 1, diag.Location.GetLineSpan().StartLinePosition.Line);
            Assert.Equal(exception.LinePosition - 1, diag.Location.GetLineSpan().StartLinePosition.Character);
            string message = diag.GetMessage();
            Assert.Contains("FIX-malformed-nested.xml", message);
            Assert.Contains("could not be parsed", message);
        }

        [Fact]
        public void Raw_string_tooling_API_also_reports_source_locations()
        {
            var diagnostics = new List<Diagnostic>();
            string xmlContent = LoadTestData("FIX-unresolved-reference.xml");

            var dictionary = SchemaReader.Parse(xmlContent, "FIX-unresolved-reference.xml", diagnostics.Add);

            Assert.NotNull(dictionary);
            var diag = Assert.Single(diagnostics, d => d.Id == "FIX005");
            AssertLocationAtToken(diag.Location, SourceText.From(xmlContent), "FIX-unresolved-reference.xml", "name=\"DoesNotExist\"");
            Assert.Equal("message 'Heartbeat' references undefined field 'DoesNotExist'", diag.GetMessage());
        }

        [Fact]
        public void Attribute_location_preserves_original_quotes_spacing_and_entities()
        {
            const string xml = """
                <fix major="4" minor="4">
                  <messages><message name="Order" msgtype="D">
                    <field name = 'Missing&amp;Field' required="Y"/>
                  </message></messages>
                </fix>
                """;
            var diagnostics = new List<Diagnostic>();
            SchemaReader.Parse(xml, "custom.xml", diagnostics.Add);
            var diagnostic = Assert.Single(diagnostics, d => d.Id == "FIX005");
            Assert.Equal("message 'Order' references undefined field 'Missing&Field'", diagnostic.GetMessage());
            AssertLocationAtToken(diagnostic.Location, SourceText.From(xml), "custom.xml", "name = 'Missing&amp;Field'");
        }
    }
}
