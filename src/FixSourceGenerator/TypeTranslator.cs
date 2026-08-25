using System;
using System.Collections.Generic;

namespace FixSourceGenerator
{
    /// <summary>
    /// Coarse classification of a FIX field type into the C# representation strategy used by the
    /// reader/writer generators (docs/CONTRACT.md §3).
    /// </summary>
    public enum FixTypeCategory
    {
        /// <summary>Raw byte span (STRING/DATA/unknown, etc.).</summary>
        Span,
        /// <summary>Single ASCII character (CHAR).</summary>
        Char,
        /// <summary>Integral value (INT/LENGTH/SEQNUM/NUMINGROUP/...).</summary>
        Int,
        /// <summary>Fixed-point decimal (PRICE/QTY/AMT/...).</summary>
        Decimal,
        /// <summary>Boolean (BOOLEAN, wire Y/N).</summary>
        Bool,
        /// <summary>UTC timestamp (UTCTIMESTAMP/TZTIMESTAMP).</summary>
        DateTime,
        /// <summary>Calendar date only (UTCDATEONLY/LOCALMKTDATE/...).</summary>
        DateOnly,
        /// <summary>Time of day only (UTCTIMEONLY/TIME/...).</summary>
        TimeOnly
    }

    /// <summary>
    /// The resolved translation of a single FIX field type: its C# type name, its category (which
    /// drives which runtime accessor/writer overload the generator emits) and whether the raw FIX
    /// type string was recognized at all.
    /// </summary>
    public sealed class TranslatedFixType
    {
        public TranslatedFixType(string cSharpType, FixTypeCategory category, bool isKnown)
        {
            CSharpType = cSharpType;
            Category = category;
            IsKnown = isKnown;
        }

        /// <summary>The C# type name (BCL types are emitted fully qualified with <c>global::</c>).</summary>
        public string CSharpType { get; }

        public FixTypeCategory Category { get; }

        /// <summary>
        /// False when the raw FIX type was not recognized; the caller must report
        /// <see cref="Diagnostics.FixDiagnostics.UnknownFieldType"/> and treat the field as a span.
        /// </summary>
        public bool IsKnown { get; }

        public bool IsSpan => Category == FixTypeCategory.Span;
    }

    /// <summary>
    /// Maps a raw FIX field <c>type</c> string (as declared in a QuickFIX DataDictionary) onto the
    /// C# type and parsing/formatting strategy used by the generated reader/writer, per the type
    /// table in docs/CONTRACT.md §3.
    /// </summary>
    public static class TypeTranslator
    {
        // BCL types are fully qualified per docs/CONTRACT.md §5 (BCL collision handling).
        private const string SpanType = "global::System.ReadOnlySpan<byte>";
        private const string DateTimeType = "global::System.DateTime";
        private const string DateOnlyType = "global::System.DateOnly";
        private const string TimeOnlyType = "global::System.TimeOnly";

        private static readonly Dictionary<string, TranslatedFixType> Map =
            new Dictionary<string, TranslatedFixType>(StringComparer.OrdinalIgnoreCase)
            {
                { "STRING", Span() },
                { "MULTIPLEVALUESTRING", Span() },
                { "MULTIPLECHARVALUE", Span() },
                { "MULTIPLESTRINGVALUE", Span() },
                { "CURRENCY", Span() },
                { "EXCHANGE", Span() },
                { "COUNTRY", Span() },
                { "LANGUAGE", Span() },
                { "MONTHYEAR", Span() },
                { "XID", Span() },
                { "XIDREF", Span() },
                { "DATA", Span() },
                { "XMLDATA", Span() },

                { "CHAR", new TranslatedFixType("char", FixTypeCategory.Char, true) },

                { "INT", Int() },
                { "LENGTH", Int() },
                { "SEQNUM", Int() },
                { "NUMINGROUP", Int() },
                { "DAYOFMONTH", Int() },
                { "TAGNUM", Int() },

                { "FLOAT", Decimal() },
                { "PRICE", Decimal() },
                { "PRICEOFFSET", Decimal() },
                { "QTY", Decimal() },
                { "AMT", Decimal() },
                { "PERCENTAGE", Decimal() },

                { "BOOLEAN", new TranslatedFixType("bool", FixTypeCategory.Bool, true) },

                { "UTCTIMESTAMP", new TranslatedFixType(DateTimeType, FixTypeCategory.DateTime, true) },
                { "TZTIMESTAMP", new TranslatedFixType(DateTimeType, FixTypeCategory.DateTime, true) },

                { "UTCDATEONLY", new TranslatedFixType(DateOnlyType, FixTypeCategory.DateOnly, true) },
                { "UTCDATE", new TranslatedFixType(DateOnlyType, FixTypeCategory.DateOnly, true) },
                { "LOCALMKTDATE", new TranslatedFixType(DateOnlyType, FixTypeCategory.DateOnly, true) },

                { "UTCTIMEONLY", new TranslatedFixType(TimeOnlyType, FixTypeCategory.TimeOnly, true) },
                { "LOCALMKTTIME", new TranslatedFixType(TimeOnlyType, FixTypeCategory.TimeOnly, true) },
                { "TZTIMEONLY", new TranslatedFixType(TimeOnlyType, FixTypeCategory.TimeOnly, true) },
                { "TIME", new TranslatedFixType(TimeOnlyType, FixTypeCategory.TimeOnly, true) },
            };

        /// <summary>
        /// Resolves the C# translation of the given raw FIX type. Unknown types fall back to a raw
        /// byte span with <see cref="TranslatedFixType.IsKnown"/> == false; the caller is
        /// responsible for reporting <see cref="Diagnostics.FixDiagnostics.UnknownFieldType"/>.
        /// </summary>
        public static TranslatedFixType Translate(string fixType)
        {
            if (!string.IsNullOrEmpty(fixType) && Map.TryGetValue(fixType, out var translated))
            {
                return translated;
            }

            return new TranslatedFixType(SpanType, FixTypeCategory.Span, false);
        }

        private static TranslatedFixType Span() => new TranslatedFixType(SpanType, FixTypeCategory.Span, true);

        private static TranslatedFixType Int() => new TranslatedFixType("int", FixTypeCategory.Int, true);

        private static TranslatedFixType Decimal() => new TranslatedFixType("decimal", FixTypeCategory.Decimal, true);
    }
}
