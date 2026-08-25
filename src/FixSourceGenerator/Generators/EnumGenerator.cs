using System.Collections.Generic;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Generators
{
    /// <summary>
    /// Emits a C# <c>enum</c> for a field carrying a fixed value domain (docs/CONTRACT.md §3).
    /// Backing type is <c>int</c> (C# forbids a <c>char</c>-backed enum): CHAR members are
    /// initialized with their ASCII char literal (implicitly convertible to int), INT members with
    /// their numeric wire value. Member names are the PascalCase-normalized descriptions.
    /// </summary>
    internal static class EnumGenerator
    {
        public static void EmitEnum(CodeWriter w, FixFieldDef field)
        {
            bool isChar = TypeTranslator.Translate(field.Type).Category == FixTypeCategory.Char;
            string enumName = field.Name.ToIdentifier();

            w.Open($"public enum {enumName} : int");

            var used = new HashSet<string>();
            foreach (var value in field.Values)
            {
                string member = value.Description.ToIdentifier();
                if (member == "_")
                {
                    member = "Value";
                }

                if (!used.Add(member))
                {
                    member = member + "_" + value.EnumValue.ToPascalCase();
                    if (!used.Add(member))
                    {
                        continue;
                    }
                }

                string literal = isChar ? CharLiteral(value.EnumValue) : value.EnumValue;
                w.Line($"{member} = {literal},");
            }

            w.Close();
        }

        private static string CharLiteral(string enumValue)
        {
            char c = string.IsNullOrEmpty(enumValue) ? '\0' : enumValue[0];
            switch (c)
            {
                case '\'':
                    return "'\\''";
                case '\\':
                    return "'\\\\'";
                default:
                    return "'" + c + "'";
            }
        }
    }
}
