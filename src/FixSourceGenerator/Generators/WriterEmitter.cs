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
        private readonly string _writerNs;
        private readonly string _runtimeNs;
        private readonly HashSet<string> _usedTypeNames;
        private readonly string _extensionsTypeName;
        private readonly Dictionary<FixComponentDef, SharedDefinition> _components =
            new Dictionary<FixComponentDef, SharedDefinition>();
        private readonly Dictionary<FixGroupRef, SharedDefinition> _groups =
            new Dictionary<FixGroupRef, SharedDefinition>();
        private readonly List<SharedDefinition> _definitions = new List<SharedDefinition>();
        private readonly List<ContinuationMarker> _markers = new List<ContinuationMarker>();
        private readonly List<Closure> _closures = new List<Closure>();
        private int _definitionId;
        private int _continuationId;

        public WriterEmitter(string writerNs, string runtimeNs, FixDictionary schema)
        {
            _writerNs = writerNs;
            _runtimeNs = runtimeNs;
            _usedTypeNames = new HashSet<string>(schema.FieldsByName.Keys.Select(name => name.ToIdentifier()), StringComparer.Ordinal);
            _extensionsTypeName = AllocateTypeName("FixWriterScopeExtensions");
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

            EmitSequence(
                w,
                typeName,
                entries,
                0,
                new ScopeShape(isRoot: true, isGeneric: false),
                1 + GetMaximumGroupDepth(entries),
                beginString,
                message.MsgType,
                Terminal.Message);
        }

        public IEnumerable<(string hintName, string content)> EmitSharedSources()
        {
            for (int i = 0; i < _definitions.Count; i++)
            {
                SharedDefinition definition = _definitions[i];
                var w = CreateSource();
                if (definition.Kind == SharedDefinitionKind.Component)
                {
                    EmitSequence(
                        w,
                        definition.BaseName,
                        definition.Entries,
                        0,
                        new ScopeShape(isRoot: false, isGeneric: true),
                        0,
                        string.Empty,
                        string.Empty,
                        Terminal.Component);
                }
                else
                {
                    EmitSharedGroup(w, definition);
                }
                w.Close();
                yield return (
                    _writerNs + "." + definition.BaseName + ".Writer.g.cs",
                    w.ToString());
            }

            if (_markers.Count == 0)
            {
                yield break;
            }

            var support = CreateSource();
            foreach (ContinuationMarker marker in _markers)
            {
                support.Line(marker.IsGeneric
                    ? $"public readonly struct {marker.Name}<TOuter> {{ }}"
                    : $"public readonly struct {marker.Name} {{ }}");
            }

            support.Line();
            support.Open($"public static class {_extensionsTypeName}");
            foreach (Closure closure in _closures)
            {
                support.Line();
                string generic = closure.IsGeneric ? "<TOuter>" : string.Empty;
                string receiverType = closure.IsGeneric
                    ? closure.ReceiverType.Replace("<TContinuation>", "<TOuter>")
                    : closure.ReceiverType;
                string returnType = closure.IsGeneric
                    ? closure.ReturnType.Replace("<TContinuation>", "<TOuter>")
                    : closure.ReturnType;
                support.Open(
                    $"public static {returnType} {closure.Method}{generic}(" +
                    $"this {receiverType} child{closure.Parameters})");
                support.Line($"return new {returnType}(child.EndScope(){closure.Arguments});");
                support.Close();
            }
            support.Close();
            support.Close();
            yield return (_writerNs + ".WriterScopes.Support.g.cs", support.ToString());
        }

        private CodeWriter CreateSource()
        {
            var w = new CodeWriter();
            w.Line("// <auto-generated/>");
            w.Line("#nullable enable");
            w.Open($"namespace {_writerNs}");
            return w;
        }

        private void EmitSequence(
            CodeWriter w,
            string baseName,
            IReadOnlyList<FixEntry> entries,
            int phaseStart,
            ScopeShape shape,
            int requiredStateLength,
            string beginString,
            string msgType,
            Terminal terminal)
        {
            int current = ConsumeRequiredFields(entries, phaseStart);
            string phaseBase = PhaseName(baseName, phaseStart);
            string phaseType = TypeUse(phaseBase, shape.IsGeneric);

            w.Open($"public ref struct {phaseType}");
            if (shape.IsRoot)
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

            if (shape.IsRoot)
            {
                w.Line($"public static void InitializeState(global::System.Span<{_runtimeNs}.FixWriterState> state) => {_runtimeNs}.FixWriterState.Initialize(state);");
                w.Line();
                string parameters = RequiredParameterList(entries, phaseStart, current);
                w.Open($"public {phaseBase}(global::System.Span<byte> destination, global::System.Span<{_runtimeNs}.FixWriterState> state{parameters})");
                w.Line($"_context = {_runtimeNs}.FixWriterContext.Begin(destination, state, RequiredStateLength, BeginStringBytes, MsgTypeBytes);");
                EmitRequiredWrites(w, entries, phaseStart, current);
                w.Close();
            }
            else
            {
                string parameters = RequiredParameterList(entries, phaseStart, current);
                w.Open($"internal {phaseBase}({_runtimeNs}.FixWriterContext context{parameters})");
                w.Line("_context = context;");
                EmitRequiredWrites(w, entries, phaseStart, current);
                w.Close();
            }

            if (current < entries.Count && AllOptional(entries, current))
            {
                EmitOptionalTail(w, phaseType, entries, current, shape, terminal);
                w.Close();
                return;
            }

            if (current == entries.Count)
            {
                EmitTerminal(w, terminal);
                w.Close();
                return;
            }

            FixEntry entry = entries[current];
            int nextStart = current + 1;
            int nextCurrent = ConsumeRequiredFields(entries, nextStart);
            string nextType = TypeUse(PhaseName(baseName, nextStart), shape.IsGeneric);
            string continuationParameters = RequiredParameterList(entries, nextStart, nextCurrent);
            string continuationArguments = RequiredArgumentList(entries, nextStart, nextCurrent);

            EmitScopeEntry(
                w,
                entry,
                current,
                nextType,
                continuationParameters,
                continuationArguments,
                shape,
                optionalTail: false);

            w.Close();
            EmitSequence(
                w,
                baseName,
                entries,
                nextStart,
                new ScopeShape(isRoot: false, isGeneric: shape.IsGeneric),
                requiredStateLength,
                beginString,
                msgType,
                terminal);
        }

        private void EmitOptionalTail(
            CodeWriter w,
            string phaseType,
            IReadOnlyList<FixEntry> entries,
            int start,
            ScopeShape shape,
            Terminal terminal)
        {
            w.Line($"private int _order = {(start - 1).ToString(CultureInfo.InvariantCulture)};");
            w.Line();
            w.Open($"internal {ConstructorName(phaseType)}({_runtimeNs}.FixWriterContext context, int order, {_runtimeNs}.FixWriterOptionalTailMarker marker)");
            w.Line("_context = context;");
            w.Line("_order = order;");
            w.Close();

            for (int i = start; i < entries.Count; i++)
            {
                if (entries[i] is FixFieldRef field)
                {
                    EmitOptionalTailField(w, phaseType, field.Field, i);
                    continue;
                }

                EmitScopeEntry(
                    w,
                    entries[i],
                    i,
                    phaseType,
                    string.Empty,
                    ", " + i.ToString(CultureInfo.InvariantCulture) + ", marker: default",
                    shape,
                    optionalTail: true);
            }

            EmitTerminal(w, terminal);
        }

        private void EmitScopeEntry(
            CodeWriter w,
            FixEntry entry,
            int order,
            string parentNextType,
            string continuationParameters,
            string continuationArguments,
            ScopeShape parentShape,
            bool optionalTail)
        {
            string ordinal = order.ToString(CultureInfo.InvariantCulture);
            switch (entry)
            {
                case FixFieldRef field:
                    EmitOptionalFieldTransition(
                        w,
                        field.Field,
                        parentNextType,
                        continuationParameters,
                        continuationArguments);
                    return;

                case FixComponentRef component:
                {
                    SharedDefinition definition = RegisterComponent(component.Component);
                    ContinuationMarker marker = RegisterContinuation(parentShape.IsGeneric);
                    string markerType = marker.TypeUse(parentShape.IsGeneric);
                    string childType = TypeUse(definition.BaseName, isGeneric: true, markerType);
                    string childTerminal = TypeUse(definition.TerminalBaseName, isGeneric: true, markerType);
                    string stem = component.Component.Name.ToIdentifier();
                    int childCurrent = ConsumeRequiredFields(component.Component.Entries, 0);
                    string parameters = RequiredParameterList(component.Component.Entries, 0, childCurrent);
                    string arguments = RequiredArgumentList(component.Component.Entries, 0, childCurrent);

                    w.Line();
                    w.Open($"public {childType} Begin{stem}({TrimLeadingComma(parameters)})");
                    if (optionalTail)
                    {
                        EmitOrderGuard(w, ordinal);
                    }
                    w.Line($"return new {childType}(_context.Transfer(){arguments});");
                    w.Close();

                    if (!component.Required)
                    {
                        if (optionalTail)
                        {
                            w.Line();
                            w.Open($"public {parentNextType} Skip{stem}()");
                            EmitOrderGuard(w, ordinal);
                            w.Line($"return new {parentNextType}(_context.Transfer(){continuationArguments});");
                            w.Close();
                        }
                        else
                        {
                            EmitSkipTransition(w, "Skip" + stem, parentNextType, continuationParameters, continuationArguments);
                        }
                    }

                    RegisterClosure(
                        marker,
                        "End" + stem,
                        childTerminal,
                        parentNextType,
                        continuationParameters,
                        continuationArguments,
                        parentShape.IsGeneric);
                    return;
                }

                case FixGroupRef group:
                {
                    SharedDefinition definition = RegisterGroup(group);
                    ContinuationMarker marker = RegisterContinuation(parentShape.IsGeneric);
                    string markerType = marker.TypeUse(parentShape.IsGeneric);
                    string groupType = TypeUse(definition.BaseName, isGeneric: true, markerType);
                    string stem = group.Name.ToIdentifier();

                    w.Line();
                    w.Open($"public {groupType} Begin{stem}(int expectedCount)");
                    if (optionalTail)
                    {
                        EmitOrderGuard(w, ordinal);
                    }
                    w.Line($"_context.BeginGroup(\"{group.CounterField.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, expectedCount);");
                    w.Line($"return new {groupType}(_context.Take());");
                    w.Close();

                    if (!group.Required)
                    {
                        if (optionalTail)
                        {
                            w.Line();
                            w.Open($"public {parentNextType} Skip{stem}()");
                            EmitOrderGuard(w, ordinal);
                            w.Line($"return new {parentNextType}(_context.Transfer(){continuationArguments});");
                            w.Close();
                        }
                        else
                        {
                            EmitSkipTransition(w, "Skip" + stem, parentNextType, continuationParameters, continuationArguments);
                        }
                    }

                    RegisterClosure(
                        marker,
                        "EndGroup",
                        groupType,
                        parentNextType,
                        continuationParameters,
                        continuationArguments,
                        parentShape.IsGeneric);
                    return;
                }
            }
        }

        private void EmitSharedGroup(CodeWriter w, SharedDefinition definition)
        {
            FixGroupRef group = definition.Group!;
            string groupType = TypeUse(definition.BaseName, isGeneric: true);
            IReadOnlyList<FixEntry> entryEntries = WithRequiredDelimiter(group.Entries);
            int entryCurrent = ConsumeRequiredFields(entryEntries, 0);
            string entryParameters = RequiredParameterList(entryEntries, 0, entryCurrent);
            string entryArguments = RequiredArgumentList(entryEntries, 0, entryCurrent);
            string entryType = TypeUse(definition.EntryBaseName!, isGeneric: true);

            w.Open($"public ref struct {groupType}");
            w.Line($"private {_runtimeNs}.FixWriterContext _context;");
            w.Line();
            w.Open($"internal {definition.BaseName}({_runtimeNs}.FixWriterContext context)");
            w.Line("_context = context;");
            w.Close();
            w.Line();
            w.Open($"public {entryType} BeginEntry({TrimLeadingComma(entryParameters)})");
            w.Line($"_context.BeginEntry({FixEntryHelpers.GetDelimiterTag(group.Entries).ToString(CultureInfo.InvariantCulture)});");
            w.Line($"return new {entryType}(_context.Take(){entryArguments});");
            w.Close();
            w.Line();
            w.Open($"internal {_runtimeNs}.FixWriterContext EndScope()");
            w.Line("_context.EndGroup();");
            w.Line("return _context.Take();");
            w.Close();
            w.Close();

            EmitSequence(
                w,
                definition.EntryBaseName!,
                entryEntries,
                0,
                new ScopeShape(isRoot: false, isGeneric: true),
                0,
                string.Empty,
                string.Empty,
                Terminal.Entry(groupType));
        }

        private void EmitOptionalTailField(CodeWriter w, string phaseType, FixFieldDef field, int order)
        {
            string stem = field.Name.ToIdentifier();
            string value = ParameterName(field);
            string declaration = ParameterDeclaration(field, requiredInput: false);
            string ordinal = order.ToString(CultureInfo.InvariantCulture);

            w.Line();
            w.Open($"public {phaseType} Write{stem}({declaration})");
            EmitOrderGuard(w, ordinal);
            EmitValidatedWrite(w, field, value);
            w.Line($"return new {phaseType}(_context.Take(), {ordinal}, marker: default);");
            w.Close();

            if (TypeTranslator.Translate(field.Type).Category == FixTypeCategory.Decimal)
            {
                w.Line();
                w.Open($"public {phaseType} Write{stem}(long {value})");
                EmitOrderGuard(w, ordinal);
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {value});");
                w.Line($"return new {phaseType}(_context.Take(), {ordinal}, marker: default);");
                w.Close();
                w.Line();
                w.Open($"public {phaseType} Write{stem}(long {value}, int scale)");
                EmitOrderGuard(w, ordinal);
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {value}, scale);");
                w.Line($"return new {phaseType}(_context.Take(), {ordinal}, marker: default);");
                w.Close();
            }

            w.Line();
            w.Open($"public {phaseType} Skip{stem}()");
            EmitOrderGuard(w, ordinal);
            w.Line($"return new {phaseType}(_context.Transfer(), {ordinal}, marker: default);");
            w.Close();
        }

        private static void EmitOrderGuard(CodeWriter w, string order)
        {
            w.Line($"_context.ValidateOrder(_order, {order});");
        }

        private void EmitOptionalFieldTransition(
            CodeWriter w,
            FixFieldDef field,
            string nextType,
            string continuationParameters,
            string continuationArguments)
        {
            string stem = field.Name.ToIdentifier();
            string valueParameter = ParameterDeclaration(field, requiredInput: false);
            string valueArgument = ParameterName(field);

            w.Line();
            w.Open($"public {nextType} Write{stem}({valueParameter}{continuationParameters})");
            EmitValidatedWrite(w, field, valueArgument);
            w.Line($"return new {nextType}(_context.Take(){continuationArguments});");
            w.Close();

            if (TypeTranslator.Translate(field.Type).Category == FixTypeCategory.Decimal)
            {
                w.Line();
                w.Open($"public {nextType} Write{stem}(long {valueArgument}{continuationParameters})");
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {valueArgument});");
                w.Line($"return new {nextType}(_context.Take(){continuationArguments});");
                w.Close();
                w.Line();
                w.Open($"public {nextType} Write{stem}(long {valueArgument}, int scale{continuationParameters})");
                w.Line($"_context.WriteField(\"{field.Number.ToString(CultureInfo.InvariantCulture)}=\"u8, {valueArgument}, scale);");
                w.Line($"return new {nextType}(_context.Take(){continuationArguments});");
                w.Close();
            }

            EmitSkipTransition(w, "Skip" + stem, nextType, continuationParameters, continuationArguments);
        }

        private void EmitSkipTransition(
            CodeWriter w,
            string method,
            string nextType,
            string continuationParameters,
            string continuationArguments)
        {
            w.Line();
            w.Open($"public {nextType} {method}({TrimLeadingComma(continuationParameters)})");
            w.Line($"return new {nextType}(_context.Transfer(){continuationArguments});");
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
                    w.Open($"internal {_runtimeNs}.FixWriterContext EndScope()");
                    w.Line("return _context.Transfer();");
                    w.Close();
                    break;
                case TerminalKind.Entry:
                    w.Open($"public {terminal.ReturnType} EndEntry()");
                    w.Line("_context.EndEntry();");
                    w.Line($"return new {terminal.ReturnType}(_context.Take());");
                    w.Close();
                    break;
            }
        }

        private SharedDefinition RegisterComponent(FixComponentDef component)
        {
            if (_components.TryGetValue(component, out SharedDefinition? existing))
            {
                return existing;
            }

            string baseName = component.Name.ToIdentifier() + "WriterScope" +
                (++_definitionId).ToString(CultureInfo.InvariantCulture);
            var definition = SharedDefinition.Component(
                baseName,
                component.Entries,
                PhaseName(baseName, FindTerminalPhase(component.Entries)));
            _components.Add(component, definition);
            _definitions.Add(definition);
            return definition;
        }

        private SharedDefinition RegisterGroup(FixGroupRef group)
        {
            if (_groups.TryGetValue(group, out SharedDefinition? existing))
            {
                return existing;
            }

            string baseName = group.Name.ToIdentifier() + "WriterGroup" +
                (++_definitionId).ToString(CultureInfo.InvariantCulture);
            var definition = SharedDefinition.GroupDefinition(
                baseName,
                baseName + "Entry",
                group);
            _groups.Add(group, definition);
            _definitions.Add(definition);
            return definition;
        }

        private ContinuationMarker RegisterContinuation(bool generic)
        {
            var marker = new ContinuationMarker(
                AllocateTypeName("FixWriterContinuation" + (++_continuationId).ToString(CultureInfo.InvariantCulture)),
                generic);
            _markers.Add(marker);
            return marker;
        }

        private string AllocateTypeName(string name)
        {
            while (!_usedTypeNames.Add(name))
            {
                name += "_";
            }
            return name;
        }

        private void RegisterClosure(
            ContinuationMarker marker,
            string method,
            string receiverType,
            string returnType,
            string parameters,
            string arguments,
            bool generic)
        {
            _closures.Add(new Closure(
                marker,
                method,
                receiverType,
                returnType,
                parameters,
                arguments,
                generic));
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
                w.Line($"_context.ValidateText({value}, {Quote(field.Name)}, nameof({value}));");
            }
            else if (FixEntryHelpers.IsEnumEligible(field))
            {
                w.Line($"_context.ValidateCode({value}.IsDefined(), nameof({value}));");
            }
            else if (translated.Category == FixTypeCategory.Char)
            {
                w.Line($"_context.ValidateCode({value} <= 127, nameof({value}));");
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
                type = "global::" + _writerNs + "." + field.Name.ToIdentifier();
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

        private static int FindTerminalPhase(IReadOnlyList<FixEntry> entries)
        {
            int phaseStart = 0;
            while (true)
            {
                int current = ConsumeRequiredFields(entries, phaseStart);
                if (current == entries.Count || AllOptional(entries, current))
                {
                    return phaseStart;
                }
                phaseStart = current + 1;
            }
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

        private static string TypeUse(string baseName, bool isGeneric, string genericArgument = "TContinuation") =>
            isGeneric ? baseName + "<" + genericArgument + ">" : baseName;

        private static string ConstructorName(string typeUse)
        {
            int generic = typeUse.IndexOf('<');
            return generic < 0 ? typeUse : typeUse.Substring(0, generic);
        }

        private static string TrimLeadingComma(string value) =>
            value.StartsWith(", ", StringComparison.Ordinal) ? value.Substring(2) : value;

        private static string Quote(string value) =>
            "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private readonly struct ScopeShape
        {
            public ScopeShape(bool isRoot, bool isGeneric)
            {
                IsRoot = isRoot;
                IsGeneric = isGeneric;
            }

            public bool IsRoot { get; }
            public bool IsGeneric { get; }
        }

        private enum TerminalKind
        {
            Message,
            Component,
            Entry,
        }

        private sealed class Terminal
        {
            private Terminal(TerminalKind kind, string returnType)
            {
                Kind = kind;
                ReturnType = returnType;
            }

            public static Terminal Message { get; } = new Terminal(TerminalKind.Message, string.Empty);
            public static Terminal Component { get; } = new Terminal(TerminalKind.Component, string.Empty);
            public static Terminal Entry(string returnType) => new Terminal(TerminalKind.Entry, returnType);

            public TerminalKind Kind { get; }
            public string ReturnType { get; }
        }

        private enum SharedDefinitionKind
        {
            Component,
            Group,
        }

        private sealed class SharedDefinition
        {
            private SharedDefinition(
                SharedDefinitionKind kind,
                string baseName,
                IReadOnlyList<FixEntry> entries,
                string terminalBaseName,
                string? entryBaseName,
                FixGroupRef? group)
            {
                Kind = kind;
                BaseName = baseName;
                Entries = entries;
                TerminalBaseName = terminalBaseName;
                EntryBaseName = entryBaseName;
                Group = group;
            }

            public static SharedDefinition Component(
                string baseName,
                IReadOnlyList<FixEntry> entries,
                string terminalBaseName) =>
                new SharedDefinition(SharedDefinitionKind.Component, baseName, entries, terminalBaseName, null, null);

            public static SharedDefinition GroupDefinition(string baseName, string entryBaseName, FixGroupRef group) =>
                new SharedDefinition(SharedDefinitionKind.Group, baseName, group.Entries, string.Empty, entryBaseName, group);

            public SharedDefinitionKind Kind { get; }
            public string BaseName { get; }
            public IReadOnlyList<FixEntry> Entries { get; }
            public string TerminalBaseName { get; }
            public string? EntryBaseName { get; }
            public FixGroupRef? Group { get; }
        }

        private sealed class ContinuationMarker
        {
            public ContinuationMarker(string name, bool isGeneric)
            {
                Name = name;
                IsGeneric = isGeneric;
            }

            public string Name { get; }
            public bool IsGeneric { get; }
            public string TypeUse(bool generic) => generic ? Name + "<TContinuation>" : Name;
        }

        private sealed class Closure
        {
            public Closure(
                ContinuationMarker marker,
                string method,
                string receiverType,
                string returnType,
                string parameters,
                string arguments,
                bool isGeneric)
            {
                Marker = marker;
                Method = method;
                ReceiverType = receiverType;
                ReturnType = returnType;
                Parameters = parameters;
                Arguments = arguments;
                IsGeneric = isGeneric;
            }

            public ContinuationMarker Marker { get; }
            public string Method { get; }
            public string ReceiverType { get; }
            public string ReturnType { get; }
            public string Parameters { get; }
            public string Arguments { get; }
            public bool IsGeneric { get; }
        }
    }
}
