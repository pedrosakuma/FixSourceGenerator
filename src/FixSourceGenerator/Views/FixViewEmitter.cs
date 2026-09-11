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
            IReadOnlyList<(FixDictionary Schema, string RuntimeNamespace, string Namespace)> schemas,
            Action<Diagnostic> reportDiagnostic,
            GroupHelperRegistry groupHelpers)
        {
            if (!request.IsPartial || !request.IsRefStruct)
            {
                reportDiagnostic(Diagnostic.Create(
                    Diagnostics.FixDiagnostics.FixViewStructMustBePartial,
                    request.StructLocation,
                    request.StructName));
                return null;
            }

            IReadOnlyList<FixEntry>? scopeEntries = null;
            string scopeName = request.MessageName;
            string? runtimeNs = null;
            string? schemaNs = null;

            bool qualified = request.MessageName.IndexOf('.') >= 0;
            var scopeCandidates = new List<(IReadOnlyList<FixEntry> Entries, string Name, string RuntimeNs, string SchemaNs)>();

            if (qualified)
            {
                CollectQualifiedCandidates(schemas, request.MessageName, messageRoot: true, scopeCandidates);
                if (scopeCandidates.Count == 0)
                {
                    CollectQualifiedCandidates(schemas, request.MessageName, messageRoot: false, scopeCandidates);
                }
            }
            else
            {
                // Preserve the original message contract globally: a component in an earlier
                // schema must not steal a message target in a later schema.
                foreach (var (schema, schemaRuntimeNs, schemaNamespace) in schemas)
                {
                    foreach (var message in schema.Messages)
                    {
                        if (string.Equals(message.Name, request.MessageName, StringComparison.Ordinal))
                        {
                            scopeCandidates.Add((message.Entries, message.Name, schemaRuntimeNs, schemaNamespace));
                            break;
                        }
                    }

                }

                if (scopeCandidates.Count == 0)
                {
                    foreach (var (schema, schemaRuntimeNs, schemaNamespace) in schemas)
                    {
                        if (schema.ComponentsByName.TryGetValue(request.MessageName, out var component))
                        {
                            scopeCandidates.Add((component.Entries, component.Name, schemaRuntimeNs, schemaNamespace));
                        }
                    }
                }

                if (scopeCandidates.Count == 0)
                {
                    foreach (var (schema, schemaRuntimeNs, schemaNamespace) in schemas)
                    {
                        CollectGroups(schema.Messages, request.MessageName, schemaRuntimeNs, schemaNamespace, scopeCandidates);
                    }
                }
            }

            if (scopeCandidates.Count > 1)
            {
                reportDiagnostic(Diagnostic.Create(
                    Diagnostics.FixDiagnostics.FixViewAmbiguousGroupScope,
                    request.StructLocation,
                    request.MessageName,
                    request.StructName,
                    string.Join(", ", scopeCandidates.Select(c => c.SchemaNs + ":" + c.Name).OrderBy(c => c, StringComparer.Ordinal))));
                return null;
            }

            if (scopeCandidates.Count == 1)
            {
                var candidate = scopeCandidates[0];
                scopeEntries = candidate.Entries;
                scopeName = candidate.Name;
                runtimeNs = candidate.RuntimeNs;
                schemaNs = candidate.SchemaNs;
            }

            if (scopeEntries == null || runtimeNs == null || schemaNs == null)
            {
                reportDiagnostic(Diagnostic.Create(
                    Diagnostics.FixDiagnostics.FixViewMessageNotFound,
                    request.StructLocation,
                    request.MessageName,
                    request.StructName));
                return null;
            }

            var fieldsByName = new Dictionary<string, (FixFieldDef Field, bool Required)>(StringComparer.Ordinal);
            FixViewFieldCollector.Collect(scopeEntries, fieldsByName);

            var groupsByName = new Dictionary<string, FixGroupRef>(StringComparer.Ordinal);
            FixViewFieldCollector.CollectGroups(scopeEntries, groupsByName);

            var slots = new List<(FixViewPropertyModel Property, FixFieldDef Field, bool Required)>();
            var groupSlots = new List<(FixViewPropertyModel Property, FixGroupRef Group)>();
            bool hadError = false;

            foreach (var property in request.Properties)
            {
                string lookupName = property.FieldNameOverride ?? property.PropertyName;

                if (groupsByName.TryGetValue(lookupName, out var groupMatch))
                {
                    if (!FixViewTypeCompatibility.IsGroupTypeCompatible(property, schemaNs!, groupMatch.Name))
                    {
                        reportDiagnostic(Diagnostic.Create(
                            Diagnostics.FixDiagnostics.FixViewIncompatibleType,
                            property.Location,
                            property.PropertyName,
                            property.DeclaredTypeText,
                            groupMatch.Name,
                            "group",
                            FixViewTypeCompatibility.GroupReaderDisplayName(schemaNs!, groupMatch.Name)));
                        hadError = true;
                        continue;
                    }

                    groupSlots.Add((property, groupMatch));
                    continue;
                }

                if (!fieldsByName.TryGetValue(lookupName, out var match))
                {
                    if (property.FieldNameOverride != null)
                    {
                        reportDiagnostic(Diagnostic.Create(
                            Diagnostics.FixDiagnostics.FixViewFieldOverrideNotFound,
                            property.Location,
                            property.FieldNameOverride,
                            property.PropertyName,
                            scopeName));
                    }
                    else
                    {
                        var candidates = fieldsByName.Keys.Concat(groupsByName.Keys);
                        string? suggestion = FixViewFieldCollector.FindClosest(lookupName, candidates);
                        string suggestionText = suggestion != null ? $" Did you mean '{suggestion}'?" : string.Empty;
                        reportDiagnostic(Diagnostic.Create(
                            Diagnostics.FixDiagnostics.FixViewPropertyNameMismatch,
                            property.Location,
                            property.PropertyName,
                            request.StructName,
                            scopeName,
                            suggestionText));
                    }

                    hadError = true;
                    continue;
                }

                var (accepted, displayList) = FixViewTypeCompatibility.GetAcceptedTypes(match.Field, match.Required, schemaNs!);
                if (!FixViewTypeCompatibility.IsCompatible(property, accepted))
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

            // Two properties (e.g. one matched by name, one via a [FixField] override) resolving
            // to the same field number would otherwise emit two identical `case N:` labels in the
            // scanning constructor's switch — a C# compile error (CS0152) in the consumer's
            // project. Report a diagnostic instead of emitting uncompilable code.
            var seenFieldNumbers = new Dictionary<int, string>();
            foreach (var slot in slots)
            {
                if (seenFieldNumbers.TryGetValue(slot.Field.Number, out var firstPropertyName))
                {
                    reportDiagnostic(Diagnostic.Create(
                        Diagnostics.FixDiagnostics.FixViewDuplicateFieldTarget,
                        slot.Property.Location,
                        slot.Property.PropertyName,
                        slot.Field.Name,
                        firstPropertyName,
                        request.StructName));
                    hadError = true;
                }
                else
                {
                    seenFieldNumbers[slot.Field.Number] = slot.Property.PropertyName;
                }
            }

            // Same duplicate-target guard for groups: two properties can't both expose the same
            // group's reader (would emit two identically-typed properties returning the same
            // GroupReader — not a compile error by itself, but a confusing/pointless duplicate).
            var seenGroupNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var groupSlot in groupSlots)
            {
                if (seenGroupNames.TryGetValue(groupSlot.Group.Name, out var firstPropertyName))
                {
                    reportDiagnostic(Diagnostic.Create(
                        Diagnostics.FixDiagnostics.FixViewDuplicateFieldTarget,
                        groupSlot.Property.Location,
                        groupSlot.Property.PropertyName,
                        groupSlot.Group.Name,
                        firstPropertyName,
                        request.StructName));
                    hadError = true;
                }
                else
                {
                    seenGroupNames[groupSlot.Group.Name] = groupSlot.Property.PropertyName;
                }
            }

            if (hadError)
            {
                return null;
            }

            string content = EmitStruct(request, runtimeNs!, schemaNs!, scopeEntries, slots, groupSlots, groupHelpers);
            string ns = string.IsNullOrEmpty(request.ContainingNamespace) ? string.Empty : request.ContainingNamespace + ".";
            string hintName = $"{ns}{request.StructName}.FixView.g.cs";
            return (hintName, content);
        }

        private static void CollectQualifiedCandidates(
            IReadOnlyList<(FixDictionary Schema, string RuntimeNamespace, string Namespace)> schemas,
            string path,
            bool messageRoot,
            List<(IReadOnlyList<FixEntry> Entries, string Name, string RuntimeNs, string SchemaNs)> candidates)
        {
            foreach (var (schema, runtimeNs, schemaNs) in schemas)
            {
                if (FixViewFieldCollector.TryResolveQualifiedScope(schema, path, messageRoot, out var entries, out var name))
                {
                    candidates.Add((entries!, path, runtimeNs, schemaNs));
                }
            }
        }

        private static void CollectGroups(
            IReadOnlyList<FixMessageDef> messages,
            string name,
            string runtimeNs,
            string schemaNs,
            List<(IReadOnlyList<FixEntry> Entries, string Name, string RuntimeNs, string SchemaNs)> candidates)
        {
            foreach (var message in messages)
            {
                CollectGroups(message.Entries, name, runtimeNs, schemaNs, candidates, message.Name);
            }
        }

        private static void CollectGroups(
            IReadOnlyList<FixEntry> entries,
            string name,
            string runtimeNs,
            string schemaNs,
            List<(IReadOnlyList<FixEntry> Entries, string Name, string RuntimeNs, string SchemaNs)> candidates,
            string path)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixGroupRef group:
                        if (string.Equals(group.Name, name, StringComparison.Ordinal))
                        {
                            candidates.Add((group.Entries, path + "." + group.Name, runtimeNs, schemaNs));
                        }

                        CollectGroups(group.Entries, name, runtimeNs, schemaNs, candidates, path + "." + group.Name);
                        break;
                    case FixComponentRef component:
                        CollectGroups(component.Component.Entries, name, runtimeNs, schemaNs, candidates, path + "." + component.Component.Name);
                        break;
                }
            }
        }

        private static string EmitStruct(
            FixViewRequest request,
            string runtimeNs,
            string schemaNs,
            IReadOnlyList<FixEntry> scopeEntries,
            List<(FixViewPropertyModel Property, FixFieldDef Field, bool Required)> slots,
            List<(FixViewPropertyModel Property, FixGroupRef Group)> groupSlots,
            GroupHelperRegistry groupHelpers)
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

            string r = $"global::{runtimeNs}.FixSpanReader";
            var directGroups = new List<FixGroupRef>();
            FixViewFieldCollector.CollectDirectGroups(scopeEntries, directGroups);
            // Ids + method bodies for the group-boundary skip helpers this scope needs are owned
            // by the shared, schema-scoped GroupHelperRegistry (issue #32 follow-up), not emitted
            // as private copies inside this struct — a topology reused by several [FixView]
            // structs (or the same struct's own nested groups) is generated exactly once per
            // schema, regardless of how many views call into it.
            var groupHelperIds = groupHelpers.EnsureHelpers(runtimeNs, directGroups);
            string helperContainer = $"global::{runtimeNs}.{GroupHelperRegistry.ContainerTypeName}";

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
            foreach (var slot in groupSlots)
            {
                w.Line($"private readonly int _{slot.Property.PropertyName}Start;");
                w.Line($"private readonly int _{slot.Property.PropertyName}Length;");
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
            foreach (var slot in groupSlots)
            {
                w.Line($"_{slot.Property.PropertyName}Start = 0;");
                w.Line($"_{slot.Property.PropertyName}Length = 0;");
            }

            if (slots.Count + groupSlots.Count > 0)
            {
                w.Line();
                w.Line($"int remaining = {slots.Count + groupSlots.Count};");
                w.Line("int pos = 0;");

                // First-occurrence-wins within the early-exit boundary (docs/CONTRACT.md §12.7):
                // a local "found" flag per requested field, distinct from the `_{Field}Present`
                // struct field (which tracks value-presence for optional fields, not scan
                // progress). Without this, a duplicate tag appearing before every requested field
                // has been located would decrement `remaining` twice for the same slot — either
                // early-exiting before an unrelated later field is found, or (harmlessly but
                // incorrectly) overwriting the already-located first occurrence with a later one,
                // which contradicts the documented "duplicates: first occurrence wins" contract
                // for early-exit projections (unlike the full/last-occurrence reader in
                // ReaderEmitter).
                foreach (var slot in slots)
                {
                    w.Line($"bool found{slot.Property.PropertyName} = false;");
                }
                foreach (var slot in groupSlots)
                {
                    w.Line($"bool found{slot.Property.PropertyName} = false;");
                }

                w.Open($"while (remaining > 0 && {r}.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))");
                foreach (var group in directGroups)
                {
                    string groupId = groupHelperIds[group];
                    w.Open($"if (tag == {group.CounterField.Number})");
                    w.Open($"if (!{r}.TryParseInt(buffer.Slice(valueStart, valueLength), out int count{groupId}) || !{helperContainer}.TrySkip{groupId}(buffer, nextPos, count{groupId}, out int end{groupId}))");
                    w.Line("pos = buffer.Length;");
                    w.Line("continue;");
                    w.Close();
                    foreach (var slot in groupSlots.Where(s => ReferenceEquals(s.Group, group)))
                    {
                        w.Open($"if (!found{slot.Property.PropertyName})");
                        w.Line($"_{slot.Property.PropertyName}Start = pos;");
                        w.Line($"_{slot.Property.PropertyName}Length = end{groupId} - pos;");
                        w.Line($"found{slot.Property.PropertyName} = true;");
                        w.Line("remaining--;");
                        w.Close();
                    }
                    w.Line($"pos = end{groupId};");
                    w.Line("continue;");
                    w.Close();
                }
                w.Open("switch (tag)");
                foreach (var slot in slots)
                {
                    w.Line($"case {slot.Field.Number}:");
                    w.Open($"if (!found{slot.Property.PropertyName})");
                    w.Line($"_{slot.Property.PropertyName}Start = valueStart;");
                    w.Line($"_{slot.Property.PropertyName}Length = valueLength;");
                    if (!slot.Required)
                    {
                        w.Line($"_{slot.Property.PropertyName}Present = true;");
                    }
                    w.Line($"found{slot.Property.PropertyName} = true;");
                    w.Line("remaining--;");
                    w.Close();
                    w.Line("break;");
                }
                w.Close();
                w.Line("pos = nextPos;");
                w.Close();
            }

            w.Close(); // constructor

            foreach (var slot in slots)
            {
                w.Line();
                EmitPropertyImpl(w, runtimeNs, schemaNs, slot.Property, slot.Field, slot.Required);
            }

            foreach (var groupSlot in groupSlots)
            {
                w.Line();
                EmitGroupPropertyImpl(w, schemaNs, groupSlot.Property, groupSlot.Group);
            }

            w.Close(); // struct

            if (hasNamespace)
            {
                w.Close(); // namespace
            }

            return w.ToString();
        }

        /// <summary>
        /// Restricts group lookup to the counter and entries located in this scope.
        /// </summary>
        private static void EmitGroupPropertyImpl(CodeWriter w, string schemaNs, FixViewPropertyModel property, FixGroupRef group)
        {
            string groupReaderType = $"global::{schemaNs}.{group.Name.ToIdentifier()}GroupReader";
            string slice = $"_buffer.Slice(_{property.PropertyName}Start, _{property.PropertyName}Length)";
            w.Line($"public partial {groupReaderType} {property.PropertyName} {{ get => new {groupReaderType}({slice}); }}");
        }

        private static void EmitPropertyImpl(CodeWriter w, string runtimeNs, string schemaNs, FixViewPropertyModel property, FixFieldDef field, bool required)
        {
            string prop = property.PropertyName;
            string startField = $"_{prop}Start";
            string lengthField = $"_{prop}Length";
            string presentField = $"_{prop}Present";
            string valueExpr = $"_buffer.Slice({startField}, {lengthField})";
            var (accepted, _) = FixViewTypeCompatibility.GetAcceptedTypes(field, required, schemaNs);
            string declaredType = property.TypeCandidates.Select(FixViewTypeCompatibility.Normalize).First(accepted.Contains);
            string r = $"global::{runtimeNs}.FixSpanReader";

            // Raw escape hatch: the declared type is exactly ReadOnlySpan<byte> (or its
            // optional-marker-less form; span never has a nullable variant), regardless of the
            // field's own category — always just slice, never parse.
            if (declaredType == "ReadOnlySpan<byte>")
            {
                w.Line($"public partial global::System.ReadOnlySpan<byte> {prop} {{ get => {valueExpr}; }}");
                if (!required)
                {
                    w.Line($"public bool TryGet{prop}(out global::System.ReadOnlySpan<byte> value) {{ value = {valueExpr}; return {presentField}; }}");
                }
                return;
            }

            if (FixEntryHelpers.IsEnumEligible(field))
            {
                string enumName = $"global::{schemaNs}.{field.Name.ToIdentifier()}";
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
