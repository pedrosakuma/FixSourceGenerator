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
    /// <para>
    /// Optional-span representation choice (docs/CONTRACT.md §4 left this to #5): required span
    /// fields expose a <c>ReadOnlySpan&lt;byte&gt;</c> property; optional span fields expose a
    /// <c>bool TryGet{Field}(out ReadOnlySpan&lt;byte&gt;)</c> method. This avoids an empty-span
    /// sentinel ambiguity (an empty value is distinct from an absent field).
    /// </para>
    /// <para>
    /// Field access strategy: **eager location, lazy parsing** (docs/CONTRACT.md §2, issue #12).
    /// The constructor performs a single forward-only scan of the buffer, locating (not
    /// converting) every field declared at this level of the schema — own fields plus fields
    /// reached through components, since a component reader is just a view over the same
    /// buffer/tags — and recording each one's <c>(start, length)</c> as a pair of named `int`
    /// fields per property, not a generic index/array. This means:
    /// - Only a single scan per reader construction, regardless of how many properties are later
    ///   read — no O(n·k) rescans, no per-field lookup.
    /// - Struct size is exactly proportional to the number of fields declared at this level — no
    ///   fixed/generic capacity, no reliance on <c>[InlineArray]</c> (which would force raising the
    ///   consumer's minimum TFM to net8+; this design intentionally avoids that).
    /// - Typed parsing (<c>decimal</c>/<c>DateTime</c>/enum cast/etc.) remains lazy: it only runs
    ///   when a property getter is actually called, so unread fields never pay conversion cost —
    ///   this matters especially for <c>{Group}EntryReader</c>, where a single group can have many
    ///   entries and only a couple of fields per entry are typically read.
    /// - Readers stay <c>readonly ref struct</c> (no post-construction mutation), unlike the
    ///   discarded lazy-index-with-<c>InlineArray</c> design.
    /// </para>
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
            string r = $"{_runtimeNs}.FixSpanReader";
            var usedNames = new HashSet<string> { "_buffer", typeName };
            var fields = new List<FieldSlot>();
            var groups = new List<FixGroupRef>();

            // Recursively collect every field declared at this level, including those reached
            // through components (a component is just a named subset view over the same buffer —
            // its fields are located by the same single scan as the enclosing reader).
            CollectFieldSlots(entries, usedNames, fields);

            w.Open($"public readonly ref struct {typeName}");
            w.Line("private readonly global::System.ReadOnlySpan<byte> _buffer;");

            foreach (var slot in fields)
            {
                w.Line($"private readonly int _{slot.LocalName}Start;");
                w.Line($"private readonly int _{slot.LocalName}Length;");
                if (NeedsPresentField(slot))
                {
                    w.Line($"private readonly bool _{slot.LocalName}Present;");
                }
            }

            w.Line();
            w.Open($"public {typeName}(global::System.ReadOnlySpan<byte> buffer)");
            w.Line("_buffer = buffer;");

            foreach (var slot in fields)
            {
                w.Line($"_{slot.LocalName}Start = 0;");
                w.Line($"_{slot.LocalName}Length = 0;");
                if (NeedsPresentField(slot))
                {
                    w.Line($"_{slot.LocalName}Present = false;");
                }
            }

            if (fields.Count > 0)
            {
                w.Line();
                w.Line("int pos = 0;");
                w.Open($"while ({r}.TryReadField(buffer, pos, out int tag, out int valueStart, out int valueLength, out int nextPos))");
                w.Open("switch (tag)");
                foreach (var slot in fields)
                {
                    w.Line($"case {slot.Tag}:");
                    w.Line($"    _{slot.LocalName}Start = valueStart;");
                    w.Line($"    _{slot.LocalName}Length = valueLength;");
                    if (NeedsPresentField(slot))
                    {
                        w.Line($"    _{slot.LocalName}Present = true;");
                    }
                    w.Line("    break;");
                }
                w.Close();
                w.Line("pos = nextPos;");
                w.Close();
            }

            w.Close(); // constructor

            foreach (var slot in fields)
            {
                w.Line();
                EmitFieldMember(w, slot);
            }

            foreach (var entry in entries)
            {
                if (entry is FixComponentRef componentRef)
                {
                    w.Line();
                    EmitComponentMember(w, componentRef, usedNames);
                }
                else if (entry is FixGroupRef groupRef)
                {
                    w.Line();
                    EmitGroupMember(w, groupRef, usedNames);
                    groups.Add(groupRef);
                }
            }

            foreach (var groupRef in groups)
            {
                w.Line();
                EmitGroupTypes(w, groupRef);
            }

            w.Close();
        }

        /// <summary>A single field's schema info plus the sanitized C# identifier used for its backing slot/property.</summary>
        private readonly struct FieldSlot
        {
            public FieldSlot(FixFieldDef field, bool required, string localName)
            {
                Field = field;
                Required = required;
                LocalName = localName;
            }

            public FixFieldDef Field { get; }
            public bool Required { get; }
            public string LocalName { get; }
            public int Tag => Field.Number;
        }

        private void CollectFieldSlots(IReadOnlyList<FixEntry> entries, HashSet<string> usedNames, List<FieldSlot> into)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        var field = fieldRef.Field;
                        var translated = TypeTranslator.Translate(field.Type);
                        if (!translated.IsKnown)
                        {
                            _context.ReportUnknownFieldType(field.Name, field.Type);
                        }

                        string prop = field.Name.ToIdentifier();
                        if (usedNames.Add(prop))
                        {
                            into.Add(new FieldSlot(field, fieldRef.Required, prop));
                        }

                        break;
                    // FixComponentRef: intentionally not collected here — a component is exposed
                    // as its own independent nested reader (own buffer view, own single scan via
                    // EmitComponentMember), matching pre-existing behavior of not flattening
                    // component fields into the enclosing reader's own property list.
                    //
                    // FixGroupRef: intentionally not collected here — a group's entries live in
                    // a repeated sub-span handled by FixGroupEnumerator/EntryReader, not by this
                    // level's own scan.
                }
            }
        }

        private static bool NeedsPresentField(FieldSlot slot) => !slot.Required;

        private void EmitFieldMember(CodeWriter w, FieldSlot slot)
        {
            var field = slot.Field;
            var translated = TypeTranslator.Translate(field.Type);
            string prop = slot.LocalName;
            bool required = slot.Required;
            string r = $"{_runtimeNs}.FixSpanReader";
            string startField = $"_{prop}Start";
            string lengthField = $"_{prop}Length";
            string presentField = $"_{prop}Present";
            string valueExpr = $"_buffer.Slice({startField}, {lengthField})";

            if (FixEntryHelpers.IsEnumEligible(field))
            {
                string enumName = field.Name.ToIdentifier();
                bool isChar = translated.Category == FixTypeCategory.Char;
                string parseExpr = isChar
                    ? $"{r}.ParseByte({valueExpr})"
                    : $"{r}.ParseInt({valueExpr})";
                string tryParseExpr = isChar
                    ? $"{r}.TryParseByte({valueExpr}, out var v)"
                    : $"{r}.TryParseInt({valueExpr}, out var v)";

                if (required)
                {
                    w.Line($"public {enumName} {prop} => ({enumName}){parseExpr};");
                }
                else
                {
                    w.Line($"public {enumName}? {prop} => {presentField} && {tryParseExpr} ? ({enumName})v : ({enumName}?)null;");
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
                    w.Line($"    if (!{presentField} || !{tryParseExpr})");
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
                        w.Line($"public global::System.ReadOnlySpan<byte> {prop} => {valueExpr};");
                    }
                    else
                    {
                        w.Line($"public bool TryGet{prop}(out global::System.ReadOnlySpan<byte> value) {{ value = {valueExpr}; return {presentField}; }}");
                    }

                    break;

                case FixTypeCategory.Char:
                    EmitScalar(w, prop, required, "char", $"(char){r}.ParseByte({valueExpr})",
                        $"{presentField} && {r}.TryParseByte({valueExpr}, out var v) ? (char)v : (char?)null");
                    break;

                case FixTypeCategory.Int:
                    EmitScalar(w, prop, required, "int", $"{r}.ParseInt({valueExpr})",
                        $"{presentField} && {r}.TryParseInt({valueExpr}, out var v) ? v : (int?)null");
                    break;

                case FixTypeCategory.Decimal:
                    EmitScalar(w, prop, required, "decimal", $"{r}.ParseDecimal({valueExpr})",
                        $"{presentField} && {r}.TryParseDecimal({valueExpr}, out var v) ? v : (decimal?)null");
                    break;

                case FixTypeCategory.Bool:
                    EmitScalar(w, prop, required, "bool", $"{r}.ParseBool({valueExpr})",
                        $"{presentField} && {r}.TryParseBool({valueExpr}, out var v) ? v : (bool?)null");
                    break;

                case FixTypeCategory.DateTime:
                    EmitScalar(w, prop, required, "global::System.DateTime", $"{r}.ParseDateTime({valueExpr})",
                        $"{presentField} && {r}.TryParseDateTime({valueExpr}, out var v) ? v : (global::System.DateTime?)null");
                    break;

                case FixTypeCategory.DateOnly:
                    EmitScalar(w, prop, required, "global::System.DateOnly", $"{r}.ParseDateOnly({valueExpr})",
                        $"{presentField} && {r}.TryParseDateOnly({valueExpr}, out var v) ? v : (global::System.DateOnly?)null");
                    break;

                case FixTypeCategory.TimeOnly:
                    EmitScalar(w, prop, required, "global::System.TimeOnly", $"{r}.ParseTimeOnly({valueExpr})",
                        $"{presentField} && {r}.TryParseTimeOnly({valueExpr}, out var v) ? v : (global::System.TimeOnly?)null");
                    break;

                case FixTypeCategory.MultiValueChar:
                case FixTypeCategory.MultiValueString:
                    // Raw span accessor preserved for callers who want the unsplit wire value
                    // (e.g. to forward it verbatim), plus a typed, allocation-free enumerator over
                    // the space-delimited tokens (docs/CONTRACT.md §10 "typed
                    // MULTIPLEVALUESTRING/MULTIPLECHARVALUE parsing").
                    if (required)
                    {
                        w.Line($"public global::System.ReadOnlySpan<byte> {prop} => {valueExpr};");
                        w.Line($"public {_runtimeNs}.FixMultiValueEnumerator {prop}Values => new {_runtimeNs}.FixMultiValueEnumerator({valueExpr});");
                    }
                    else
                    {
                        w.Line($"public bool TryGet{prop}(out global::System.ReadOnlySpan<byte> value) {{ value = {valueExpr}; return {presentField}; }}");
                        w.Line($"public {_runtimeNs}.FixMultiValueEnumerator {prop}Values => new {_runtimeNs}.FixMultiValueEnumerator({presentField} ? {valueExpr} : default);");
                    }

                    break;
            }
        }

        private static void EmitScalar(CodeWriter w, string prop, bool required, string type, string requiredExpr, string optionalExpr)
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
