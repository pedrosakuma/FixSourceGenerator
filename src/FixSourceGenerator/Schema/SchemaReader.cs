using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using FixSourceGenerator.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace FixSourceGenerator.Schema
{
    /// <summary>
    /// Parses a QuickFIX-style DataDictionary XML document (docs/CONTRACT.md §1) into a fully
    /// resolved <see cref="FixDictionary"/> model. All &lt;field&gt;/&lt;component&gt;/&lt;group&gt;
    /// references are resolved against &lt;fields&gt;/&lt;components&gt; at parse time, so downstream
    /// codegen never has to look up a name — it only walks an already-resolved object graph.
    /// </summary>
    public static class SchemaReader
    {
        /// <summary>
        /// Parses <paramref name="xmlContent"/> into a <see cref="FixDictionary"/>.
        /// Returns null only when the document cannot be parsed at all (e.g. not well-formed XML,
        /// or missing the &lt;fix&gt; root) — in that case a FIX002 diagnostic has already been
        /// reported via <paramref name="reportDiagnostic"/>. Recoverable problems (duplicate
        /// definitions, unresolved references, unknown field types, ...) are reported as
        /// diagnostics but do not prevent a (possibly partial) model from being returned, so a
        /// single malformed message/field doesn't abort generation for the rest of the schema.
        /// </summary>
        /// <param name="xmlContent">The raw schema XML text.</param>
        /// <param name="schemaPath">
        /// The AdditionalFile path used both in FIX002's message and as the file path of every
        /// reported <see cref="Location"/>.
        /// </param>
        /// <param name="reportDiagnostic">Sink for every diagnostic raised while parsing.</param>
        public static FixDictionary? Parse(string xmlContent, string schemaPath, Action<Diagnostic> reportDiagnostic) =>
            Parse(xmlContent, schemaPath, reportDiagnostic, SourceText.From(xmlContent));

        internal static FixDictionary? Parse(string xmlContent, string schemaPath, Action<Diagnostic> reportDiagnostic, SourceText sourceText)
        {
            var context = new ParseContext(schemaPath, sourceText, reportDiagnostic);

            XDocument document;
            try
            {
                document = XDocument.Parse(xmlContent, LoadOptions.SetLineInfo);
            }
            catch (XmlException ex)
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MalformedSchema, context.GetLocation(ex.LineNumber, ex.LinePosition, 0), schemaPath, ex.Message));
                return null;
            }

            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "fix", StringComparison.OrdinalIgnoreCase))
            {
                context.Report(FixDiagnostics.MalformedSchema, root, schemaPath, "missing <fix> root element");
                return null;
            }

            string? majorText = (string?)root.Attribute("major");
            string? minorText = (string?)root.Attribute("minor");
            if (majorText == null)
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, root, "fix", "major");
            }
            else if (!int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                context.Report(FixDiagnostics.InvalidAttributeValue, root.Attribute("major"), "fix", "major", majorText, "an integer");
            }
            if (minorText == null)
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, root, "fix", "minor");
            }
            else if (!int.TryParse(minorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                context.Report(FixDiagnostics.InvalidAttributeValue, root.Attribute("minor"), "fix", "minor", minorText, "an integer");
            }

            string? servicePackText = (string?)root.Attribute("servicepack");
            if (servicePackText != null && !int.TryParse(servicePackText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                context.Report(FixDiagnostics.InvalidAttributeValue, root.Attribute("servicepack"), "fix", "servicepack", servicePackText, "an integer");
            }

            int major = ParseIntOrDefault(majorText, 0);
            int minor = ParseIntOrDefault(minorText, 0);
            int servicePack = ParseIntOrDefault(servicePackText, 0);
            string fixType = (string?)root.Attribute("type") ?? "FIX";

            // ---- Stage 1: <fields> — must exist before anything else can be resolved. ----
            var fieldsByNumber = new Dictionary<int, FixFieldDef>();
            var fieldsByName = new Dictionary<string, FixFieldDef>(StringComparer.Ordinal);

            var fieldsSection = root.Element("fields");
            if (fieldsSection != null)
            {
                foreach (var fieldEl in fieldsSection.Elements("field"))
                {
                    ParseFieldDefinition(fieldEl, fieldsByNumber, fieldsByName, context);
                }
            }

            // ---- Stage 2: <components> — resolved recursively, with cycle detection. ----
            var componentElementsByName = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var componentsSection = root.Element("components");
            if (componentsSection != null)
            {
                foreach (var componentEl in componentsSection.Elements("component"))
                {
                    string? name = (string?)componentEl.Attribute("name");
                    if (string.IsNullOrEmpty(name))
                    {
                        context.Report(FixDiagnostics.MissingRequiredAttribute, componentEl, "component", "name");
                        continue;
                    }

                    if (componentElementsByName.ContainsKey(name!))
                    {
                        context.Report(FixDiagnostics.DuplicateDefinition, componentEl.Attribute("name"), "component", name);
                        continue;
                    }

                    componentElementsByName[name!] = componentEl;
                }
            }

            var componentsByName = new Dictionary<string, FixComponentDef>(StringComparer.Ordinal);
            var componentResolutionState = new Dictionary<string, ResolutionState>(StringComparer.Ordinal);

            foreach (var name in componentElementsByName.Keys.ToArray())
            {
                ResolveComponent(name, componentElementsByName, componentsByName, componentResolutionState, fieldsByName, context);
            }

            // ---- Stage 3: header / trailer / messages — resolved against fields+components. ----
            var header = ParseEntries(root.Element("header"), fieldsByName, componentsByName, context, new SchemaOwner("header", "header"));
            var trailer = ParseEntries(root.Element("trailer"), fieldsByName, componentsByName, context, new SchemaOwner("trailer", "trailer"));

            var messages = new List<FixMessageDef>();
            var messageNames = new HashSet<string>(StringComparer.Ordinal);
            var messageTypes = new HashSet<string>(StringComparer.Ordinal);

            var messagesSection = root.Element("messages");
            if (messagesSection != null)
            {
                foreach (var messageEl in messagesSection.Elements("message"))
                {
                    string? name = (string?)messageEl.Attribute("name");
                    string? msgType = (string?)messageEl.Attribute("msgtype");
                    string? msgCat = (string?)messageEl.Attribute("msgcat");

                    if (string.IsNullOrEmpty(name))
                    {
                        context.Report(FixDiagnostics.MissingRequiredAttribute, messageEl, "message", "name");
                        continue;
                    }
                    if (string.IsNullOrEmpty(msgType))
                    {
                        context.Report(FixDiagnostics.MissingRequiredAttribute, messageEl, "message", "msgtype");
                        continue;
                    }

                    if (!messageNames.Add(name!))
                    {
                        context.Report(FixDiagnostics.DuplicateDefinition, messageEl.Attribute("name"), "message name", name);
                        continue;
                    }
                    if (!messageTypes.Add(msgType!))
                    {
                        context.Report(FixDiagnostics.DuplicateDefinition, messageEl.Attribute("msgtype"), "message msgtype", msgType);
                        continue;
                    }

                    var entries = ParseEntries(messageEl, fieldsByName, componentsByName, context, new SchemaOwner("message", name!));
                    messages.Add(new FixMessageDef(name!, msgType!, msgCat, entries));
                }
            }

            return new FixDictionary(
                fixType,
                major,
                minor,
                servicePack,
                header,
                trailer,
                messages,
                componentsByName,
                fieldsByName,
                fieldsByNumber);
        }

        private static void ParseFieldDefinition(
            XElement fieldEl,
            Dictionary<int, FixFieldDef> fieldsByNumber,
            Dictionary<string, FixFieldDef> fieldsByName,
            ParseContext context)
        {
            string? numberText = (string?)fieldEl.Attribute("number");
            string? name = (string?)fieldEl.Attribute("name");
            string? type = (string?)fieldEl.Attribute("type");

            bool missingAttr = false;
            if (string.IsNullOrEmpty(numberText))
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, fieldEl, "field", "number");
                missingAttr = true;
            }
            if (string.IsNullOrEmpty(name))
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, fieldEl, "field", "name");
                missingAttr = true;
            }
            if (string.IsNullOrEmpty(type))
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, fieldEl, "field", "type");
                missingAttr = true;
            }

            if (missingAttr)
            {
                return;
            }

            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                context.Report(FixDiagnostics.InvalidAttributeValue, fieldEl.Attribute("number"), "field", "number", numberText, "an integer");
                return;
            }

            var values = fieldEl.Elements("value")
                .Select(valueEl => new FixValueDef((string?)valueEl.Attribute("enum") ?? string.Empty, (string?)valueEl.Attribute("description") ?? string.Empty))
                .ToList();

            var fieldDef = new FixFieldDef(number, name!, type!, values);

            if (fieldsByNumber.ContainsKey(number))
            {
                context.Report(FixDiagnostics.DuplicateDefinition, fieldEl.Attribute("number"), "field number", number.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                fieldsByNumber[number] = fieldDef;
            }

            if (fieldsByName.ContainsKey(name!))
            {
                context.Report(FixDiagnostics.DuplicateDefinition, fieldEl.Attribute("name"), "field name", name);
            }
            else
            {
                fieldsByName[name!] = fieldDef;
            }
        }

        private enum ResolutionState
        {
            InProgress,
            Resolved
        }

        /// <summary>Identifies the schema element (header/trailer/message/component/group) that owns a field/component reference, for use in FIX005's "{0} '{1}' references undefined {2} '{3}'" message.</summary>
        private readonly struct SchemaOwner
        {
            public SchemaOwner(string kind, string name)
            {
                Kind = kind;
                Name = name;
            }

            public string Kind { get; }

            public string Name { get; }
        }

        private static FixComponentDef? ResolveComponent(
            string name,
            Dictionary<string, XElement> componentElementsByName,
            Dictionary<string, FixComponentDef> componentsByName,
            Dictionary<string, ResolutionState> resolutionState,
            Dictionary<string, FixFieldDef> fieldsByName,
            ParseContext context)
        {
            if (componentsByName.TryGetValue(name, out var resolved))
            {
                return resolved;
            }

            if (!componentElementsByName.TryGetValue(name, out var componentEl))
            {
                // Unresolved forward reference — the caller (ParseEntries) reports FIX005 when a
                // <component> reference doesn't resolve; this path is hit only from recursive
                // component-to-component references, handled the same way there.
                return null;
            }

            if (resolutionState.TryGetValue(name, out var state) && state == ResolutionState.InProgress)
            {
                context.Report(FixDiagnostics.CircularComponentReference, componentEl.Attribute("name") ?? (XObject)componentEl, name);
                return null;
            }

            resolutionState[name] = ResolutionState.InProgress;

            var owner = new SchemaOwner("component", name);
            var entries = ParseEntries(
                componentEl,
                fieldsByName,
                componentsByName,
                context,
                owner,
                nestedComponentResolver: refName => ResolveComponent(refName, componentElementsByName, componentsByName, resolutionState, fieldsByName, context));

            var def = new FixComponentDef(name, entries);
            componentsByName[name] = def;
            resolutionState[name] = ResolutionState.Resolved;
            return def;
        }

        /// <summary>
        /// Parses the field/component/group children of <paramref name="container"/> (a
        /// header/trailer/message/component/group element) into a resolved, ordered list of
        /// <see cref="FixEntry"/>. Order is preserved because FIX field ordering is wire-significant.
        /// </summary>
        private static List<FixEntry> ParseEntries(
            XElement? container,
            Dictionary<string, FixFieldDef> fieldsByName,
            Dictionary<string, FixComponentDef> componentsByName,
            ParseContext context,
            SchemaOwner owner,
            Func<string, FixComponentDef?>? nestedComponentResolver = null)
        {
            var entries = new List<FixEntry>();
            if (container == null)
            {
                return entries;
            }

            foreach (var child in container.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "field":
                        entries.Add(ParseFieldRef(child, fieldsByName, owner, context));
                        break;

                    case "component":
                        entries.Add(ParseComponentRef(child, componentsByName, owner, context, nestedComponentResolver));
                        break;

                    case "group":
                        entries.Add(ParseGroupRef(child, fieldsByName, componentsByName, owner, context, nestedComponentResolver));
                        break;

                    default:
                        context.Report(
                            FixDiagnostics.UnsupportedConstruct,
                            child,
                            $"Unrecognized element <{child.Name.LocalName}> inside <{container.Name.LocalName}> is ignored");
                        break;
                }
            }

            return entries.Where(e => e != null).ToList()!;
        }

        private static FixEntry ParseFieldRef(
            XElement fieldRefEl,
            Dictionary<string, FixFieldDef> fieldsByName,
            SchemaOwner owner,
            ParseContext context)
        {
            string? name = (string?)fieldRefEl.Attribute("name");
            bool required = IsRequired(fieldRefEl);

            if (string.IsNullOrEmpty(name))
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, fieldRefEl, "field", "name");
                return new FixFieldRef(UnknownField, required);
            }

            if (!fieldsByName.TryGetValue(name!, out var fieldDef))
            {
                context.Report(FixDiagnostics.UnresolvedReference, fieldRefEl.Attribute("name"), owner.Kind, owner.Name, "field", name);
                return new FixFieldRef(new FixFieldDef(0, name!, "STRING", Array.Empty<FixValueDef>()), required);
            }

            return new FixFieldRef(fieldDef, required);
        }

        private static readonly FixFieldDef UnknownField = new FixFieldDef(0, "Unknown", "STRING", Array.Empty<FixValueDef>());

        private static FixEntry ParseComponentRef(
            XElement componentRefEl,
            Dictionary<string, FixComponentDef> componentsByName,
            SchemaOwner owner,
            ParseContext context,
            Func<string, FixComponentDef?>? nestedComponentResolver)
        {
            string? name = (string?)componentRefEl.Attribute("name");
            bool required = IsRequired(componentRefEl);

            if (string.IsNullOrEmpty(name))
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, componentRefEl, "component", "name");
                return new FixComponentRef(new FixComponentDef("Unknown", Array.Empty<FixEntry>()), required);
            }

            FixComponentDef? componentDef = null;
            if (!componentsByName.TryGetValue(name!, out componentDef))
            {
                componentDef = nestedComponentResolver?.Invoke(name!);
            }

            if (componentDef == null)
            {
                context.Report(FixDiagnostics.UnresolvedReference, componentRefEl.Attribute("name"), owner.Kind, owner.Name, "component", name);
                componentDef = new FixComponentDef(name!, Array.Empty<FixEntry>());
            }

            return new FixComponentRef(componentDef, required);
        }

        private static FixEntry ParseGroupRef(
            XElement groupEl,
            Dictionary<string, FixFieldDef> fieldsByName,
            Dictionary<string, FixComponentDef> componentsByName,
            SchemaOwner owner,
            ParseContext context,
            Func<string, FixComponentDef?>? nestedComponentResolver)
        {
            string? name = (string?)groupEl.Attribute("name");
            bool required = IsRequired(groupEl);

            if (string.IsNullOrEmpty(name))
            {
                context.Report(FixDiagnostics.MissingRequiredAttribute, groupEl, "group", "name");
                name = "Unknown";
            }

            if (!fieldsByName.TryGetValue(name!, out var counterField))
            {
                context.Report(FixDiagnostics.MissingGroupCounterField, groupEl.Attribute("name") ?? (XObject)groupEl, name);
                counterField = new FixFieldDef(0, name!, "NUMINGROUP", Array.Empty<FixValueDef>());
            }

            var groupOwner = new SchemaOwner("group", name!);
            var entries = ParseEntries(groupEl, fieldsByName, componentsByName, context, groupOwner, nestedComponentResolver);
            return new FixGroupRef(name!, counterField, entries, required);
        }

        private static bool IsRequired(XElement element)
        {
            string? required = (string?)element.Attribute("required");
            return string.Equals(required, "Y", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseIntOrDefault(string? text, int defaultValue)
        {
            if (string.IsNullOrEmpty(text))
            {
                return defaultValue;
            }

            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : defaultValue;
        }

        /// <summary>
        /// Maps XML positions back to the original schema text.
        /// </summary>
        private sealed class ParseContext
        {
            private readonly Action<Diagnostic> _reportDiagnostic;

            public ParseContext(string schemaPath, SourceText sourceText, Action<Diagnostic> reportDiagnostic)
            {
                SchemaPath = schemaPath;
                SourceText = sourceText;
                _reportDiagnostic = reportDiagnostic;
            }

            public string SchemaPath { get; }

            public SourceText SourceText { get; }

            /// <summary>
            /// Reports a diagnostic located at <paramref name="locationSource"/> (an <see cref="XElement"/>
            /// or <see cref="XAttribute"/> carrying line info from <see cref="LoadOptions.SetLineInfo"/>).
            /// Falls back to <see cref="Location.None"/> when the node has no line info.
            /// </summary>
            public void Report(DiagnosticDescriptor descriptor, XObject? locationSource, params object?[] messageArgs)
            {
                _reportDiagnostic(Diagnostic.Create(descriptor, GetLocation(locationSource), messageArgs));
            }

            private Location GetLocation(XObject? node)
            {
                if (node is not IXmlLineInfo lineInfo || !lineInfo.HasLineInfo())
                {
                    return Location.None;
                }

                int length = node is XElement element ? element.Name.LocalName.Length : 1;
                var startLocation = GetLocation(lineInfo.LineNumber, lineInfo.LinePosition, 0);
                if (node is XAttribute && startLocation != Location.None)
                {
                    // Scan the original token: XAttribute.ToString() normalizes quotes,
                    // whitespace and entities, so its length need not match the source.
                    int start = startLocation.SourceSpan.Start;
                    int cursor = start;
                    while (cursor < SourceText.Length && SourceText[cursor] != '"' && SourceText[cursor] != '\'')
                        cursor++;
                    if (cursor < SourceText.Length)
                    {
                        char quote = SourceText[cursor++];
                        while (cursor < SourceText.Length && SourceText[cursor] != quote)
                            cursor++;
                        length = Math.Min(cursor + 1, SourceText.Length) - start;
                    }
                }
                return GetLocation(lineInfo.LineNumber, lineInfo.LinePosition, length);
            }

            public Location GetLocation(int lineNumber, int linePosition, int length)
            {
                int lineIndex = lineNumber - 1;
                if (lineIndex < 0 || lineIndex >= SourceText.Lines.Count)
                {
                    return Location.None;
                }

                var textLine = SourceText.Lines[lineIndex];
                int character = Math.Max(0, linePosition - 1);
                int start = Math.Min(textLine.Start + character, textLine.End);
                int end = Math.Min(start + length, SourceText.Length);
                if (end < start)
                {
                    end = start;
                }

                var span = TextSpan.FromBounds(start, end);
                var linePositionSpan = SourceText.Lines.GetLinePositionSpan(span);
                return Location.Create(SchemaPath, span, linePositionSpan);
            }

        }
    }
}
