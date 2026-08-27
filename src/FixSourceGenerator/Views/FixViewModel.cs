using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace FixSourceGenerator.Views
{
    /// <summary>
    /// One <c>partial</c> property declared on a <c>[FixView]</c>-annotated struct, extracted
    /// straight from syntax (see remarks on why this avoids <see cref="ITypeSymbol"/> resolution).
    /// </summary>
    /// <remarks>
    /// The declared property type is captured as its literal source text (e.g. <c>"Side"</c>,
    /// <c>"decimal?"</c>, <c>"global::System.ReadOnlySpan&lt;byte&gt;"</c>) rather than as a
    /// resolved <see cref="ITypeSymbol"/>. This is deliberate: a property typed as an enum this
    /// same generator is *also* emitting in the same compilation pass (e.g. <c>Side</c>) has no
    /// resolvable metadata symbol yet — the semantic model reports <c>TypeKind.Error</c> for it,
    /// since incremental generators can't see each other's not-yet-emitted output within one pass.
    /// Matching against the textual spelling of the type sidesteps this entirely and mirrors how
    /// the rest of the codegen already treats enum names (schema field name → <c>ToIdentifier()</c>).
    /// </remarks>
    public sealed class FixViewPropertyModel
    {
        public FixViewPropertyModel(
            string propertyName,
            string? fieldNameOverride,
            string declaredTypeText,
            bool isPartialDefinition,
            Location location)
        {
            PropertyName = propertyName;
            FieldNameOverride = fieldNameOverride;
            DeclaredTypeText = declaredTypeText;
            IsPartialDefinition = isPartialDefinition;
            Location = location;
        }

        public string PropertyName { get; }

        /// <summary>The field name from an explicit <c>[FixField("...")]</c>, if any.</summary>
        public string? FieldNameOverride { get; }

        /// <summary>The property's declared type, exactly as written in source.</summary>
        public string DeclaredTypeText { get; }

        /// <summary>
        /// Whether this property declaration is the partial *definition* (no accessor bodies)
        /// that needs a generated implementation, per <c>IPropertySymbol.IsPartialDefinition</c>.
        /// </summary>
        public bool IsPartialDefinition { get; }

        public Location Location { get; }
    }

    /// <summary>A <c>[FixView("MessageName")]</c>-annotated <c>partial struct</c> discovered in the consumer's compilation.</summary>
    public sealed class FixViewRequest
    {
        public FixViewRequest(
            string structName,
            string? containingNamespace,
            string messageName,
            bool isPartial,
            bool isRefStruct,
            Location structLocation,
            ImmutableArray<FixViewPropertyModel> properties)
        {
            StructName = structName;
            ContainingNamespace = containingNamespace;
            MessageName = messageName;
            IsPartial = isPartial;
            IsRefStruct = isRefStruct;
            StructLocation = structLocation;
            Properties = properties;
        }

        public string StructName { get; }

        public string? ContainingNamespace { get; }

        /// <summary>The message name argument passed to <c>[FixView("...")]</c>.</summary>
        public string MessageName { get; }

        public bool IsPartial { get; }

        /// <summary>
        /// Whether the consumer declared the struct as a <c>ref struct</c> — required, since the
        /// generated implementation stores a <c>ReadOnlySpan&lt;byte&gt;</c> field. C# only
        /// requires the <c>ref</c>/<c>readonly</c> modifiers to appear on one partial declaration,
        /// so the generated partial doesn't need to repeat them.
        /// </summary>
        public bool IsRefStruct { get; }

        public Location StructLocation { get; }

        public ImmutableArray<FixViewPropertyModel> Properties { get; }
    }
}
