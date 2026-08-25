using Microsoft.CodeAnalysis;

namespace FixSourceGenerator.Diagnostics
{
    /// <summary>
    /// Central catalog of diagnostics reported by the generator. IDs must stay in sync
    /// with AnalyzerReleases.Shipped.md / AnalyzerReleases.Unshipped.md and docs/CONTRACT.md §8.
    /// </summary>
    public static class FixDiagnostics
    {
        private const string Category = "FixSourceGenerator";

        public static readonly DiagnosticDescriptor MissingRequiredAttribute = new DiagnosticDescriptor(
            id: "FIX001",
            title: "Missing required attribute",
            messageFormat: "Element '{0}' is missing required attribute '{1}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MalformedSchema = new DiagnosticDescriptor(
            id: "FIX002",
            title: "Malformed schema file",
            messageFormat: "Schema '{0}' could not be parsed: {1}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnsupportedConstruct = new DiagnosticDescriptor(
            id: "FIX003",
            title: "Unsupported schema construct",
            messageFormat: "{0}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateDefinition = new DiagnosticDescriptor(
            id: "FIX004",
            title: "Duplicate definition in schema",
            messageFormat: "Duplicate {0} '{1}' defined in schema",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnresolvedReference = new DiagnosticDescriptor(
            id: "FIX005",
            title: "Unresolved reference",
            messageFormat: "{0} '{1}' references undefined {2} '{3}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor UnknownFieldType = new DiagnosticDescriptor(
            id: "FIX006",
            title: "Unknown FIX field type",
            messageFormat: "Field '{0}' has unrecognized type '{1}'; falling back to a raw byte span",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MissingGroupCounterField = new DiagnosticDescriptor(
            id: "FIX007",
            title: "Missing group counter field",
            messageFormat: "Group '{0}' has no corresponding NUMINGROUP field definition named '{0}' in <fields>",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor CircularComponentReference = new DiagnosticDescriptor(
            id: "FIX008",
            title: "Circular component reference",
            messageFormat: "Component '{0}' references itself, directly or indirectly, and cannot be materialized",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
