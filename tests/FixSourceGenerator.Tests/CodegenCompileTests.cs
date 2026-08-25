using System.Linq;
using Microsoft.CodeAnalysis;

namespace FixSourceGenerator.Tests;

public class CodegenCompileTests
{
    [Fact]
    public void GeneratedCode_Compiles_WithZeroErrors()
    {
        var dictionary = TestSupport.BuildSampleDictionary();
        var files = TestSupport.Generate(dictionary, out _);

        Assert.Contains(files, f => f.hintName.EndsWith("Runtime.FixRuntime.g.cs"));
        Assert.Contains(files, f => f.hintName.EndsWith("NewOrderSingle.g.cs"));
        Assert.Contains(files, f => f.hintName.EndsWith("Enums.g.cs"));
        Assert.Contains(files, f => f.hintName.EndsWith("Components.g.cs"));

        var compilation = TestSupport.Compile(files.Select(f => f.content));
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())) +
            "\n\n--- SOURCES ---\n" + string.Join("\n\n", files.Select(f => "// " + f.hintName + "\n" + f.content)));
    }
}
