using System;
using System.Collections.Generic;
using System.Linq;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;
using Microsoft.CodeAnalysis;

namespace FixSourceGenerator.Views
{
    /// <summary>
    /// Matches a <see cref="FixViewRequest"/> against the loaded <see cref="FixDictionary"/>(ies),
    /// reports FIX010–FIX014 for any mismatch, and emits the partial struct's missing property
    /// implementations plus a single early-exit scanning constructor (issue #13).
    /// </summary>
    internal sealed class FixViewEmitter
    {
        public static (string HintName, string Content)? Generate(
            FixViewRequest request,
            IReadOnlyList<(FixDictionary Schema, string RuntimeNamespace)> schemas,
            Action<Diagnostic> reportDiagnostic)
        {
            if (!request.IsPartial || !request.IsRefStruct)
            {
                reportDiagnostic(Diagnostic.Create(
                    Diagnostics.FixDiagnostics.FixViewStructMustBePartial,
                    request.StructLocation,
                    request.StructName));
                return null;
            }

            FixMessageDef? message = null;
            string? runtimeNs = null;
            foreach (var (schema, schemaRuntimeNs) in schemas)
            {
                var match = schema.Messages.FirstOrDefault(m => string.Equals(m.Name, request.MessageName, StringComparison.Ordinal));
                if (match != null)
                {
                    message = match;
                    runtimeNs = schemaRuntimeNs;
                    break;
                }
            }

            if (message == null || runtimeNs == null)
            {
                reportDiagnostic(Diagnostic.Create(
                    Diagnostics.FixDiagnostics.FixViewMessageNotFound,
                    request.StructLocation,
                    request.MessageName,
                    request.StructName));
                return null;
            }

            var fieldsByName = new Dictionary<string, (FixFieldDef Field, bool Required)>(StringComparer.Ordinal);
            FixViewFieldCollector.Collect(message.Entries, fieldsByName);

            var slots = new List<(FixViewPropertyModel Property, FixFieldDef Field, bool Required)>();
            bool hadError = false;

            foreach (var property in request.Properties)
            {
                string lookupName = property.FieldNameOverride ?? property.PropertyName;

                if (!fieldsByName.TryGetValue(lookupName, out var match))
                {
                    if (property.FieldNameOverride != null)
                    {
                        reportDiagnostic(Diagnostic.Create(
                            Diagnostics.FixDiagnostics.FixViewFieldOverrideNotFound,
                            property.Location,
                            property.FieldNameOverride,
                            property.PropertyName,
                            message.Name));
                    }
                    else
                    {
                        string? suggestion = FixViewFieldCollector.FindClosest(lookupName, fieldsByName.Keys);
                        string suggestionText = suggestion != null ? $" Did you mean '{suggestion}'?" : string.Empty;
                        reportDiagnostic(Diagnostic.Create(
                            Diagnostics.FixDiagnostics.FixViewPropertyNameMismatch,
                            property.Location,
                            property.PropertyName,
                            request.StructName,
                            message.Name,
                            suggestionText));
                    }

                    hadError = true;
                    continue;
                }

                var (accepted, displayList) = FixViewTypeCompatibility.GetAcceptedTypes(match.Field, match.Required);
                if (!FixViewTypeCompatibility.IsCompatible(property.DeclaredTypeText, accepted))
                {
                    reportDiagnostic(Diagnostic.Create(
                        Diagnostics.FixDiagnostics.FixViewIncompatibleType,
                        property.Location,
                        property.PropertyName,
                        property.DeclaredTypeText,
                        match.Field.Name,
                        match.Field.Type,
                        displayList));
                    hadError = true;
                    continue;
                }

                slots.Add((property, match.Field, match.Required));
            }

            if (hadError)
            {
                return null;
            }

            string content = EmitStruct(request, message, runtimeNs!, slots);
            string ns = string.IsNullOrEmpty(request.ContainingNamespace) ? string.Empty : request.ContainingNamespace + ".";
            string hintName = $"{ns}{request.StructName}.FixView.g.cs";
            return (hintName, content);
        }

        private static string EmitStruct(
            FixViewRequest request,
            FixMessageDef message,
            string runtimeNs,
            List<(FixViewPropertyModel Property, FixFieldDef Field, bool Required)> slots)
        {
            var w = new CodeWriter();
            w.Line("// <auto-generated/>");
            w.Line("#nullable enable");
            w.Line();

            bool hasNamespace = !string.IsNullOrEmpty(request.ContainingNamespace);
            if (hasNamespace)
            {
                w.Open($"namespace {request.ContainingNamespace}");
            }

            w.Open($"partial struct {request.StructName}");

            string r = $"{runtimeNs}.FixSpanReader";

            w.Line("private readonly global::System.ReadOnlySpan<byte> _buffer;");

            // Named Start/Length/Present fields per requested property (issue #12's eager-location
            // pattern, reused here), plus a bitmask so the constructor can early-exit as soon as
            // every requested tag has been located — the key differentiator from a full message
            // reader, which can't early-exit since it doesn't know its own total field count.
            foreach (var slot in slots)
            {
                w.Line($"private readonly int _{slot.Property.PropertyName}Start;");
                w.Line($"private readonly int _{slot.Property.PropertyName}Length;");
                if (!slot.Required)
                {
                    w.Line($"private readonly bool _{slot.Property.PropertyName}Present;");
                }
            }

            w.Line();
            w.Open($"public {request.StructName}(global::System.ReadOnlySpan<byte> buffer)");
            w.Line("_buffer = buffer;");

            foreach (var slot in slots)
            {
                w.Line($"_{slot.Property.PropertyName}Start = 0;");
                w.Line($"_{slot.Property.PropertyName}Length = 0;");
                if (!slot.Required)
                {
                    w.Line($"_{slot.Property.PropertyName}Present = false;");
                }
            }

            if (slots.Count > 0)
            {
                w.Line();
                w.Line($"int remaining = {slots.Count};");
                w.Line("int pos = 0;");
                w.Open($"while (remaining > 0 && {r}.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))");
                w.Open("switch (tag)");
                foreach (var slot in slots)
                {
                    w.Line($"case {slot.Field.Number}:");
                    w.Line($"    _{slot.Property.PropertyName}Start = valueStart;");
                    w.Line($"    _{slot.Property.PropertyName}Length = valueLength;");
                    if (!slot.Required)
                    {
                        w.Line($"    _{slot.Property.PropertyName}Present = true;");
                    }
                    w.Line("    remaining--;");
                    w.Line("    break;");
                }
                w.Close();
                w.Line("pos = nextPos;");
                w.Close();
            }

            w.Close(); // constructor

            foreach (var slot in slots)
            {
                w.Line();
                EmitPropertyImpl(w, runtimeNs, slot.Property, slot.Field, slot.Required);
            }

            w.Close(); // struct

            if (hasNamespace)
            {
                w.Close(); // namespace
            }

            return w.ToString();
        }

        private static void EmitPropertyImpl(CodeWriter w, string runtimeNs, FixViewPropertyModel property, FixFieldDef field, bool required)
        {
            string prop = property.PropertyName;
            string startField = $"_{prop}Start";
            string lengthField = $"_{prop}Length";
            string presentField = $"_{prop}Present";
            string valueExpr = $"_buffer.Slice({startField}, {lengthField})";
            string declaredType = FixViewTypeCompatibility.Normalize(property.DeclaredTypeText);
            string r = $"{runtimeNs}.FixSpanReader";

            // Raw escape hatch: the declared type is exactly ReadOnlySpan<byte> (or its
            // optional-marker-less form; span never has a nullable variant), regardless of the
            // field's own category — always just slice, never parse.
            if (declaredType == "ReadOnlySpan<byte>")
            {
                w.Line($"public partial global::System.ReadOnlySpan<byte> {prop} {{ get => {valueExpr}; }}");
                return;
            }

            if (FixEntryHelpers.IsEnumEligible(field))
            {
                string enumName = field.Name.ToIdentifier();
                var translated = TypeTranslator.Translate(field.Type);
                bool isCharBacked = translated.Category == FixTypeCategory.Char;
                string parseExpr = isCharBacked ? $"{r}.ParseByte({valueExpr})" : $"{r}.ParseInt({valueExpr})";
                string tryParseExpr = isCharBacked ? $"{r}.TryParseByte({valueExpr}, out var v)" : $"{r}.TryParseInt({valueExpr}, out var v)";
                bool isNullable = declaredType.EndsWith("?", StringComparison.Ordinal);
                string underlying = isCharBacked ? "byte" : "int";

                if (declaredType == underlying || declaredType == underlying + "?")
                {
                    // Raw underlying-value escape hatch (byte/int), not the enum type.
                    if (isNullable)
                    {
                        w.Line($"public partial {underlying}? {prop} {{ get => {presentField} && {tryParseExpr} ? v : ({underlying}?)null; }}");
                    }
                    else
                    {
                        w.Line($"public partial {underlying} {prop} {{ get => ({underlying}){parseExpr}; }}");
                    }

                    return;
                }

                if (isNullable)
                {
                    w.Line($"public partial {enumName}? {prop} {{ get => {presentField} && {tryParseExpr} ? ({enumName})v : ({enumName}?)null; }}");
                }
                else
                {
                    w.Line($"public partial {enumName} {prop} {{ get => ({enumName}){parseExpr}; }}");
                }

                return;
            }

            var category = TypeTranslator.Translate(field.Type).Category;
            bool nullable = declaredType.EndsWith("?", StringComparison.Ordinal);

            switch (category)
            {
                case FixTypeCategory.Char:
                    EmitScalar(w, prop, nullable, "char", $"(char){r}.ParseByte({valueExpr})",
                        $"{presentField} && {r}.TryParseByte({valueExpr}, out var v) ? (char)v : (char?)null");
                    break;

                case FixTypeCategory.Int:
                    EmitScalar(w, prop, nullable, "int", $"{r}.ParseInt({valueExpr})",
                        $"{presentField} && {r}.TryParseInt({valueExpr}, out var v) ? v : (int?)null");
                    break;

                case FixTypeCategory.Decimal:
                    EmitScalar(w, prop, nullable, "decimal", $"{r}.ParseDecimal({valueExpr})",
                        $"{presentField} && {r}.TryParseDecimal({valueExpr}, out var v) ? v : (decimal?)null");
                    break;

                case FixTypeCategory.Bool:
                    EmitScalar(w, prop, nullable, "bool", $"{r}.ParseBool({valueExpr})",
                        $"{presentField} && {r}.TryParseBool({valueExpr}, out var v) ? v : (bool?)null");
                    break;

                case FixTypeCategory.DateTime:
                    EmitScalar(w, prop, nullable, "global::System.DateTime", $"{r}.ParseDateTime({valueExpr})",
                        $"{presentField} && {r}.TryParseDateTime({valueExpr}, out var v) ? v : (global::System.DateTime?)null");
                    break;

                case FixTypeCategory.DateOnly:
                    EmitScalar(w, prop, nullable, "global::System.DateOnly", $"{r}.ParseDateOnly({valueExpr})",
                        $"{presentField} && {r}.TryParseDateOnly({valueExpr}, out var v) ? v : (global::System.DateOnly?)null");
                    break;

                case FixTypeCategory.TimeOnly:
                    EmitScalar(w, prop, nullable, "global::System.TimeOnly", $"{r}.ParseTimeOnly({valueExpr})",
                        $"{presentField} && {r}.TryParseTimeOnly({valueExpr}, out var v) ? v : (global::System.TimeOnly?)null");
                    break;

                default:
                    // Span/MultiValue* fields only ever accept ReadOnlySpan<byte>, already handled above.
                    break;
            }
        }

        private static void EmitScalar(CodeWriter w, string prop, bool nullable, string type, string requiredExpr, string optionalExpr)
        {
            if (nullable)
            {
                w.Line($"public partial {type}? {prop} {{ get => {optionalExpr}; }}");
            }
            else
            {
                w.Line($"public partial {type} {prop} {{ get => {requiredExpr}; }}");
            }
        }
    }
}
