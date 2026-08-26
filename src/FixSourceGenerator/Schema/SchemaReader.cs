using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using FixSourceGenerator.Diagnostics;
using Microsoft.CodeAnalysis;

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
        public static FixDictionary? Parse(string xmlContent, string schemaPath, Action<Diagnostic> reportDiagnostic)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(xmlContent, LoadOptions.None);
            }
            catch (Exception ex)
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MalformedSchema, Location.None, schemaPath, ex.Message));
                return null;
            }

            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "fix", StringComparison.OrdinalIgnoreCase))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MalformedSchema, Location.None, schemaPath, "missing <fix> root element"));
                return null;
            }

            string? majorText = (string?)root.Attribute("major");
            string? minorText = (string?)root.Attribute("minor");
            if (majorText == null)
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "fix", "major"));
            }
            else if (!int.TryParse(majorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.InvalidAttributeValue, Location.None, "fix", "major", majorText, "an integer"));
            }
            if (minorText == null)
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "fix", "minor"));
            }
            else if (!int.TryParse(minorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.InvalidAttributeValue, Location.None, "fix", "minor", minorText, "an integer"));
            }

            int major = ParseIntOrDefault(majorText, 0);
            int minor = ParseIntOrDefault(minorText, 0);
            int servicePack = ParseIntOrDefault((string?)root.Attribute("servicepack"), 0);
            string fixType = (string?)root.Attribute("type") ?? "FIX";

            // ---- Stage 1: <fields> — must exist before anything else can be resolved. ----
            var fieldsByNumber = new Dictionary<int, FixFieldDef>();
            var fieldsByName = new Dictionary<string, FixFieldDef>(StringComparer.Ordinal);

            var fieldsSection = root.Element("fields");
            if (fieldsSection != null)
            {
                foreach (var fieldEl in fieldsSection.Elements("field"))
                {
                    ParseFieldDefinition(fieldEl, fieldsByNumber, fieldsByName, reportDiagnostic);
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
                        reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "component", "name"));
                        continue;
                    }

                    if (componentElementsByName.ContainsKey(name!))
                    {
                        reportDiagnostic(Diagnostic.Create(FixDiagnostics.DuplicateDefinition, Location.None, "component", name));
                        continue;
                    }

                    componentElementsByName[name!] = componentEl;
                }
            }

            var componentsByName = new Dictionary<string, FixComponentDef>(StringComparer.Ordinal);
            var componentResolutionState = new Dictionary<string, ResolutionState>(StringComparer.Ordinal);

            foreach (var name in componentElementsByName.Keys.ToArray())
            {
                ResolveComponent(name, componentElementsByName, componentsByName, componentResolutionState, fieldsByName, reportDiagnostic);
            }

            // ---- Stage 3: header / trailer / messages — resolved against fields+components. ----
            var header = ParseEntries(root.Element("header"), fieldsByName, componentsByName, reportDiagnostic);
            var trailer = ParseEntries(root.Element("trailer"), fieldsByName, componentsByName, reportDiagnostic);

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
                        reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "message", "name"));
                        continue;
                    }
                    if (string.IsNullOrEmpty(msgType))
                    {
                        reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "message", "msgtype"));
                        continue;
                    }

                    if (!messageNames.Add(name!))
                    {
                        reportDiagnostic(Diagnostic.Create(FixDiagnostics.DuplicateDefinition, Location.None, "message name", name));
                        continue;
                    }
                    if (!messageTypes.Add(msgType!))
                    {
                        reportDiagnostic(Diagnostic.Create(FixDiagnostics.DuplicateDefinition, Location.None, "message msgtype", msgType));
                        continue;
                    }

                    var entries = ParseEntries(messageEl, fieldsByName, componentsByName, reportDiagnostic);
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
            Action<Diagnostic> reportDiagnostic)
        {
            string? numberText = (string?)fieldEl.Attribute("number");
            string? name = (string?)fieldEl.Attribute("name");
            string? type = (string?)fieldEl.Attribute("type");

            bool missingAttr = false;
            if (string.IsNullOrEmpty(numberText))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "field", "number"));
                missingAttr = true;
            }
            if (string.IsNullOrEmpty(name))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "field", "name"));
                missingAttr = true;
            }
            if (string.IsNullOrEmpty(type))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "field", "type"));
                missingAttr = true;
            }

            if (missingAttr)
            {
                return;
            }

            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.InvalidAttributeValue, Location.None, "field", "number", numberText, "an integer"));
                return;
            }

            var values = fieldEl.Elements("value")
                .Select(valueEl => new FixValueDef((string?)valueEl.Attribute("enum") ?? string.Empty, (string?)valueEl.Attribute("description") ?? string.Empty))
                .ToList();

            var fieldDef = new FixFieldDef(number, name!, type!, values);

            if (fieldsByNumber.ContainsKey(number))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.DuplicateDefinition, Location.None, "field number", number.ToString(CultureInfo.InvariantCulture)));
            }
            else
            {
                fieldsByNumber[number] = fieldDef;
            }

            if (fieldsByName.ContainsKey(name!))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.DuplicateDefinition, Location.None, "field name", name));
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

        private static FixComponentDef? ResolveComponent(
            string name,
            Dictionary<string, XElement> componentElementsByName,
            Dictionary<string, FixComponentDef> componentsByName,
            Dictionary<string, ResolutionState> resolutionState,
            Dictionary<string, FixFieldDef> fieldsByName,
            Action<Diagnostic> reportDiagnostic)
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
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.CircularComponentReference, Location.None, name));
                return null;
            }

            resolutionState[name] = ResolutionState.InProgress;

            var entries = ParseEntries(
                componentEl,
                fieldsByName,
                componentsByName,
                reportDiagnostic,
                nestedComponentResolver: refName => ResolveComponent(refName, componentElementsByName, componentsByName, resolutionState, fieldsByName, reportDiagnostic));

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
            Action<Diagnostic> reportDiagnostic,
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
                        entries.Add(ParseFieldRef(child, fieldsByName, container.Name.LocalName, reportDiagnostic));
                        break;

                    case "component":
                        entries.Add(ParseComponentRef(child, componentsByName, container.Name.LocalName, reportDiagnostic, nestedComponentResolver));
                        break;

                    case "group":
                        entries.Add(ParseGroupRef(child, fieldsByName, componentsByName, reportDiagnostic, nestedComponentResolver));
                        break;

                    default:
                        reportDiagnostic(Diagnostic.Create(
                            FixDiagnostics.UnsupportedConstruct,
                            Location.None,
                            $"Unrecognized element <{child.Name.LocalName}> inside <{container.Name.LocalName}> is ignored"));
                        break;
                }
            }

            return entries.Where(e => e != null).ToList()!;
        }

        private static FixEntry ParseFieldRef(
            XElement fieldRefEl,
            Dictionary<string, FixFieldDef> fieldsByName,
            string parentElementName,
            Action<Diagnostic> reportDiagnostic)
        {
            string? name = (string?)fieldRefEl.Attribute("name");
            bool required = IsRequired(fieldRefEl);

            if (string.IsNullOrEmpty(name))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "field", "name"));
                return new FixFieldRef(UnknownField, required);
            }

            if (!fieldsByName.TryGetValue(name!, out var fieldDef))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.UnresolvedReference, Location.None, parentElementName, name, "field", name));
                return new FixFieldRef(new FixFieldDef(0, name!, "STRING", Array.Empty<FixValueDef>()), required);
            }

            return new FixFieldRef(fieldDef, required);
        }

        private static readonly FixFieldDef UnknownField = new FixFieldDef(0, "Unknown", "STRING", Array.Empty<FixValueDef>());

        private static FixEntry ParseComponentRef(
            XElement componentRefEl,
            Dictionary<string, FixComponentDef> componentsByName,
            string parentElementName,
            Action<Diagnostic> reportDiagnostic,
            Func<string, FixComponentDef?>? nestedComponentResolver)
        {
            string? name = (string?)componentRefEl.Attribute("name");
            bool required = IsRequired(componentRefEl);

            if (string.IsNullOrEmpty(name))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "component", "name"));
                return new FixComponentRef(new FixComponentDef("Unknown", Array.Empty<FixEntry>()), required);
            }

            FixComponentDef? componentDef = null;
            if (!componentsByName.TryGetValue(name!, out componentDef))
            {
                componentDef = nestedComponentResolver?.Invoke(name!);
            }

            if (componentDef == null)
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.UnresolvedReference, Location.None, parentElementName, name, "component", name));
                componentDef = new FixComponentDef(name!, Array.Empty<FixEntry>());
            }

            return new FixComponentRef(componentDef, required);
        }

        private static FixEntry ParseGroupRef(
            XElement groupEl,
            Dictionary<string, FixFieldDef> fieldsByName,
            Dictionary<string, FixComponentDef> componentsByName,
            Action<Diagnostic> reportDiagnostic,
            Func<string, FixComponentDef?>? nestedComponentResolver)
        {
            string? name = (string?)groupEl.Attribute("name");
            bool required = IsRequired(groupEl);

            if (string.IsNullOrEmpty(name))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingRequiredAttribute, Location.None, "group", "name"));
                name = "Unknown";
            }

            if (!fieldsByName.TryGetValue(name!, out var counterField))
            {
                reportDiagnostic(Diagnostic.Create(FixDiagnostics.MissingGroupCounterField, Location.None, name));
                counterField = new FixFieldDef(0, name!, "NUMINGROUP", Array.Empty<FixValueDef>());
            }

            var entries = ParseEntries(groupEl, fieldsByName, componentsByName, reportDiagnostic, nestedComponentResolver);
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
    }
}
