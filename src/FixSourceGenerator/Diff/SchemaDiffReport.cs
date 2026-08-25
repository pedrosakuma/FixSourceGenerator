using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FixSourceGenerator.Diff
{
    public static class SchemaDiffReport
    {
        public static string ToMarkdown(IReadOnlyList<SchemaChange> changes)
        {
            if (changes == null || changes.Count == 0)
            {
                return "# Schema diff\n\nNo changes detected.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("# Schema diff");
            AppendSection(builder, "Breaking changes", SchemaDiffSeverity.Breaking, changes);
            AppendSection(builder, "Warnings", SchemaDiffSeverity.Warning, changes);
            AppendSection(builder, "Informational changes", SchemaDiffSeverity.Info, changes);
            return builder.ToString().TrimEnd();
        }

        private static void AppendSection(StringBuilder builder, string title, SchemaDiffSeverity severity, IReadOnlyList<SchemaChange> changes)
        {
            var bucket = changes.Where(change => change.Severity == severity).ToList();
            if (bucket.Count == 0) return;

            builder.AppendLine();
            builder.Append("## ").AppendLine(title);
            foreach (var change in bucket)
            {
                builder.Append("- **").Append(change.Kind).Append("** `").Append(change.Path).Append("`: ").AppendLine(change.Description);
            }
        }
    }
}
