using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace FixSourceGenerator
{
    /// <summary>
    /// Mutable, per-generation bookkeeping shared across the individual generators for a single
    /// <c>FixCodeGenerator.Generate</c> run (mirrors the role of <c>SchemaContext</c> in the sibling
    /// SbeSourceGenerator).
    /// </summary>
    /// <remarks>
    /// The codegen layer intentionally depends only on an <see cref="Action{Diagnostic}"/> sink
    /// rather than on Roslyn's <c>SourceProductionContext</c>, so it can be unit-tested without the
    /// incremental-generator infrastructure (deliverable #5, item 5). The real
    /// <c>IIncrementalGenerator</c> entry point (out of scope for this layer) adapts
    /// <c>SourceProductionContext.ReportDiagnostic</c> into <see cref="ReportDiagnostic"/>.
    /// </remarks>
    public sealed class GenerationContext
    {
        public GenerationContext(Action<Diagnostic>? reportDiagnostic = null)
        {
            ReportDiagnostic = reportDiagnostic ?? (_ => { });
        }

        /// <summary>Diagnostic sink; never null (defaults to a no-op).</summary>
        public Action<Diagnostic> ReportDiagnostic { get; }

        /// <summary>
        /// Runtime helper namespaces (<c>{ns}.Runtime</c>) that already had the FixSpanReader/
        /// FixSpanWriter/FixGroupEnumerator helpers emitted. Prevents duplicate type definitions
        /// when several schema files share a base namespace (docs/CONTRACT.md §7).
        /// </summary>
        public HashSet<string> GeneratedRuntimeNamespaces { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Fully qualified component reader type names already emitted, so a component referenced by
        /// several messages is generated once per namespace (docs/CONTRACT.md §6).
        /// </summary>
        public HashSet<string> GeneratedComponents { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Fully qualified value-enum type names already emitted, so an enum field shared by several
        /// messages produces a single enum definition.
        /// </summary>
        public HashSet<string> GeneratedEnums { get; } = new HashSet<string>(StringComparer.Ordinal);

        private readonly HashSet<string> _reportedUnknownTypes = new HashSet<string>(StringComparer.Ordinal);

        internal void ReportUnknownFieldType(string fieldName, string fieldType)
        {
            if (!_reportedUnknownTypes.Add(fieldName + "\u0000" + fieldType))
            {
                return;
            }

            ReportDiagnostic(Diagnostic.Create(
                Diagnostics.FixDiagnostics.UnknownFieldType,
                Location.None,
                fieldName,
                fieldType));
        }
    }
}
