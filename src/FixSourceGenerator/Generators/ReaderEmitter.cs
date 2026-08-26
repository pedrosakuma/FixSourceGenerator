using System.Collections.Generic;
using System.Text;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Generators
{
    /// <summary>
    /// Emits a <c>readonly ref struct</c> reader over a FIX buffer/sub-span for an arbitrary list of
    /// entries. Reused for message readers, component readers and group-entry readers (unlimited
    /// nesting), per docs/CONTRACT.md §2.1/§6.
    /// </summary>
    /// <remarks>
    /// Optional-span representation choice (docs/CONTRACT.md §4 left this to #5): required span
    /// fields expose a <c>ReadOnlySpan&lt;byte&gt;</c> property; optional span fields expose a
    /// <c>bool TryGet{Field}(out ReadOnlySpan&lt;byte&gt;)</c> method. This avoids an empty-span
    /// sentinel ambiguity (an empty value is distinct from an absent field).
    /// </remarks>
    internal sealed class ReaderEmitter
    {
        private readonly string _runtimeNs;
        private readonly GenerationContext _context;

        public ReaderEmitter(string runtimeNs, GenerationContext context)
        {
            _runtimeNs = runtimeNs;
            _context = context;
        }

        public void EmitReader(CodeWriter w, string typeName, IReadOnlyList<FixEntry> entries)
        {
            w.Open($"public readonly ref struct {typeName}");
            w.Line("private readonly global::System.ReadOnlySpan<byte> _buffer;");
            w.Line();
            w.Line($"public {typeName}(global::System.ReadOnlySpan<byte> buffer) => _buffer = buffer;");

            var usedNames = new HashSet<string> { "_buffer", typeName };
            var groups = new List<FixGroupRef>();

            foreach (var entry in entries)
            {
                w.Line();
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        EmitFieldMember(w, fieldRef, usedNames);
                        break;
                    case FixComponentRef componentRef:
                        EmitComponentMember(w, componentRef, usedNames);
                        break;
                    case FixGroupRef groupRef:
                        EmitGroupMember(w, groupRef, usedNames);
                        groups.Add(groupRef);
                        break;
                }
            }

            foreach (var groupRef in groups)
            {
                w.Line();
                EmitGroupTypes(w, groupRef);
            }

            w.Close();
        }

        private void EmitFieldMember(CodeWriter w, FixFieldRef fieldRef, HashSet<string> usedNames)
        {
            var field = fieldRef.Field;
            var translated = TypeTranslator.Translate(field.Type);
            if (!translated.IsKnown)
            {
                _context.ReportUnknownFieldType(field.Name, field.Type);
            }

            string prop = field.Name.ToIdentifier();
            if (!usedNames.Add(prop))
            {
                return;
            }

            int tag = field.Number;
            bool required = fieldRef.Required;
            string r = $"{_runtimeNs}.FixSpanReader";

            if (FixEntryHelpers.IsEnumEligible(field))
            {
                string enumName = field.Name.ToIdentifier();
                bool isChar = translated.Category == FixTypeCategory.Char;
                string getter = isChar ? "GetByte" : "GetInt";
                string tryGetter = isChar ? "TryGetByte" : "TryGetInt";
                if (required)
                {
                    w.Line($"public {enumName} {prop} => ({enumName}){r}.{getter}(_buffer, {tag});");
                }
                else
                {
                    w.Line($"public {enumName}? {prop} => {r}.{tryGetter}(_buffer, {tag}, out var v) ? ({enumName})v : ({enumName}?)null;");
                }

                // Strict variant (docs/CONTRACT.md §10 "enum domain validation"): the plain
                // property above always casts the wire value to the enum, even if it's outside
                // the schema's known <value> domain (silently producing an "unnamed" enum member).
                // TryGet{Field}Strict lets callers opt into rejecting out-of-domain values without
                // paying for a switch/IsDefined check on every read by default.
                w.Line($"public bool TryGet{prop}Strict(out {enumName} value)");
                w.Line("{");
                if (required)
                {
                    w.Line($"    value = {prop};");
                }
                else
                {
                    w.Line($"    if (!{r}.{tryGetter}(_buffer, {tag}, out var v))");
                    w.Line("    {");
                    w.Line("        value = default;");
                    w.Line("        return false;");
                    w.Line("    }");
                    w.Line();
                    w.Line($"    value = ({enumName})v;");
                }
                w.Line($"    return value.IsDefined();");
                w.Line("}");

                return;
            }

            switch (translated.Category)
            {
                case FixTypeCategory.Span:
                    if (required)
                    {
                        w.Line($"public global::System.ReadOnlySpan<byte> {prop} => {r}.GetField(_buffer, {tag});");
                    }
                    else
                    {
                        w.Line($"public bool TryGet{prop}(out global::System.ReadOnlySpan<byte> value) => {r}.TryGetField(_buffer, {tag}, out value);");
                    }

                    break;

                case FixTypeCategory.Char:
                    EmitScalar(w, prop, tag, required, "char", $"(char){r}.GetByte(_buffer, {tag})",
                        $"{r}.TryGetByte(_buffer, {tag}, out var v) ? (char)v : (char?)null");
                    break;

                case FixTypeCategory.Int:
                    EmitScalar(w, prop, tag, required, "int", $"{r}.GetInt(_buffer, {tag})",
                        $"{r}.TryGetInt(_buffer, {tag}, out var v) ? v : (int?)null");
                    break;

                case FixTypeCategory.Decimal:
                    EmitScalar(w, prop, tag, required, "decimal", $"{r}.GetDecimal(_buffer, {tag})",
                        $"{r}.TryGetDecimal(_buffer, {tag}, out var v) ? v : (decimal?)null");
                    break;

                case FixTypeCategory.Bool:
                    EmitScalar(w, prop, tag, required, "bool", $"{r}.GetBool(_buffer, {tag})",
                        $"{r}.TryGetBool(_buffer, {tag}, out var v) ? v : (bool?)null");
                    break;

                case FixTypeCategory.DateTime:
                    EmitScalar(w, prop, tag, required, "global::System.DateTime", $"{r}.GetDateTime(_buffer, {tag})",
                        $"{r}.TryGetDateTime(_buffer, {tag}, out var v) ? v : (global::System.DateTime?)null");
                    break;

                case FixTypeCategory.DateOnly:
                    EmitScalar(w, prop, tag, required, "global::System.DateOnly", $"{r}.GetDateOnly(_buffer, {tag})",
                        $"{r}.TryGetDateOnly(_buffer, {tag}, out var v) ? v : (global::System.DateOnly?)null");
                    break;

                case FixTypeCategory.TimeOnly:
                    EmitScalar(w, prop, tag, required, "global::System.TimeOnly", $"{r}.GetTimeOnly(_buffer, {tag})",
                        $"{r}.TryGetTimeOnly(_buffer, {tag}, out var v) ? v : (global::System.TimeOnly?)null");
                    break;

                case FixTypeCategory.MultiValueChar:
                case FixTypeCategory.MultiValueString:
                    // Raw span accessor preserved for callers who want the unsplit wire value
                    // (e.g. to forward it verbatim), plus a typed, allocation-free enumerator over
                    // the space-delimited tokens (docs/CONTRACT.md §10 "typed
                    // MULTIPLEVALUESTRING/MULTIPLECHARVALUE parsing").
                    if (required)
                    {
                        w.Line($"public global::System.ReadOnlySpan<byte> {prop} => {r}.GetField(_buffer, {tag});");
                    }
                    else
                    {
                        w.Line($"public bool TryGet{prop}(out global::System.ReadOnlySpan<byte> value) => {r}.TryGetField(_buffer, {tag}, out value);");
                    }

                    w.Line($"public {_runtimeNs}.FixMultiValueEnumerator {prop}Values => {r}.GetMultiValue(_buffer, {tag});");
                    break;
            }
        }

        private static void EmitScalar(CodeWriter w, string prop, int tag, bool required, string type, string requiredExpr, string optionalExpr)
        {
            if (required)
            {
                w.Line($"public {type} {prop} => {requiredExpr};");
            }
            else
            {
                w.Line($"public {type}? {prop} => {optionalExpr};");
            }
        }

        private static void EmitComponentMember(CodeWriter w, FixComponentRef componentRef, HashSet<string> usedNames)
        {
            string prop = componentRef.Component.Name.ToIdentifier();
            string readerType = componentRef.Component.Name.ToIdentifier() + "Reader";
            if (!usedNames.Add(prop))
            {
                return;
            }

            w.Line($"public {readerType} {prop} => new {readerType}(_buffer);");
        }

        private static void EmitGroupMember(CodeWriter w, FixGroupRef groupRef, HashSet<string> usedNames)
        {
            string prop = groupRef.Name.ToIdentifier();
            string readerType = groupRef.Name.ToIdentifier() + "GroupReader";
            if (!usedNames.Add(prop))
            {
                return;
            }

            w.Line($"public {readerType} {prop} => new {readerType}(_buffer);");
        }

        public void EmitStandaloneGroupReader(CodeWriter w, FixGroupRef groupRef)
        {
            EmitGroupTypes(w, groupRef);
        }

        private void EmitGroupTypes(CodeWriter w, FixGroupRef groupRef)
        {
            string groupId = groupRef.Name.ToIdentifier();
            string groupReaderType = groupId + "GroupReader";
            string entryReaderType = groupId + "EntryReader";
            int counterTag = groupRef.CounterField.Number;
            int delimiterTag = FixEntryHelpers.GetDelimiterTag(groupRef.Entries);
            var entryTags = FixEntryHelpers.FlattenEntryTags(groupRef.Entries);
            string r = $"{_runtimeNs}.FixSpanReader";

            w.Open($"public readonly ref struct {groupReaderType}");
            w.Line("private readonly global::System.ReadOnlySpan<byte> _buffer;");
            w.Line();
            w.Line($"private static readonly int[] EntryTags = new int[] {{ {Join(entryTags)} }};");
            w.Line();
            w.Line($"public {groupReaderType}(global::System.ReadOnlySpan<byte> buffer) => _buffer = buffer;");
            w.Line();
            w.Line($"public int Count => {r}.TryGetInt(_buffer, {counterTag}, out var c) ? c : 0;");
            w.Line();
            w.Line("public Enumerator GetEnumerator() => new Enumerator(_buffer);");
            w.Line();

            w.Open("public ref struct Enumerator");
            w.Line($"private {_runtimeNs}.FixGroupEnumerator _inner;");
            w.Line();
            w.Line($"public Enumerator(global::System.ReadOnlySpan<byte> buffer) => _inner = new {_runtimeNs}.FixGroupEnumerator(buffer, {counterTag}, {delimiterTag}, EntryTags);");
            w.Line();
            w.Line($"public {entryReaderType} Current => new {entryReaderType}(_inner.Current);");
            w.Line();
            w.Line("public bool MoveNext() => _inner.MoveNext();");
            w.Close();

            w.Line();
            EmitReader(w, entryReaderType, groupRef.Entries);

            w.Close();
        }

        private static string Join(IReadOnlyList<int> tags)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tags.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(tags[i]);
            }

            return sb.ToString();
        }
    }
}
