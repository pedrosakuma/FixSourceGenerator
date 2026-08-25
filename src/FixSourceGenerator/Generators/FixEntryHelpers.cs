using System.Collections.Generic;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Generators
{
    /// <summary>Shared traversal helpers over the resolved <see cref="FixEntry"/> tree.</summary>
    internal static class FixEntryHelpers
    {
        /// <summary>
        /// A value field is represented as a generated C# enum only when it carries a fixed value
        /// domain AND its base type is CHAR or INT. STRING-with-&lt;value&gt; degrades to a raw span
        /// in v1 (documented pragmatic decision — a string→enum match would require runtime lookup;
        /// see docs/CONTRACT.md §3 and the final summary).
        /// </summary>
        public static bool IsEnumEligible(FixFieldDef field)
        {
            if (!field.HasValues)
            {
                return false;
            }

            var category = TypeTranslator.Translate(field.Type).Category;
            return category == FixTypeCategory.Char || category == FixTypeCategory.Int;
        }

        /// <summary>The first field number encountered depth-first: the group's delimiter tag.</summary>
        public static int GetDelimiterTag(IReadOnlyList<FixEntry> entries)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        return fieldRef.Field.Number;
                    case FixComponentRef componentRef:
                        int nested = GetDelimiterTag(componentRef.Component.Entries);
                        if (nested != 0)
                        {
                            return nested;
                        }

                        break;
                    case FixGroupRef groupRef:
                        return groupRef.CounterField.Number;
                }
            }

            return 0;
        }

        /// <summary>
        /// The full, flattened set of tags that belong to a single group entry — including nested
        /// group counter tags and every tag reachable through nested components/groups. Passed to
        /// <c>FixGroupEnumerator</c> so it can bound the last entry (docs/CONTRACT.md §6).
        /// </summary>
        public static IReadOnlyList<int> FlattenEntryTags(IReadOnlyList<FixEntry> entries)
        {
            var tags = new List<int>();
            var seen = new HashSet<int>();
            Collect(entries, tags, seen);
            return tags;
        }

        private static void Collect(IReadOnlyList<FixEntry> entries, List<int> tags, HashSet<int> seen)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        Add(fieldRef.Field.Number, tags, seen);
                        break;
                    case FixComponentRef componentRef:
                        Collect(componentRef.Component.Entries, tags, seen);
                        break;
                    case FixGroupRef groupRef:
                        Add(groupRef.CounterField.Number, tags, seen);
                        Collect(groupRef.Entries, tags, seen);
                        break;
                }
            }
        }

        private static void Add(int tag, List<int> tags, HashSet<int> seen)
        {
            if (seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        /// <summary>Collects every component definition reachable from <paramref name="entries"/>.</summary>
        public static void CollectComponents(IReadOnlyList<FixEntry> entries, Dictionary<string, FixComponentDef> into)
        {
            var visited = new HashSet<string>();
            CollectComponents(entries, into, visited);
        }

        /// <summary>Collects every named group shape reachable from <paramref name="entries"/>.</summary>
        public static void CollectGroups(IReadOnlyList<FixEntry> entries, Dictionary<string, FixGroupRef> into)
        {
            var visitedComponents = new HashSet<string>();
            CollectGroups(entries, into, visitedComponents);
        }


        private static void CollectComponents(IReadOnlyList<FixEntry> entries, Dictionary<string, FixComponentDef> into, HashSet<string> visited)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixComponentRef componentRef:
                        if (!into.ContainsKey(componentRef.Component.Name))
                        {
                            into[componentRef.Component.Name] = componentRef.Component;
                        }

                        if (visited.Add(componentRef.Component.Name))
                        {
                            CollectComponents(componentRef.Component.Entries, into, visited);
                        }

                        break;
                    case FixGroupRef groupRef:
                        CollectComponents(groupRef.Entries, into, visited);
                        break;
                }
            }
        }

        private static void CollectGroups(IReadOnlyList<FixEntry> entries, Dictionary<string, FixGroupRef> into, HashSet<string> visitedComponents)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixComponentRef componentRef when visitedComponents.Add(componentRef.Component.Name):
                        CollectGroups(componentRef.Component.Entries, into, visitedComponents);
                        break;
                    case FixGroupRef groupRef:
                        if (!into.ContainsKey(groupRef.Name))
                        {
                            into[groupRef.Name] = groupRef;
                        }
                        CollectGroups(groupRef.Entries, into, visitedComponents);
                        break;
                }
            }
        }

        /// <summary>Collects every enum-eligible field definition reachable from <paramref name="entries"/>.</summary>
        public static void CollectEnumFields(IReadOnlyList<FixEntry> entries, Dictionary<string, FixFieldDef> into)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        if (IsEnumEligible(fieldRef.Field) && !into.ContainsKey(fieldRef.Field.Name))
                        {
                            into[fieldRef.Field.Name] = fieldRef.Field;
                        }

                        break;
                    case FixComponentRef componentRef:
                        CollectEnumFields(componentRef.Component.Entries, into);
                        break;
                    case FixGroupRef groupRef:
                        CollectEnumFields(groupRef.Entries, into);
                        break;
                }
            }
        }
    }
}
