using System.Linq;
using FixSourceGenerator;

namespace FixSourceGenerator.Tests;

public class StringExtensionsTests
{
    [Theory]
    [InlineData("EXECUTING_FIRM", "ExecutingFirm")] // all-caps with underscores
    [InlineData("NewOrderSingle", "NewOrderSingle")] // already PascalCase preserved
    [InlineData("BUY", "Buy")] // single all-caps token
    [InlineData("Good Till Cancel", "GoodTillCancel")] // spaces
    [InlineData("PARTIALLY_FILLED", "PartiallyFilled")]
    [InlineData("", "")]
    public void ToPascalCase_Normalizes(string input, string expected)
    {
        Assert.Equal(expected, input.ToPascalCase());
    }

    [Theory]
    [InlineData("1stRound", "_1StRound")] // leading digit gets underscore; first letter is capitalized
    [InlineData("9WEST", "_9West")]
    [InlineData("EXECUTING_FIRM", "ExecutingFirm")]
    [InlineData("", "_")] // empty falls back to underscore
    public void ToIdentifier_ProducesLegalIdentifiers(string input, string expected)
    {
        Assert.Equal(expected, input.ToIdentifier());
    }
}

public class EnumGenerationTests
{
    [Fact]
    public void GeneratesEnum_WithNormalizedMembers_AndCharLiterals()
    {
        var files = TestSupport.Generate(TestSupport.BuildSampleDictionary(), out _);
        string enums = files.Single(f => f.hintName.EndsWith("Enums.g.cs")).content;

        Assert.Contains("public enum Side : int", enums);
        Assert.Contains("Buy = '1',", enums);
        Assert.Contains("Sell = '2',", enums);
    }

    [Fact]
    public void EnumMembers_HandleAllCaps_LeadingDigit_AndUnderscores()
    {
        var dictionary = TestSupport.BuildEnumEdgeCaseDictionary();
        var files = TestSupport.Generate(dictionary, out _);
        string enums = files.Single(f => f.hintName.EndsWith("Enums.g.cs")).content;

        Assert.Contains("New = '0',", enums);
        Assert.Contains("PartiallyFilled = '1',", enums);
        Assert.Contains("_9West = '2',", enums);

        // The generated enums must still compile.
        var compilation = TestSupport.Compile(files.Select(f => f.content));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }
}
