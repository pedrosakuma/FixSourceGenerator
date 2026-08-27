using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FixSourceGenerator.Views
{
    /// <summary>
    /// Extracts a <see cref="FixViewRequest"/> from a <c>partial struct</c> declaration annotated
    /// with <c>[FixView]</c>, using <c>SyntaxProvider.ForAttributeWithMetadataName</c>'s semantic
    /// model (issue #13).
    /// </summary>
    internal static class FixViewDiscovery
    {
        public static FixViewRequest? Transform(GeneratorAttributeSyntaxContext context)
        {
            if (context.TargetSymbol is not INamedTypeSymbol structSymbol)
            {
                return null;
            }

            if (context.TargetNode is not StructDeclarationSyntax structSyntax)
            {
                return null;
            }

            var fixViewAttributeData = context.Attributes.FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == FixViewAttributes.FixViewAttributeMetadataName);
            if (fixViewAttributeData == null)
            {
                return null;
            }

            string messageName = fixViewAttributeData.ConstructorArguments.Length > 0
                ? fixViewAttributeData.ConstructorArguments[0].Value as string ?? string.Empty
                : string.Empty;

            bool isPartial = structSyntax.Modifiers.Any(SyntaxKind.PartialKeyword);
            bool isRefStruct = structSyntax.Modifiers.Any(SyntaxKind.RefKeyword);
            string? containingNamespace = structSymbol.ContainingNamespace is { IsGlobalNamespace: false } ns
                ? ns.ToDisplayString()
                : null;

            var properties = ImmutableArray.CreateBuilder<FixViewPropertyModel>();
            foreach (var member in structSymbol.GetMembers().OfType<IPropertySymbol>())
            {
                if (!member.IsPartialDefinition)
                {
                    // Only the partial *definition* (no accessor bodies yet) needs matching/codegen;
                    // an already-implemented property (e.g. a hand-written helper) is left alone.
                    continue;
                }

                var propertySyntax = member.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<PropertyDeclarationSyntax>()
                    .FirstOrDefault();
                if (propertySyntax == null)
                {
                    continue;
                }

                string? fieldNameOverride = null;
                foreach (var attributeData in member.GetAttributes())
                {
                    if (attributeData.AttributeClass?.ToDisplayString() == FixViewAttributes.FixFieldAttributeMetadataName
                        && attributeData.ConstructorArguments.Length > 0)
                    {
                        fieldNameOverride = attributeData.ConstructorArguments[0].Value as string;
                    }
                }

                string declaredTypeText = propertySyntax.Type.ToString();

                properties.Add(new FixViewPropertyModel(
                    member.Name,
                    fieldNameOverride,
                    declaredTypeText,
                    isPartialDefinition: true,
                    propertySyntax.GetLocation()));
            }

            return new FixViewRequest(
                structSymbol.Name,
                containingNamespace,
                messageName,
                isPartial,
                isRefStruct,
                structSyntax.Identifier.GetLocation(),
                properties.ToImmutable());
        }
    }
}
