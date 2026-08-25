using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FixSourceGenerator
{
    /// <summary>
    /// Identifier normalization helpers used by the codegen layer.
    /// </summary>
    /// <remarks>
    /// PascalCase normalization algorithm (documented per docs/CONTRACT.md §5, deliverable #5 3c):
    /// <list type="number">
    /// <item>Split the raw text on any character that is not a letter or digit (so
    /// <c>EXECUTING_FIRM</c>, <c>"Good Till Cancel"</c> and <c>a-b</c> all tokenize).</item>
    /// <item>Each token is capitalized: an ALL-CAPS token (has upper, no lower) is lowercased
    /// first (so <c>FIRM</c> → <c>Firm</c>, not <c>FIRM</c>); an already mixed/PascalCase token is
    /// preserved verbatim (so <c>NewOrderSingle</c> stays intact); then the first letter is
    /// upper-cased.</item>
    /// <item>Tokens are concatenated. An empty result becomes <c>_</c>; a result starting with a
    /// digit is prefixed with <c>_</c>; a result colliding with a C# keyword is escaped with a
    /// leading <c>@</c>.</item>
    /// </list>
    /// </remarks>
    public static class StringExtensions
    {
        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while"
        };

        /// <summary>
        /// Normalizes an arbitrary label (field description, name) into a PascalCase C# identifier
        /// stem (without keyword/leading-digit escaping applied). See the type remarks for the
        /// full algorithm.
        /// </summary>
        public static string ToPascalCase(this string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var tokens = new List<string>();
            var current = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(c);
                }
                else if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            var sb = new StringBuilder();
            foreach (string token in tokens)
            {
                sb.Append(Capitalize(token));
            }

            return sb.ToString();
        }

        /// <summary>
        /// Produces a legal, non-colliding C# identifier from a raw label: applies
        /// <see cref="ToPascalCase(string)"/> then escapes leading digits (with <c>_</c>) and C#
        /// keywords (with a verbatim <c>@</c> prefix), per docs/CONTRACT.md §5.
        /// </summary>
        public static string ToIdentifier(this string value)
        {
            string pascal = value.ToPascalCase();
            if (pascal.Length == 0)
            {
                return "_";
            }

            if (char.IsDigit(pascal[0]))
            {
                pascal = "_" + pascal;
            }

            return CSharpKeywords.Contains(pascal) ? "@" + pascal : pascal;
        }

        private static string Capitalize(string token)
        {
            if (token.Length == 0)
            {
                return token;
            }

            bool hasLower = token.Any(char.IsLower);
            bool hasUpper = token.Any(char.IsUpper);

            string body = (hasUpper && !hasLower) ? token.ToLowerInvariant() : token;

            char[] chars = body.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsLetter(chars[i]))
                {
                    chars[i] = char.ToUpperInvariant(chars[i]);
                    break;
                }
            }

            return new string(chars);
        }
    }
}
