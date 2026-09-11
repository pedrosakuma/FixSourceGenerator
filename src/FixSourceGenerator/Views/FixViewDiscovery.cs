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
                    propertySyntax.GetLocation())
                {
                    TypeCandidates = GetTypeCandidates(context.SemanticModel, propertySyntax, member.Type)
                });
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

        private static ImmutableArray<string> GetTypeCandidates(
            SemanticModel semanticModel, PropertyDeclarationSyntax property, ITypeSymbol type)
        {
            if (!ContainsError(type))
            {
                return ImmutableArray.Create(type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }

            // Schema enums/group readers do not exist until this generator's output is added.
            // Resolve their source spelling against the consumer's lexical imports instead.
            var usings = property.Ancestors().SelectMany(node => node switch
            {
                BaseNamespaceDeclarationSyntax ns => ns.Usings,
                CompilationUnitSyntax unit => unit.Usings,
                _ => default(SyntaxList<UsingDirectiveSyntax>)
            }).Concat(semanticModel.Compilation.SyntaxTrees
                .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
                .Where(usingDirective => usingDirective.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                .Distinct().ToArray();

            string text = property.Type.WithoutTrivia().ToString();
            string suffix = string.Empty;
            if (property.Type is NullableTypeSyntax nullable)
            {
                text = nullable.ElementType.WithoutTrivia().ToString();
                suffix = "?";
            }

            string first = text.Split('.', ':')[0];
            var alias = usings.FirstOrDefault(u => u.Alias?.Name.Identifier.ValueText == first);
            if (alias?.Name != null)
            {
                text = alias.Name.ToString() + text.Substring(first.Length).Replace("::", ".");
                // A type alias's target is resolved at its declaration, not at the property.
                var aliasModel = semanticModel.Compilation.GetSemanticModel(alias.SyntaxTree);
                var aliasType = aliasModel.GetTypeInfo(alias.Name).Type;
                if (aliasType != null && !ContainsError(aliasType) && text == alias.Name.ToString())
                {
                    return ImmutableArray.Create(aliasType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + suffix);
                }
            }

            var candidates = ImmutableArray.CreateBuilder<string>();
            if (text.StartsWith("global::", System.StringComparison.Ordinal))
            {
                candidates.Add(text + suffix);
                return candidates.ToImmutable();
            }

            candidates.Add(text + suffix);
            var containingNamespace = semanticModel.GetEnclosingSymbol(property.SpanStart)?.ContainingNamespace;
            while (containingNamespace is { IsGlobalNamespace: false })
            {
                candidates.Add(containingNamespace.ToDisplayString() + "." + text + suffix);
                containingNamespace = containingNamespace.ContainingNamespace;
            }

            foreach (var directive in usings.Where(u => u.Alias == null && u.StaticKeyword == default))
            {
                if (directive.Name != null)
                {
                    candidates.Add(directive.Name + "." + text + suffix);
                    var directiveModel = semanticModel.Compilation.GetSemanticModel(directive.SyntaxTree);
                    var namespaceSyntax = directive.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                    var importNamespace = namespaceSyntax == null ? null : directiveModel.GetDeclaredSymbol(namespaceSyntax) as INamespaceSymbol;
                    while (importNamespace is { IsGlobalNamespace: false })
                    {
                        candidates.Add(importNamespace.ToDisplayString() + "." + directive.Name + "." + text + suffix);
                        importNamespace = importNamespace.ContainingNamespace;
                    }
                }
            }

            return candidates.ToImmutable();
        }

        private static bool ContainsError(ITypeSymbol type) =>
            type.TypeKind == TypeKind.Error ||
            type is INamedTypeSymbol named && named.TypeArguments.Any(ContainsError);
    }
}
