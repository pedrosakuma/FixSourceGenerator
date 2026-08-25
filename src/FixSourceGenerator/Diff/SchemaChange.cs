using System;

namespace FixSourceGenerator.Diff
{
    public sealed class SchemaChange
    {
        public SchemaChange(SchemaChangeKind kind, SchemaDiffSeverity severity, string path, string description, string? oldValue = null, string? newValue = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (description == null) throw new ArgumentNullException(nameof(description));
            Kind = kind;
            Severity = severity;
            Path = path;
            Description = description;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public SchemaChangeKind Kind { get; }
        public SchemaDiffSeverity Severity { get; }
        public string Path { get; }
        public string Description { get; }
        public string? OldValue { get; }
        public string? NewValue { get; }
    }
}
