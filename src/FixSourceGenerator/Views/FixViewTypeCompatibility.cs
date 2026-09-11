using System;
using System.Collections.Generic;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Views
{
    /// <summary>
    /// The <c>[FixView]</c> property-type compatibility matrix (issue #13): every FIX category
    /// accepts its native C# type, plus <c>ReadOnlySpan&lt;byte&gt;</c> as a raw/no-parse escape
    /// hatch. Enum-eligible fields additionally accept the generated enum type and its underlying
    /// wire representation (<c>byte</c> for CHAR-backed enums, <c>int</c> for INT-backed enums).
    /// </summary>
    internal static class FixViewTypeCompatibility
    {
        /// <summary>
        /// Returns the set of declared-type spellings (normalized: no <c>global::</c>/<c>System.</c>
        /// prefixes, no whitespace) a <c>[FixView]</c> property is allowed to use for the given
        /// field, plus the human-readable list used in FIX014's message.
        /// </summary>
        public static (HashSet<string> Accepted, string DisplayList) GetAcceptedTypes(FixFieldDef field, bool required, string schemaNamespace)
        {
            var accepted = new HashSet<string>(StringComparer.Ordinal);
            var display = new List<string>();

            void Add(string type)
            {
                if (accepted.Add(Normalize(type)))
                {
                    display.Add(type);
                }
            }

            const string span = "ReadOnlySpan<byte>";

            if (FixEntryHelpers.IsEnumEligible(field))
            {
                string enumName = $"global::{schemaNamespace}.{field.Name.ToIdentifier()}";
                var translated = TypeTranslator.Translate(field.Type);
                bool isCharBacked = translated.Category == FixTypeCategory.Char;
                string underlying = isCharBacked ? "byte" : "int";

                Add(required ? enumName : enumName + "?");
                Add(required ? underlying : underlying + "?");
                Add(span);
                return (accepted, string.Join(", ", display));
            }

            var category = TypeTranslator.Translate(field.Type).Category;
            switch (category)
            {
                case FixTypeCategory.Span:
                case FixTypeCategory.MultiValueChar:
                case FixTypeCategory.MultiValueString:
                    Add(span);
                    break;

                case FixTypeCategory.Char:
                    Add(required ? "char" : "char?");
                    Add(span);
                    break;

                case FixTypeCategory.Int:
                    Add(required ? "int" : "int?");
                    Add(span);
                    break;

                case FixTypeCategory.Decimal:
                    Add(required ? "decimal" : "decimal?");
                    Add(span);
                    break;

                case FixTypeCategory.Bool:
                    Add(required ? "bool" : "bool?");
                    Add(span);
                    break;

                case FixTypeCategory.DateTime:
                    Add(required ? "DateTime" : "DateTime?");
                    Add(span);
                    break;

                case FixTypeCategory.DateOnly:
                    Add(required ? "DateOnly" : "DateOnly?");
                    Add(span);
                    break;

                case FixTypeCategory.TimeOnly:
                    Add(required ? "TimeOnly" : "TimeOnly?");
                    Add(span);
                    break;
            }

            return (accepted, string.Join(", ", display));
        }

        /// <summary>
        /// Normalizes a declared type's source spelling for comparison: strips <c>global::</c> and
        /// leading <c>System.</c> namespace qualifiers and all whitespace, so
        /// <c>"global::System.ReadOnlySpan&lt;byte&gt;"</c>, <c>"System.ReadOnlySpan&lt;byte&gt;"</c>
        /// and <c>"ReadOnlySpan&lt;byte&gt;"</c> all compare equal.
        /// </summary>
        public static string Normalize(string typeText)
        {
            string s = typeText.Replace(" ", string.Empty).Replace("\t", string.Empty).Replace("\r", string.Empty).Replace("\n", string.Empty);
            s = s.Replace("global::", string.Empty);
            if (s.StartsWith("System.", StringComparison.Ordinal))
            {
                s = s.Substring("System.".Length);
            }
            return s;
        }

        public static bool IsCompatible(FixViewPropertyModel property, HashSet<string> accepted)
        {
            foreach (string candidate in property.TypeCandidates)
            {
                if (accepted.Contains(Normalize(candidate)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether a declared property type matches the given group's reader type (issue #17):
        /// exactly <c>{SchemaNamespace}.{GroupName}GroupReader</c>, after discovery resolves
        /// imports and aliases in the consumer's context. Unlike scalar
        /// fields, a group has no nullable/span escape hatch — it always "exists" as a reader
        /// (an absent group simply has <c>Count == 0</c>).
        /// </summary>
        public static bool IsGroupTypeCompatible(FixViewPropertyModel property, string runtimeNamespace, string groupName)
        {
            string expected = Normalize($"{runtimeNamespace}.{groupName.ToIdentifier()}GroupReader");
            return IsCompatible(property, new HashSet<string>(StringComparer.Ordinal) { expected });
        }

        /// <summary>The human-readable expected type name for a group property, used in FIX014's message.</summary>
        public static string GroupReaderDisplayName(string schemaNamespace, string groupName) =>
            $"global::{schemaNamespace}.{groupName.ToIdentifier()}GroupReader";
    }
}
