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

        public static readonly DiagnosticDescriptor InvalidAttributeValue = new DiagnosticDescriptor(
            id: "FIX009",
            title: "Invalid attribute value",
            messageFormat: "Element '{0}' has attribute '{1}' with invalid value '{2}'; expected {3}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FixViewMessageNotFound = new DiagnosticDescriptor(
            id: "FIX010",
            title: "FixView target message not found",
            messageFormat: "[FixView(\"{0}\")] on '{1}' does not match any message in the loaded schema(s)",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FixViewStructMustBePartial = new DiagnosticDescriptor(
            id: "FIX011",
            title: "FixView struct must be a partial ref struct",
            messageFormat: "'{0}' is annotated with [FixView] but is not declared as a 'partial ref struct'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FixViewPropertyNameMismatch = new DiagnosticDescriptor(
            id: "FIX012",
            title: "FixView property does not match any field",
            messageFormat: "Property '{0}' on '{1}' does not match any field of message '{2}'.{3}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FixViewFieldOverrideNotFound = new DiagnosticDescriptor(
            id: "FIX013",
            title: "[FixField] references an unknown field",
            messageFormat: "[FixField(\"{0}\")] on property '{1}' does not match any field of message '{2}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FixViewIncompatibleType = new DiagnosticDescriptor(
            id: "FIX014",
            title: "FixView property type incompatible with field type",
            messageFormat: "Property '{0}' has type '{1}', which is not compatible with field '{2}' (FIX type '{3}'). Accepted types: {4}",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor FixViewDuplicateFieldTarget = new DiagnosticDescriptor(
            id: "FIX015",
            title: "Multiple FixView properties target the same field",
            messageFormat: "Property '{0}' targets field '{1}', which is already targeted by property '{2}' on '{3}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
