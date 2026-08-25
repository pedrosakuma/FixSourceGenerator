using System.Linq;
using Microsoft.CodeAnalysis;

namespace FixSourceGenerator.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void UnknownFieldType_ReportsFix006_AndFallsBackToSpan()
    {
        var dictionary = TestSupport.BuildUnknownTypeDictionary();
        var files = TestSupport.Generate(dictionary, out var diagnostics);

        var diag = Assert.Single(diagnostics);
        Assert.Equal("FIX006", diag.Id);
        Assert.Equal(DiagnosticSeverity.Warning, diag.Severity);
        string message = diag.GetMessage();
        Assert.Contains("VendorThing", message);
        Assert.Contains("FOOBAR", message);

        // The unknown-typed field degrades to a raw byte span.
        string source = files.Single(f => f.hintName.EndsWith("VendorMessage.g.cs")).content;
        Assert.Contains("global::System.ReadOnlySpan<byte> VendorThing", source);

        // And the fallback still compiles.
        var compilation = TestSupport.Compile(files.Select(f => f.content));
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
