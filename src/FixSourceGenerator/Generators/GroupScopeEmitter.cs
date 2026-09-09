using System.Collections.Generic;
using System.Linq;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Generators
{
    internal static class GroupScopeEmitter
    {
        internal static void CollectMembers(IReadOnlyList<FixEntry> entries, List<FixGroupRef> groups, HashSet<int> tags)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef field:
                        tags.Add(field.Field.Number);
                        break;
                    case FixGroupRef group:
                        if (!groups.Contains(group))
                            groups.Add(group);
                        break;
                    case FixComponentRef component:
                        CollectMembers(component.Component.Entries, groups, tags);
                        break;
                }
            }
        }

        internal static void AssignIds(IReadOnlyList<FixGroupRef> groups, Dictionary<FixGroupRef, string> ids)
        {
            foreach (var group in groups)
            {
                if (ids.ContainsKey(group))
                    continue;
                ids[group] = group.Name.ToIdentifier() + "_" + group.CounterField.Number + "_" + ids.Count;
                var nested = new List<FixGroupRef>();
                CollectMembers(group.Entries, nested, new HashSet<int>());
                AssignIds(nested, ids);
            }
        }

        internal static void EmitSkipHelper(
            CodeWriter w, string runtimeNs, FixGroupRef group,
            Dictionary<FixGroupRef, string> ids, HashSet<FixGroupRef> emitted)
        {
            if (!emitted.Add(group))
                return;

            var nested = new List<FixGroupRef>();
            var tags = new HashSet<int>();
            CollectMembers(group.Entries, nested, tags);
            foreach (var child in nested)
                EmitSkipHelper(w, runtimeNs, child, ids, emitted);

            string r = $"{runtimeNs}.FixSpanReader";
            int delimiter = FixEntryHelpers.GetDelimiterTag(group.Entries);
            w.Line();
            w.Open($"private static bool TrySkip{ids[group]}(global::System.ReadOnlySpan<byte> buffer, int position, int count, out int end, bool allowTrailingDelimiter = false)");
            w.Line("end = position;");
            w.Open("if (count < 0)");
            w.Line("return false;");
            w.Close();
            w.Open("for (int entryIndex = 0; entryIndex < count; entryIndex++)");
            w.Open($"if (!{r}.TryReadField(buffer, end, out int delimiter, out int firstValueStart, out int firstValueLength, out int next) || delimiter != {delimiter})");
            w.Line("return false;");
            w.Close();
            w.Line("end = next;");
            foreach (var child in nested)
                w.Line($"bool seen{ids[child]} = false;");
            foreach (var child in nested.Where(child => child.CounterField.Number == delimiter))
            {
                string allowTrailing = FixEntryHelpers.GetDelimiterTag(child.Entries) == delimiter ? "true" : "false";
                w.Open($"if (!{r}.TryParseInt(buffer.Slice(firstValueStart, firstValueLength), out int firstCount) || !TrySkip{ids[child]}(buffer, next, firstCount, out end, allowTrailingDelimiter: {allowTrailing}))");
                w.Line("return false;");
                w.Close();
                w.Line($"seen{ids[child]} = true;");
            }
            w.Open($"while ({r}.TryReadField(buffer, end, out int tag, out int valueStart, out int valueLength, out next))");
            w.Open($"if (tag == {delimiter})");
            w.Line("break;");
            w.Close();
            foreach (var child in nested)
            {
                string id = ids[child];
                w.Open($"if (tag == {child.CounterField.Number})");
                w.Open($"if (seen{id})");
                w.Line("break;");
                w.Close();
                string allowTrailing = FixEntryHelpers.GetDelimiterTag(child.Entries) == delimiter ? "true" : "false";
                w.Open($"if (!{r}.TryParseInt(buffer.Slice(valueStart, valueLength), out int nestedCount) || !TrySkip{id}(buffer, next, nestedCount, out end, allowTrailingDelimiter: {allowTrailing}))");
                w.Line("return false;");
                w.Close();
                w.Line($"seen{id} = true;");
                w.Line("continue;");
                w.Close();
            }
            if (tags.Count > 0)
            {
                w.Open($"if ({string.Join(" || ", tags.OrderBy(t => t).Select(t => $"tag == {t}"))})");
                w.Line("end = next;");
                w.Line("continue;");
                w.Close();
            }
            w.Line("break;");
            w.Close();
            w.Close();
            w.Open($"if (!allowTrailingDelimiter && {r}.TryReadField(buffer, end, out int trailingTag, out _, out _, out _) && trailingTag == {delimiter})");
            w.Line("return false;");
            w.Close();
            w.Line("return true;");
            w.Close();
        }
    }
}
