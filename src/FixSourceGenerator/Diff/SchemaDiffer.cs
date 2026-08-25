using System;
using System.Collections.Generic;
using System.Linq;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Diff
{
    public static class SchemaDiffer
    {
        public static IReadOnlyList<SchemaChange> Diff(FixDictionary oldSchema, FixDictionary newSchema)
        {
            if (oldSchema == null) throw new ArgumentNullException(nameof(oldSchema));
            if (newSchema == null) throw new ArgumentNullException(nameof(newSchema));

            var changes = new List<SchemaChange>();
            DiffMessages(oldSchema.Messages, newSchema.Messages, changes);
            DiffComponents(oldSchema.ComponentsByName, newSchema.ComponentsByName, changes);
            DiffFieldDefinitions(oldSchema.FieldsByName, newSchema.FieldsByName, changes);

            return changes.OrderByDescending(change => change.Severity).ThenBy(change => change.Path, StringComparer.Ordinal).ThenBy(change => change.Kind).ToArray();
        }

        private static void DiffMessages(IReadOnlyList<FixMessageDef> oldMessages, IReadOnlyList<FixMessageDef> newMessages, List<SchemaChange> changes)
        {
            var oldByName = oldMessages.ToDictionary(message => message.Name, StringComparer.Ordinal);
            var newByName = newMessages.ToDictionary(message => message.Name, StringComparer.Ordinal);

            foreach (var oldPair in oldByName)
            {
                if (!newByName.TryGetValue(oldPair.Key, out var newMessage))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.MessageRemoved, SchemaDiffSeverity.Breaking, oldPair.Key, "Message was removed.", oldPair.Value.MsgType, null));
                    continue;
                }

                if (!string.Equals(oldPair.Value.MsgType, newMessage.MsgType, StringComparison.Ordinal))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.MessageMsgTypeChanged, SchemaDiffSeverity.Breaking, oldPair.Key, "Message type changed.", oldPair.Value.MsgType, newMessage.MsgType));
                }

                DiffEntries(oldPair.Value.Entries, newMessage.Entries, oldPair.Key, changes);
            }

            foreach (var newPair in newByName)
            {
                if (!oldByName.ContainsKey(newPair.Key))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.MessageAdded, SchemaDiffSeverity.Info, newPair.Key, "Message was added.", null, newPair.Value.MsgType));
                }
            }
        }

        private static void DiffComponents(IReadOnlyDictionary<string, FixComponentDef> oldComponents, IReadOnlyDictionary<string, FixComponentDef> newComponents, List<SchemaChange> changes)
        {
            foreach (var oldPair in oldComponents)
            {
                if (!newComponents.TryGetValue(oldPair.Key, out var newComponent))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.ComponentRemoved, SchemaDiffSeverity.Breaking, oldPair.Key, "Component was removed."));
                    continue;
                }

                DiffEntries(oldPair.Value.Entries, newComponent.Entries, oldPair.Key, changes);
            }

            foreach (var newPair in newComponents)
            {
                if (!oldComponents.ContainsKey(newPair.Key))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.ComponentAdded, SchemaDiffSeverity.Info, newPair.Key, "Component was added."));
                }
            }
        }

        private static void DiffFieldDefinitions(IReadOnlyDictionary<string, FixFieldDef> oldFields, IReadOnlyDictionary<string, FixFieldDef> newFields, List<SchemaChange> changes)
        {
            foreach (var oldPair in oldFields)
            {
                if (!newFields.TryGetValue(oldPair.Key, out var newField))
                {
                    continue;
                }

                if (!string.Equals(oldPair.Value.Type, newField.Type, StringComparison.Ordinal))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.FieldTypeChanged, SchemaDiffSeverity.Breaking, "fields." + oldPair.Key, "Field type changed.", oldPair.Value.Type, newField.Type));
                }

                DiffEnumValues(oldPair.Value, newField, changes);
            }
        }

        private static void DiffEnumValues(FixFieldDef oldField, FixFieldDef newField, List<SchemaChange> changes)
        {
            var oldValues = oldField.Values.ToDictionary(value => value.EnumValue, StringComparer.Ordinal);
            var newValues = newField.Values.ToDictionary(value => value.EnumValue, StringComparer.Ordinal);

            foreach (var oldPair in oldValues)
            {
                if (!newValues.ContainsKey(oldPair.Key))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.EnumValueRemoved, SchemaDiffSeverity.Breaking, "fields." + oldField.Name + ".values." + oldPair.Key, "Enumerated value was removed.", oldPair.Value.Description, null));
                }
            }

            foreach (var newPair in newValues)
            {
                if (!oldValues.ContainsKey(newPair.Key))
                {
                    changes.Add(new SchemaChange(SchemaChangeKind.EnumValueAdded, SchemaDiffSeverity.Info, "fields." + oldField.Name + ".values." + newPair.Key, "Enumerated value was added.", null, newPair.Value.Description));
                }
            }
        }

        private static void DiffEntries(IReadOnlyList<FixEntry> oldEntries, IReadOnlyList<FixEntry> newEntries, string parentPath, List<SchemaChange> changes)
        {
            var oldByKey = oldEntries.ToDictionary(GetEntryKey, StringComparer.Ordinal);
            var newByKey = newEntries.ToDictionary(GetEntryKey, StringComparer.Ordinal);

            foreach (var oldPair in oldByKey)
            {
                if (!newByKey.TryGetValue(oldPair.Key, out var newEntry))
                {
                    AddRemoval(oldPair.Value, parentPath, changes);
                    continue;
                }

                if (oldPair.Value.Required != newEntry.Required)
                {
                    AddRequirednessChange(oldPair.Value, newEntry, parentPath, changes);
                }

                if (oldPair.Value is FixGroupRef oldGroup && newEntry is FixGroupRef newGroup)
                {
                    DiffEntries(oldGroup.Entries, newGroup.Entries, parentPath + "." + oldGroup.Name, changes);
                }

                if (oldPair.Value is FixComponentRef oldComponent && newEntry is FixComponentRef newComponent)
                {
                    DiffEntries(oldComponent.Component.Entries, newComponent.Component.Entries, parentPath + "." + oldComponent.Component.Name, changes);
                }
            }

            foreach (var newPair in newByKey)
            {
                if (!oldByKey.ContainsKey(newPair.Key))
                {
                    AddAddition(newPair.Value, parentPath, changes);
                }
            }
        }

        private static void AddRequirednessChange(FixEntry oldEntry, FixEntry newEntry, string parentPath, List<SchemaChange> changes)
        {
            if (!oldEntry.Required && newEntry.Required)
            {
                changes.Add(new SchemaChange(SchemaChangeKind.FieldAdded, SchemaDiffSeverity.Breaking, BuildEntryPath(parentPath, newEntry), "Entry changed from optional to required.", "optional", "required"));
            }
            else if (oldEntry.Required && !newEntry.Required)
            {
                changes.Add(new SchemaChange(SchemaChangeKind.FieldRemoved, SchemaDiffSeverity.Warning, BuildEntryPath(parentPath, newEntry), "Entry changed from required to optional.", "required", "optional"));
            }
        }

        private static void AddRemoval(FixEntry entry, string parentPath, List<SchemaChange> changes)
        {
            var severity = entry.Required ? SchemaDiffSeverity.Breaking : SchemaDiffSeverity.Warning;
            var kind = entry is FixGroupRef ? SchemaChangeKind.GroupEntryRemoved : SchemaChangeKind.FieldRemoved;
            changes.Add(new SchemaChange(kind, severity, BuildEntryPath(parentPath, entry), entry.Required ? "Required entry was removed." : "Optional entry was removed."));
        }

        private static void AddAddition(FixEntry entry, string parentPath, List<SchemaChange> changes)
        {
            var severity = entry.Required ? SchemaDiffSeverity.Breaking : SchemaDiffSeverity.Info;
            var kind = entry is FixGroupRef ? SchemaChangeKind.GroupEntryAdded : SchemaChangeKind.FieldAdded;
            changes.Add(new SchemaChange(kind, severity, BuildEntryPath(parentPath, entry), entry.Required ? "Required entry was added." : "Optional entry was added."));
        }

        private static string GetEntryKey(FixEntry entry)
        {
            if (entry is FixFieldRef field) return "field:" + field.Field.Name;
            if (entry is FixComponentRef component) return "component:" + component.Component.Name;
            return "group:" + ((FixGroupRef)entry).Name;
        }

        private static string BuildEntryPath(string parentPath, FixEntry entry)
        {
            if (entry is FixFieldRef field) return parentPath + "." + field.Field.Name;
            if (entry is FixComponentRef component) return parentPath + "." + component.Component.Name;
            return parentPath + "." + ((FixGroupRef)entry).Name;
        }
    }
}
