using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using FixSourceGenerator.Diagnostics;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;
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
            IncrementalValuesProvider<AdditionalText> xmlSchemaFiles =
                initContext.AdditionalTextsProvider.Where(file => file.Path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            var combined = xmlSchemaFiles.Collect().Combine(initContext.AnalyzerConfigOptionsProvider);

            initContext.RegisterSourceOutput(combined, static (sourceContext, input) =>
            {
                var (files, configOptions) = input;
                if (files.IsDefaultOrEmpty)
                {
                    return;
                }

                configOptions.GlobalOptions.TryGetValue("build_property.FixGeneratorNamespace", out var configuredNamespace);
                configOptions.GlobalOptions.TryGetValue("build_property.RootNamespace", out var rootNamespace);

                var emittedRuntimeNamespaces = new HashSet<string>(StringComparer.Ordinal);
                var emittedHintNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (var additionalText in files)
                {
                    sourceContext.CancellationToken.ThrowIfCancellationRequested();
                    ProcessSchemaFile(additionalText, configuredNamespace, rootNamespace, emittedRuntimeNamespaces, emittedHintNames, sourceContext);
                }
            });
        }

        private static void ProcessSchemaFile(
            AdditionalText additionalText,
            string? configuredNamespace,
            string? rootNamespace,
            HashSet<string> emittedRuntimeNamespaces,
            HashSet<string> emittedHintNames,
            SourceProductionContext sourceContext)
        {
            string path = additionalText.Path;

            try
            {
                string? xmlContent = additionalText.GetText(sourceContext.CancellationToken)?.ToString();
                if (string.IsNullOrEmpty(xmlContent))
                {
                    return;
                }

                var schema = SchemaReader.Parse(xmlContent!, path, diagnostic => sourceContext.ReportDiagnostic(diagnostic));
                if (schema == null)
                {
                    // SchemaReader already reported FIX002 for the failure reason.
                    return;
                }

                string @namespace = GetNamespace(configuredNamespace, rootNamespace, path, schema);

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
            catch (Exception ex) when (!sourceContext.CancellationToken.IsCancellationRequested)
            {
                sourceContext.ReportDiagnostic(Diagnostic.Create(FixDiagnostics.MalformedSchema, Location.None, path, ex.Message));
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
