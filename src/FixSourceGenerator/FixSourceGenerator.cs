using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using FixSourceGenerator.Diagnostics;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;
using FixSourceGenerator.Views;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FixSourceGenerator
{
    /// <summary>
    /// Roslyn entry point: wires <see cref="SchemaReader"/> (XML → <see cref="FixDictionary"/>,
    /// issue #4) into <see cref="FixCodeGenerator"/> (<see cref="FixDictionary"/> → C# source,
    /// issue #5). Each QuickFIX-style DataDictionary XML file passed as an AdditionalFile produces
    /// its own generated namespace (docs/CONTRACT.md §5/§7); a base namespace derived from the
    /// <c>FixGeneratorNamespace</c>/<c>RootNamespace</c> MSBuild properties (or the file path as a
    /// fallback) plus a version-token suffix keeps multiple dictionaries from colliding.
    /// </summary>
    [Generator]
    public sealed class FixSourceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext initContext)
        {
            initContext.RegisterPostInitializationOutput(static postInitContext =>
                postInitContext.AddSource(FixViewAttributes.HintName, FixViewAttributes.Source));

            IncrementalValuesProvider<AdditionalText> xmlSchemaFiles =
                initContext.AdditionalTextsProvider.Where(file => file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            var combined = xmlSchemaFiles.Collect().Combine(initContext.AnalyzerConfigOptionsProvider);

            IncrementalValueProvider<ImmutableArray<ParsedSchema>> parsedSchemas = combined.Select(static (input, cancellationToken) =>
            {
                var (files, configOptions) = input;
                if (files.IsDefaultOrEmpty)
                {
                    return ImmutableArray<ParsedSchema>.Empty;
                }

                configOptions.GlobalOptions.TryGetValue("build_property.FixGeneratorNamespace", out var configuredNamespace);
                configOptions.GlobalOptions.TryGetValue("build_property.RootNamespace", out var rootNamespace);

                var results = ImmutableArray.CreateBuilder<ParsedSchema>();
                foreach (var additionalText in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var parsed = ParseSchemaFile(additionalText, configuredNamespace, rootNamespace, cancellationToken);
                    if (parsed != null)
                    {
                        results.Add(parsed);
                    }
                }

                return results.ToImmutable();
            });

            initContext.RegisterSourceOutput(parsedSchemas, static (sourceContext, schemas) =>
            {
                var emittedRuntimeNamespaces = new HashSet<string>(StringComparer.Ordinal);
                var emittedHintNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var parsed in schemas)
                {
                    sourceContext.CancellationToken.ThrowIfCancellationRequested();
                    foreach (var diagnostic in parsed.Diagnostics)
                    {
                        sourceContext.ReportDiagnostic(diagnostic);
                    }

                    if (parsed.Schema == null)
                    {
                        continue;
                    }

                    EmitSchemaSource(parsed.Schema, parsed.Namespace!, emittedRuntimeNamespaces, emittedHintNames, sourceContext);
                }
            });

            // [FixView]/[FixField] pipeline (issue #13): discover annotated partial ref structs in
            // the consumer's compilation and match them against the same parsed schemas.
            IncrementalValuesProvider<FixViewRequest?> fixViewRequests = initContext.SyntaxProvider
                .ForAttributeWithMetadataName(
                    FixViewAttributes.FixViewAttributeMetadataName,
                    static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.StructDeclarationSyntax,
                    static (context, _) => FixViewDiscovery.Transform(context));

            var fixViewInput = fixViewRequests.Where(static r => r != null).Collect().Combine(parsedSchemas);

            initContext.RegisterSourceOutput(fixViewInput, static (sourceContext, input) =>
            {
                var (requests, schemas) = input;
                if (requests.IsDefaultOrEmpty)
                {
                    return;
                }

                var schemaList = schemas
                    .Where(s => s.Schema != null)
                    .Select(s => (s.Schema!, s.RuntimeNamespace!))
                    .ToList();

                var emittedHintNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var request in requests)
                {
                    sourceContext.CancellationToken.ThrowIfCancellationRequested();
                    if (request == null)
                    {
                        continue;
                    }

                    var result = FixViewEmitter.Generate(request, schemaList, diagnostic => sourceContext.ReportDiagnostic(diagnostic));
                    if (result == null)
                    {
                        continue;
                    }

                    var (hintName, content) = result.Value;
                    if (!emittedHintNames.Add(hintName))
                    {
                        sourceContext.ReportDiagnostic(Diagnostic.Create(
                            FixDiagnostics.DuplicateDefinition,
                            Location.None,
                            "generated source hint name",
                            hintName));
                        continue;
                    }

                    sourceContext.AddSource(hintName, content);
                }
            });
        }

        /// <summary>Parsed schema plus the runtime namespace its helpers live in ({@namespace}.Runtime), or a schema-load failure's diagnostics if parsing failed.</summary>
        private sealed class ParsedSchema
        {
            public ParsedSchema(FixDictionary? schema, string? @namespace, ImmutableArray<Diagnostic> diagnostics)
            {
                Schema = schema;
                Namespace = @namespace;
                Diagnostics = diagnostics;
            }

            public FixDictionary? Schema { get; }

            public string? Namespace { get; }

            /// <summary>The runtime helper namespace ({Namespace}.Runtime) this schema's generated readers use.</summary>
            public string? RuntimeNamespace => Namespace == null ? null : $"{Namespace}.Runtime";

            public ImmutableArray<Diagnostic> Diagnostics { get; }
        }

        private static ParsedSchema? ParseSchemaFile(
            AdditionalText additionalText,
            string? configuredNamespace,
            string? rootNamespace,
            System.Threading.CancellationToken cancellationToken)
        {
            string path = additionalText.Path;
            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

            try
            {
                string? xmlContent = additionalText.GetText(cancellationToken)?.ToString();
                if (string.IsNullOrEmpty(xmlContent))
                {
                    return null;
                }

                var schema = SchemaReader.Parse(xmlContent!, path, diagnostic => diagnostics.Add(diagnostic));
                if (schema == null)
                {
                    // SchemaReader already reported FIX002 for the failure reason.
                    return new ParsedSchema(null, null, diagnostics.ToImmutable());
                }

                string @namespace = GetNamespace(configuredNamespace, rootNamespace, path, schema);
                return new ParsedSchema(schema, @namespace, diagnostics.ToImmutable());
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                diagnostics.Add(Diagnostic.Create(FixDiagnostics.MalformedSchema, Location.None, path, ex.Message));
                return new ParsedSchema(null, null, diagnostics.ToImmutable());
            }
        }

        private static void EmitSchemaSource(
            FixDictionary schema,
            string @namespace,
            HashSet<string> emittedRuntimeNamespaces,
            HashSet<string> emittedHintNames,
            SourceProductionContext sourceContext)
        {
            var context = new GenerationContext(diagnostic => sourceContext.ReportDiagnostic(diagnostic));
            // Share runtime-namespace dedup across all schema files in this compilation.
            foreach (var ns in emittedRuntimeNamespaces)
            {
                context.GeneratedRuntimeNamespaces.Add(ns);
            }

            var generator = new FixCodeGenerator();
            foreach (var (hintName, content) in generator.Generate(@namespace, schema, context))
            {
                sourceContext.CancellationToken.ThrowIfCancellationRequested();

                if (!emittedHintNames.Add(hintName))
                {
                    // Roslyn throws ArgumentException on a duplicate hint name, which would
                    // abort the whole RegisterSourceOutput callback. Skip and report instead,
                    // so one collision doesn't cascade into a wall of downstream CS0246s.
                    sourceContext.ReportDiagnostic(Diagnostic.Create(
                        FixDiagnostics.DuplicateDefinition,
                        Location.None,
                        "generated source hint name",
                        hintName));
                    continue;
                }

                sourceContext.AddSource(hintName, content);
            }

            foreach (var ns in context.GeneratedRuntimeNamespaces)
            {
                emittedRuntimeNamespaces.Add(ns);
            }
        }

        /// <summary>
        /// Derives <c>{Root}.Fix.V{token}</c> (docs/CONTRACT.md §5) from, in priority order: the
        /// <c>FixGeneratorNamespace</c> MSBuild property, the <c>RootNamespace</c> MSBuild property,
        /// or the schema file name (mirrors SbeSourceGenerator's fallback chain).
        /// </summary>
        private static string GetNamespace(string? configuredNamespace, string? rootNamespace, string path, FixDictionary schema)
        {
            string root = !string.IsNullOrWhiteSpace(configuredNamespace)
                ? configuredNamespace!
                : !string.IsNullOrWhiteSpace(rootNamespace)
                    ? rootNamespace!
                    : GetNamespaceFromPath(path);

            return string.IsNullOrWhiteSpace(root)
                ? $"Fix.{schema.VersionToken}"
                : $"{root}.Fix.{schema.VersionToken}";
        }

        private static string GetNamespaceFromPath(string path)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            var parts = fileName
                .Split(new[] { '-', '_', '.', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(part => !part.Equals("schema", StringComparison.OrdinalIgnoreCase))
                .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1));

            return string.Join(".", parts);
        }
    }
}
