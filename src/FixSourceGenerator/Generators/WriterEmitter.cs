using System.Collections.Generic;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Generators
{
    /// <summary>
    /// Emits a <c>ref struct</c> writer for a message, per docs/CONTRACT.md §2.2.
    /// </summary>
    /// <remarks>
    /// Pragmatic decision: the writer flattens component/group fields into <c>Write{Field}</c>
    /// methods (in wire-declaration order) on the message writer, rather than emitting nested
    /// component/group writer ref structs. Nested writers sharing one <c>FixSpanWriter</c> position
    /// would require C# 11 <c>ref</c> fields (net7+), whereas the generated code targets net6+.
    /// The group counter (NUMINGROUP) is exposed as a normal <c>Write{Counter}(int)</c> — automatic
    /// count back-patch (docs/CONTRACT.md §6 ideal) is a documented fast-follow.
    /// Header/trailer fields (docs/CONTRACT.md §1) are flattened onto the same writer alongside the
    /// message body, in header → body → trailer order, so a single writer instance can produce a
    /// complete, valid wire message (issue #10) — BeginString/BodyLength/MsgType/CheckSum stay
    /// automatic (<c>BeginMessage</c>/<c>Finish</c>) and are never exposed as writable fields.
    /// </remarks>
    internal sealed class WriterEmitter
    {
        private readonly string _runtimeNs;

        public WriterEmitter(string runtimeNs)
        {
            _runtimeNs = runtimeNs;
        }

        public void EmitWriter(CodeWriter w, string typeName, FixMessageDef message, string beginString, IReadOnlyList<FixEntry> header, IReadOnlyList<FixEntry> trailer)
        {
            w.Open($"public ref struct {typeName}");
            w.Line($"public const string MsgType = {Quote(message.MsgType)};");
            w.Line();
            w.Line($"private static global::System.ReadOnlySpan<byte> BeginStringBytes => \"{beginString}\"u8;");
            w.Line($"private static global::System.ReadOnlySpan<byte> MsgTypeBytes => \"{message.MsgType}\"u8;");
            w.Line();
            w.Line($"private {_runtimeNs}.FixSpanWriter _writer;");
            w.Line();
            w.Open($"public {typeName}(global::System.Span<byte> destination)");
            w.Line($"_writer = new {_runtimeNs}.FixSpanWriter(destination);");
            w.Line("_writer.BeginMessage(BeginStringBytes, MsgTypeBytes);");
            w.Close();

            var used = new Dictionary<string, int>();

            // Header/trailer fields (docs/CONTRACT.md §1: "common to all messages in the
            // dictionary"). BeginString/BodyLength/MsgType/CheckSum are structural and already
            // handled automatically (BeginMessage/Finish above) — every other header/trailer field
            // (SenderCompID, TargetCompID, MsgSeqNum, SendingTime, Signature, etc.) needs an
            // explicit Write{Field} so a caller can actually produce a complete, valid wire message
            // (previously only the message body was writable — see issue #10).
            var headerFlat = new List<(FixFieldDef Field, bool IsGroupCounter)>();
            Flatten(header, headerFlat);
            foreach (var (field, isGroupCounter) in headerFlat)
            {
                if (IsAutomaticEnvelopeField(field))
                {
                    continue;
                }

                string? method = MethodName(field, used);
                if (method == null)
                {
                    continue;
                }

                w.Line();
                EmitWriteMethod(w, field, method, isGroupCounter);
            }

            var flat = new List<(FixFieldDef Field, bool IsGroupCounter)>();
            Flatten(message.Entries, flat);

            foreach (var (field, isGroupCounter) in flat)
            {
                string? method = MethodName(field, used);
                if (method == null)
                {
                    continue;
                }

                w.Line();
                EmitWriteMethod(w, field, method, isGroupCounter);
            }

            var trailerFlat = new List<(FixFieldDef Field, bool IsGroupCounter)>();
            Flatten(trailer, trailerFlat);
            foreach (var (field, isGroupCounter) in trailerFlat)
            {
                if (IsAutomaticEnvelopeField(field))
                {
                    continue;
                }

                string? method = MethodName(field, used);
                if (method == null)
                {
                    continue;
                }

                w.Line();
                EmitWriteMethod(w, field, method, isGroupCounter);
            }

            w.Line();
            w.Line("public int Finish() => _writer.Finish();");
            w.Close();
        }

        /// <summary>
        /// BeginString (8), BodyLength (9), MsgType (35) and CheckSum (10) are always emitted
        /// automatically by <c>BeginMessage</c>/<c>Finish</c> — never exposed as a writable field,
        /// even though they're formally part of the header/trailer entry lists.
        /// </summary>
        private static bool IsAutomaticEnvelopeField(FixFieldDef field) =>
            field.Number == 8 || field.Number == 9 || field.Number == 35 || field.Number == 10;

        private void EmitWriteMethod(CodeWriter w, FixFieldDef field, string method, bool isGroupCounter)
        {
            int tag = field.Number;
            var translated = TypeTranslator.Translate(field.Type);

            // Group counter (NUMINGROUP) fields are structural, not semantic values — even when a
            // real dictionary attaches documentary <value> entries to them (e.g. FIX44's NoSides:
            // "1=ONE_SIDE"/"2=BOTH_SIDES" describing what the count means, not a value domain), no
            // enum type is generated for them (see FixEntryHelpers.CollectEnumFields), so the
            // writer must always expose them as a plain int, never as an enum parameter.
            if (!isGroupCounter && FixEntryHelpers.IsEnumEligible(field))
            {
                string enumName = field.Name.ToIdentifier();
                string cast = translated.Category == FixTypeCategory.Char ? "(char)value" : "(int)value";
                w.Line($"public void {method}({enumName} value) => _writer.WriteField({tag}, {cast});");
                return;
            }

            string paramType;
            switch (translated.Category)
            {
                case FixTypeCategory.Char:
                    paramType = "char";
                    break;
                case FixTypeCategory.Int:
                    paramType = "int";
                    break;
                case FixTypeCategory.Decimal:
                    paramType = "decimal";
                    break;
                case FixTypeCategory.Bool:
                    paramType = "bool";
                    break;
                case FixTypeCategory.DateTime:
                    paramType = "global::System.DateTime";
                    break;
                case FixTypeCategory.DateOnly:
                    paramType = "global::System.DateOnly";
                    break;
                case FixTypeCategory.TimeOnly:
                    paramType = "global::System.TimeOnly";
                    break;
                default:
                    paramType = "global::System.ReadOnlySpan<byte>";
                    break;
            }

            w.Line($"public void {method}({paramType} value) => _writer.WriteField({tag}, value);");
        }

        private static void Flatten(IReadOnlyList<FixEntry> entries, List<(FixFieldDef Field, bool IsGroupCounter)> into)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        into.Add((fieldRef.Field, false));
                        break;
                    case FixComponentRef componentRef:
                        Flatten(componentRef.Component.Entries, into);
                        break;
                    case FixGroupRef groupRef:
                        into.Add((groupRef.CounterField, true));
                        Flatten(groupRef.Entries, into);
                        break;
                }
            }
        }

        private static string? MethodName(FixFieldDef field, Dictionary<string, int> used)
        {
            string stem = field.Name.ToPascalCase();
            if (stem.Length == 0)
            {
                stem = "Field";
            }

            string method = "Write" + stem;
            if (used.TryGetValue(method, out int existingTag))
            {
                if (existingTag == field.Number)
                {
                    return null; // same field already emitted
                }

                method = method + "_" + field.Number;
            }

            used[method] = field.Number;
            return method;
        }

        private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
