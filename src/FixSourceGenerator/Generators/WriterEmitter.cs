using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Generators
{
    /// <summary>Emits copy-safe, scope-oriented FIX writers per docs/CONTRACT.md §12.</summary>
    internal sealed class WriterEmitter
    {
        private readonly string _runtimeNs;

        public WriterEmitter(string runtimeNs)
        {
            _runtimeNs = runtimeNs;
        }

        public void EmitWriter(
            CodeWriter w,
            string typeName,
            FixMessageDef message,
            string beginString,
            IReadOnlyList<FixEntry> header,
            IReadOnlyList<FixEntry> trailer)
        {
            var entries = new List<FixEntry>();
            entries.AddRange(header.Where(entry => !IsAutomaticEnvelopeEntry(entry)));
            entries.AddRange(message.Entries);
            entries.AddRange(trailer.Where(entry => !IsAutomaticEnvelopeEntry(entry)));

            int requiredStateLength = 1 + GetMaximumGroupDepth(entries);
            EmitSequence(
                w,
                typeName,
                entries,
                0,
                isRoot: true,
                requiredStateLength,
                beginString,
                message.MsgType,
                new Terminal(TerminalKind.Message, string.Empty, string.Empty));
        }

        private void EmitSequence(
            CodeWriter w,
            string baseName,
            IReadOnlyList<FixEntry> entries,
            int phaseStart,
            bool isRoot,
            int requiredStateLength,
            string beginString,
            string msgType,
            Terminal terminal)
        {
            int current = ConsumeRequiredFields(entries, phaseStart);
            string phaseName = PhaseName(baseName, phaseStart);

            w.Open($"public ref struct {phaseName}");
            if (isRoot)
            {
                w.Line($"public const string MsgType = {Quote(msgType)};");
                w.Line($"public const int RequiredStateLength = {requiredStateLength.ToString(CultureInfo.InvariantCulture)};");
                w.Line();
                w.Line($"private static global::System.ReadOnlySpan<byte> BeginStringBytes => \"{beginString}\"u8;");
                w.Line($"private static global::System.ReadOnlySpan<byte> MsgTypeBytes => \"{msgType}\"u8;");
                w.Line();
            }
            w.Line($"private {_runtimeNs}.FixWriterContext _context;");
            w.Line();

            if (isRoot)
            {
                w.Line($"public static void InitializeState(global::System.Span<{_runtimeNs}.FixWriterState> state) => {_runtimeNs}.FixWriterState.Initialize(state);");
                w.Line();
                string parameters = RequiredParameterList(entries, phaseStart, current);
                w.Open($"public {phaseName}(global::System.Span<byte> destination, global::System.Span<{_runtimeNs}.FixWriterState> state{parameters})");
                w.Line($"_context = {_runtimeNs}.FixWriterContext.Begin(destination, state, RequiredStateLength, BeginStringBytes, MsgTypeBytes);");
                EmitRequiredWrites(w, entries, phaseStart, current);
                w.Close();
            }
            else
            {
                string parameters = RequiredParameterList(entries, phaseStart, current);
                w.Open($"internal {phaseName}({_runtimeNs}.FixWriterContext context{parameters})");
                w.Line("_context = context;");
                EmitRequiredWrites(w, entries, phaseStart, current);
                w.Close();
            }

            if (current < entries.Count && AllOptional(entries, current))
            {
                EmitOptionalTail(
                    w, baseName, phaseName, entries, current, requiredStateLength,
                    beginString, msgType, terminal);
                w.Close();
                return;
            }

            if (current == entries.Count)
            {
                EmitTerminal(w, terminal);
                w.Close();
                return;
            }

            if (AllOptional(entries, current))
            {
                EmitTerminal(w, terminal);
            }

            FixEntry entry = entries[current];
            int nextStart = current + 1;
            int nextCurrent = ConsumeRequiredFields(entries, nextStart);
            string nextName = PhaseName(baseName, nextStart);
            string continuationParameters = RequiredParameterList(entries, nextStart, nextCurrent);
            string continuationArguments = RequiredArgumentList(entries, nextStart, nextCurrent);

            switch (entry)
            {
                case FixFieldRef fieldRef:
                    EmitOptionalFieldTransition(w, fieldRef.Field, nextName, continuationParameters, continuationArguments);
                    break;
                case FixComponentRef componentRef:
                {
                    string childBase = componentRef.Component.Name.ToIdentifier() + "Scope" +
                        current.ToString(CultureInfo.InvariantCulture);
                    string childName = PhaseName(childBase, 0);
                    int childCurrent = ConsumeRequiredFields(componentRef.Component.Entries, 0);
                    string childParameters = RequiredParameterList(componentRef.Component.Entries, 0, childCurrent);
                    string childArguments = RequiredArgumentList(componentRef.Component.Entries, 0, childCurrent);
                    string methodStem = componentRef.Component.Name.ToIdentifier();

                    w.Line();
                    w.Open($"public {childName} Begin{methodStem}({TrimLeadingComma(childParameters)})");
                    w.Line("var context = _context.Transfer();");
                    w.Line("_context = default;");
                    w.Line($"return new {childName}(context{childArguments});");
                    w.Close();

                    if (!componentRef.Required)
                    {
                        EmitSkipTransition(w, "Skip" + methodStem, nextName, continuationParameters, continuationArguments);
                    }

                    EmitSequence(
                        w,
                        childBase,
                        componentRef.Component.Entries,
                        0,
                        isRoot: false,
                        requiredStateLength,
                        beginString,
                        msgType,
                        new Terminal(TerminalKind.Component, "End" + methodStem, nextName, continuationParameters, continuationArguments));
                    break;
                }
                case FixGroupRef groupRef:
                {
                    string groupBase = groupRef.Name.ToIdentifier() + "Scope" +
                        current.ToString(CultureInfo.InvariantCulture);
                    string groupType = groupBase + "GroupWriter";
                    string methodStem = groupRef.Name.ToIdentifier();

                    w.Line();
                    w.Open($"public {groupType} Begin{methodStem}(int expectedCount)");
                    w.Line($"_context.BeginGroup(\"{groupRef.CounterField.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, expectedCount);");
                    w.Line("var context = _context;");
                    w.Line("_context = default;");
                    w.Line($"return new {groupType}(context);");
                    w.Close();

                    if (!groupRef.Required)
                    {
                        EmitSkipTransition(w, "Skip" + methodStem, nextName, continuationParameters, continuationArguments);
                    }

                    EmitGroup(
                        w,
                        groupBase,
                        groupType,
                        groupRef,
                        nextName,
                        continuationParameters,
                        continuationArguments,
                        requiredStateLength,
                        beginString,
                        msgType);
                    break;
                }
            }

            w.Close();
            EmitSequence(w, baseName, entries, nextStart, false, requiredStateLength, beginString, msgType, terminal);
        }

        private void EmitOptionalTail(
            CodeWriter w,
            string baseName,
            string phaseName,
            IReadOnlyList<FixEntry> entries,
            int start,
            int requiredStateLength,
            string beginString,
            string msgType,
            Terminal terminal)
        {
            w.Line($"private int _order = {(start - 1).ToString(CultureInfo.InvariantCulture)};");
            w.Line();
            w.Open($"internal {phaseName}({_runtimeNs}.FixWriterContext context, int order, {_runtimeNs}.FixWriterOptionalTailMarker marker)");
            w.Line("_context = context;");
            w.Line("_order = order;");
            w.Close();

            for (int i = start; i < entries.Count; i++)
            {
                string order = i.ToString(CultureInfo.InvariantCulture);
                switch (entries[i])
                {
                    case FixFieldRef fieldRef:
                        EmitOptionalTailField(w, phaseName, fieldRef.Field, order);
                        break;
                    case FixComponentRef componentRef:
                    {
                        string stem = componentRef.Component.Name.ToIdentifier();
                        string childBase = stem + "Scope" + order;
                        string childName = PhaseName(childBase, 0);
                        int childCurrent = ConsumeRequiredFields(componentRef.Component.Entries, 0);
                        string parameters = RequiredParameterList(componentRef.Component.Entries, 0, childCurrent);
                        string arguments = RequiredArgumentList(componentRef.Component.Entries, 0, childCurrent);

                        w.Line();
                        w.Open($"public {childName} Begin{stem}({TrimLeadingComma(parameters)})");
                        EmitOrderGuard(w, order);
                        w.Line("var context = _context.Transfer();");
                        w.Line("_context = default;");
                        w.Line($"return new {childName}(context{arguments});");
                        w.Close();
                        w.Line();
                        w.Open($"public {phaseName} Skip{stem}()");
                        EmitOrderGuard(w, order);
                        w.Line("var context = _context.Transfer();");
                        w.Line("_context = default;");
                        w.Line($"return new {phaseName}(context, {order}, marker: default);");
                        w.Close();

                        EmitSequence(
                            w, childBase, componentRef.Component.Entries, 0, false,
                            requiredStateLength, beginString, msgType,
                            new Terminal(TerminalKind.Component, "End" + stem, phaseName, "", ", " + order + ", marker: default"));
                        break;
                    }
                    case FixGroupRef groupRef:
                    {
                        string stem = groupRef.Name.ToIdentifier();
                        string groupBase = stem + "Scope" + order;
                        string groupType = groupBase + "GroupWriter";
                        w.Line();
                        w.Open($"public {groupType} Begin{stem}(int expectedCount)");
                        EmitOrderGuard(w, order);
                        w.Line($"_context.BeginGroup(\"{groupRef.CounterField.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, expectedCount);");
                        w.Line("var context = _context;");
                        w.Line("_context = default;");
                        w.Line($"return new {groupType}(context);");
                        w.Close();
                        w.Line();
                        w.Open($"public {phaseName} Skip{stem}()");
                        EmitOrderGuard(w, order);
                        w.Line("var context = _context.Transfer();");
                        w.Line("_context = default;");
                        w.Line($"return new {phaseName}(context, {order}, marker: default);");
                        w.Close();
                        EmitGroup(
                            w, groupBase, groupType, groupRef, phaseName, "", ", " + order + ", marker: default",
                            requiredStateLength, beginString, msgType);
                        break;
                    }
                }
            }

            EmitTerminal(w, terminal);
        }

        private void EmitOptionalTailField(CodeWriter w, string phaseName, FixFieldDef field, string order)
        {
            string stem = field.Name.ToIdentifier();
            string value = ParameterName(field);
            string declaration = ParameterDeclaration(field, requiredInput: false);

            w.Line();
            w.Open($"public {phaseName} Write{stem}({declaration})");
            EmitOrderGuard(w, order);
            EmitValidatedWrite(w, field, value);
            w.Line("var context = _context;");
            w.Line("_context = default;");
            w.Line($"return new {phaseName}(context, {order}, marker: default);");
            w.Close();

            if (TypeTranslator.Translate(field.Type).Category == FixTypeCategory.Decimal)
            {
                w.Line();
                w.Open($"public {phaseName} Write{stem}(long {value})");
                EmitOrderGuard(w, order);
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {value});");
                w.Line("var context = _context;");
                w.Line("_context = default;");
                w.Line($"return new {phaseName}(context, {order}, marker: default);");
                w.Close();
                w.Line();
                w.Open($"public {phaseName} Write{stem}(long {value}, int scale)");
                EmitOrderGuard(w, order);
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {value}, scale);");
                w.Line("var context = _context;");
                w.Line("_context = default;");
                w.Line($"return new {phaseName}(context, {order}, marker: default);");
                w.Close();
            }

            w.Line();
            w.Open($"public {phaseName} Skip{stem}()");
            EmitOrderGuard(w, order);
            w.Line("var context = _context.Transfer();");
            w.Line("_context = default;");
            w.Line($"return new {phaseName}(context, {order}, marker: default);");
            w.Close();
        }

        private static void EmitOrderGuard(CodeWriter w, string order)
        {
            w.Line("_context.Validate();");
            w.Open($"if (_order >= {order})");
            w.Line("_context.Poison();");
            w.Line("throw new global::System.InvalidOperationException(\"A field or scope cannot be emitted twice or out of schema order.\");");
            w.Close();
        }

        private void EmitGroup(
            CodeWriter w,
            string groupBase,
            string groupType,
            FixGroupRef group,
            string parentNextName,
            string parentParameters,
            string parentArguments,
            int requiredStateLength,
            string beginString,
            string msgType)
        {
            string entryBase = groupBase + "EntryWriter";
            string entryType = PhaseName(entryBase, 0);
            IReadOnlyList<FixEntry> entryEntries = WithRequiredDelimiter(group.Entries);
            int entryCurrent = ConsumeRequiredFields(entryEntries, 0);
            string entryParameters = RequiredParameterList(entryEntries, 0, entryCurrent);
            string entryArguments = RequiredArgumentList(entryEntries, 0, entryCurrent);

            w.Line();
            w.Open($"public ref struct {groupType}");
            w.Line($"private {_runtimeNs}.FixWriterContext _context;");
            w.Line();
            w.Open($"internal {groupType}({_runtimeNs}.FixWriterContext context)");
            w.Line("_context = context;");
            w.Close();
            w.Line();
            w.Open($"public {entryType} BeginEntry({TrimLeadingComma(entryParameters)})");
            w.Line($"_context.BeginEntry({FixEntryHelpers.GetDelimiterTag(group.Entries).ToString(CultureInfo.InvariantCulture)});");
            w.Line("var context = _context;");
            w.Line("_context = default;");
            w.Line($"return new {entryType}(context{entryArguments});");
            w.Close();
            w.Line();
            w.Open($"public {parentNextName} EndGroup({TrimLeadingComma(parentParameters)})");
            w.Line("_context.EndGroup();");
            w.Line("var context = _context;");
            w.Line("_context = default;");
            w.Line($"return new {parentNextName}(context{parentArguments});");
            w.Close();
            w.Close();

            EmitSequence(
                w,
                entryBase,
                entryEntries,
                0,
                isRoot: false,
                requiredStateLength,
                beginString,
                msgType,
                new Terminal(TerminalKind.Entry, "EndEntry", groupType));
        }

        private void EmitOptionalFieldTransition(
            CodeWriter w,
            FixFieldDef field,
            string nextName,
            string continuationParameters,
            string continuationArguments)
        {
            string stem = field.Name.ToIdentifier();
            string valueParameter = ParameterDeclaration(field, requiredInput: false);
            string valueArgument = ParameterName(field);

            w.Line();
            w.Open($"public {nextName} Write{stem}({valueParameter}{continuationParameters})");
            EmitValidatedWrite(w, field, valueArgument);
            w.Line("var context = _context;");
            w.Line("_context = default;");
            w.Line($"return new {nextName}(context{continuationArguments});");
            w.Close();

            if (TypeTranslator.Translate(field.Type).Category == FixTypeCategory.Decimal)
            {
                w.Line();
                w.Open($"public {nextName} Write{stem}(long {valueArgument}{continuationParameters})");
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {valueArgument});");
                w.Line("var context = _context;");
                w.Line("_context = default;");
                w.Line($"return new {nextName}(context{continuationArguments});");
                w.Close();
                w.Line();
                w.Open($"public {nextName} Write{stem}(long {valueArgument}, int scale{continuationParameters})");
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {valueArgument}, scale);");
                w.Line("var context = _context;");
                w.Line("_context = default;");
                w.Line($"return new {nextName}(context{continuationArguments});");
                w.Close();
            }

            EmitSkipTransition(w, "Skip" + stem, nextName, continuationParameters, continuationArguments);
        }

        private void EmitSkipTransition(
            CodeWriter w,
            string method,
            string nextName,
            string continuationParameters,
            string continuationArguments)
        {
            w.Line();
            w.Open($"public {nextName} {method}({TrimLeadingComma(continuationParameters)})");
            w.Line("var context = _context.Transfer();");
            w.Line("_context = default;");
            w.Line($"return new {nextName}(context{continuationArguments});");
            w.Close();
        }

        private void EmitTerminal(CodeWriter w, Terminal terminal)
        {
            w.Line();
            switch (terminal.Kind)
            {
                case TerminalKind.Message:
                    w.Line("public int Finish() => _context.Finish();");
                    break;
                case TerminalKind.Component:
                    w.Open($"public {terminal.ReturnType} {terminal.Method}({TrimLeadingComma(terminal.Parameters)})");
                    w.Line("var context = _context.Transfer();");
                    w.Line("_context = default;");
                    w.Line($"return new {terminal.ReturnType}(context{terminal.Arguments});");
                    w.Close();
                    break;
                case TerminalKind.Entry:
                    w.Open($"public {terminal.ReturnType} {terminal.Method}()");
                    w.Line("_context.EndEntry();");
                    w.Line("var context = _context;");
                    w.Line("_context = default;");
                    w.Line($"return new {terminal.ReturnType}(context);");
                    w.Close();
                    break;
            }
        }

        private void EmitRequiredWrites(CodeWriter w, IReadOnlyList<FixEntry> entries, int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var field = ((FixFieldRef)entries[i]).Field;
                EmitValidatedWrite(w, field, ParameterName(field));
            }
        }

        private void EmitValidatedWrite(CodeWriter w, FixFieldDef field, string value)
        {
            var translated = TypeTranslator.Translate(field.Type);
            if (IsText(field))
            {
                w.Line("_context.Validate();");
                w.Open($"if ({value}.IsEmpty)");
                w.Line("_context.Poison();");
                w.Line($"throw new global::System.ArgumentException(\"An explicit {field.Name} value must not be empty.\", nameof({value}));");
                w.Close();
            }
            else if (FixEntryHelpers.IsEnumEligible(field))
            {
                w.Line("_context.Validate();");
                w.Open($"if (!{value}.IsDefined())");
                w.Line("_context.Poison();");
                w.Line($"throw new global::System.ArgumentOutOfRangeException(nameof({value}));");
                w.Close();
            }
            else if (translated.Category == FixTypeCategory.Char)
            {
                w.Line("_context.Validate();");
                w.Open($"if ({value} > 127)");
                w.Line("_context.Poison();");
                w.Line($"throw new global::System.ArgumentOutOfRangeException(nameof({value}));");
                w.Close();
            }

            string writeValue = value;
            if (FixEntryHelpers.IsEnumEligible(field))
            {
                writeValue = translated.Category == FixTypeCategory.Char ? $"(char){value}" : $"(int){value}";
            }
            w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {writeValue});");
        }

        private string RequiredParameterList(IReadOnlyList<FixEntry> entries, int start, int end)
        {
            if (start == end)
            {
                return string.Empty;
            }

            return ", " + string.Join(", ", Enumerable.Range(start, end - start)
                .Select(i => ParameterDeclaration(((FixFieldRef)entries[i]).Field, requiredInput: true)));
        }

        private static string RequiredArgumentList(IReadOnlyList<FixEntry> entries, int start, int end)
        {
            if (start == end)
            {
                return string.Empty;
            }

            return ", " + string.Join(", ", Enumerable.Range(start, end - start)
                .Select(i => ParameterName(((FixFieldRef)entries[i]).Field)));
        }

        private string ParameterDeclaration(FixFieldDef field, bool requiredInput)
        {
            string type;
            var translated = TypeTranslator.Translate(field.Type);
            if (FixEntryHelpers.IsEnumEligible(field))
            {
                type = field.Name.ToIdentifier();
            }
            else if (requiredInput && translated.Category == FixTypeCategory.Decimal)
            {
                type = _runtimeNs + ".FixDecimal";
            }
            else
            {
                type = translated.CSharpType;
            }

            string scoped = translated.Category == FixTypeCategory.Span ||
                translated.Category == FixTypeCategory.MultiValueChar ||
                translated.Category == FixTypeCategory.MultiValueString
                ? "scoped "
                : string.Empty;
            return scoped + type + " " + ParameterName(field);
        }

        private static int ConsumeRequiredFields(IReadOnlyList<FixEntry> entries, int start)
        {
            int current = start;
            while (current < entries.Count &&
                entries[current] is FixFieldRef field &&
                field.Required)
            {
                current++;
            }
            return current;
        }

        private static int GetMaximumGroupDepth(IReadOnlyList<FixEntry> entries)
        {
            int maximum = 0;
            foreach (var entry in entries)
            {
                if (entry is FixComponentRef component)
                {
                    maximum = Math.Max(maximum, GetMaximumGroupDepth(component.Component.Entries));
                }
                else if (entry is FixGroupRef group)
                {
                    maximum = Math.Max(maximum, 1 + GetMaximumGroupDepth(group.Entries));
                }
            }
            return maximum;
        }

        private static bool AllOptional(IReadOnlyList<FixEntry> entries, int start)
        {
            for (int i = start; i < entries.Count; i++)
            {
                if (entries[i].Required)
                {
                    return false;
                }
            }
            return true;
        }

        private static IReadOnlyList<FixEntry> WithRequiredDelimiter(IReadOnlyList<FixEntry> entries)
        {
            if (entries.Count == 0 || !(entries[0] is FixFieldRef delimiter) || delimiter.Required)
            {
                return entries;
            }

            var adjusted = new List<FixEntry>(entries)
            {
                [0] = new FixFieldRef(delimiter.Field, required: true),
            };
            return adjusted;
        }

        private static bool IsAutomaticEnvelopeEntry(FixEntry entry) =>
            entry is FixFieldRef field &&
            (field.Field.Number == 8 || field.Field.Number == 9 || field.Field.Number == 35 || field.Field.Number == 10);

        private static bool IsText(FixFieldDef field)
        {
            var category = TypeTranslator.Translate(field.Type).Category;
            return (category == FixTypeCategory.Span ||
                    category == FixTypeCategory.MultiValueChar ||
                    category == FixTypeCategory.MultiValueString) &&
                !field.Type.Equals("DATA", StringComparison.OrdinalIgnoreCase) &&
                !field.Type.Equals("XMLDATA", StringComparison.OrdinalIgnoreCase);
        }

        private static string ParameterName(FixFieldDef field) => "value" + field.Name.ToIdentifier().TrimStart('@');

        private static string PhaseName(string baseName, int phaseStart) =>
            phaseStart == 0 ? baseName : baseName + "Phase" + (phaseStart + 1).ToString(CultureInfo.InvariantCulture);

        private static string TrimLeadingComma(string value) =>
            value.StartsWith(", ", StringComparison.Ordinal) ? value.Substring(2) : value;

        private static string Quote(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private enum TerminalKind
        {
            Message,
            Component,
            Entry,
        }

        private sealed class Terminal
        {
            public Terminal(TerminalKind kind, string method, string returnType, string parameters = "", string arguments = "")
            {
                Kind = kind;
                Method = method;
                ReturnType = returnType;
                Parameters = parameters;
                Arguments = arguments;
            }

            public TerminalKind Kind { get; }
            public string Method { get; }
            public string ReturnType { get; }
            public string Parameters { get; }
            public string Arguments { get; }
        }
    }
}
