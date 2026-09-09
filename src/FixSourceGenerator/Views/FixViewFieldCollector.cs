using System.Collections.Generic;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Views
{
    /// <summary>Flattens the fields directly reachable from a message's own entries plus its components (mirrors <see cref="Generators.FixEntryHelpers"/>'s traversal, but keyed by field name for FixView matching). A group's own *entries* are intentionally NOT flattened into scalar fields — a view can't select individual fields nested inside a group (out of scope, no single scalar value to expose for a 0..N repetition) — but the group itself can be exposed as a whole via <see cref="CollectGroups"/> (issue #17).</summary>
    internal static class FixViewFieldCollector
    {
        public static void Collect(
            IReadOnlyList<FixEntry> entries,
            Dictionary<string, (FixFieldDef Field, bool Required)> into,
            bool requiredContext = true)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixFieldRef fieldRef:
                        if (!into.ContainsKey(fieldRef.Field.Name))
                        {
                            into[fieldRef.Field.Name] = (fieldRef.Field, requiredContext && fieldRef.Required);
                        }

                        break;
                    case FixComponentRef componentRef:
                        Collect(componentRef.Component.Entries, into, requiredContext && componentRef.Required);
                        break;
                    // FixGroupRef: not flattened into scalar fields here — see CollectGroups.
                }
            }
        }

        public static void CollectDirectGroups(IReadOnlyList<FixEntry> entries, List<FixGroupRef> into)
        {
            Generators.GroupScopeEmitter.CollectMembers(entries, into, new HashSet<int>());
        }

        /// <summary>Collects the groups directly reachable from a message's own entries plus its components, keyed by group name, for exposing a whole group as a <c>[FixView]</c> property (issue #17). Groups nested inside other groups are NOT collected — a view targets a single message level, matching how the full reader only exposes its own level's groups.</summary>
        public static void CollectGroups(IReadOnlyList<FixEntry> entries, Dictionary<string, FixGroupRef> into)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixGroupRef groupRef:
                        if (!into.ContainsKey(groupRef.Name))
                        {
                            into[groupRef.Name] = groupRef;
                        }

                        break;
                    case FixComponentRef componentRef:
                        CollectGroups(componentRef.Component.Entries, into);
                        break;
                }
            }
        }

        public static bool TryResolveQualifiedScope(
            FixDictionary schema,
            string path,
            bool messageRoot,
            out IReadOnlyList<FixEntry>? entries,
            out string? scopeName)
        {
            string[] segments = path.Split('.');
            entries = null;
            scopeName = null;
            if (segments.Length < 2)
            {
                return false;
            }

            if (messageRoot)
            {
                foreach (var message in schema.Messages)
                {
                    if (string.Equals(message.Name, segments[0], System.StringComparison.Ordinal))
                    {
                        entries = message.Entries;
                        scopeName = message.Name;
                        break;
                    }
                }
            }
            else if (schema.ComponentsByName.TryGetValue(segments[0], out var component))
            {
                entries = component.Entries;
                scopeName = component.Name;
            }

            if (entries == null)
            {
                return false;
            }

            for (int i = 1; i < segments.Length; i++)
            {
                IReadOnlyList<FixEntry>? nextEntries = null;
                string? nextName = null;
                int matches = 0;
                FindDirectScopes(entries, segments[i], ref nextEntries, ref nextName, ref matches);
                if (matches != 1 || nextEntries == null)
                {
                    entries = null;
                    scopeName = null;
                    return false;
                }

                entries = nextEntries;
                scopeName = nextName;
            }

            return true;
        }

        private static void FindDirectScopes(
            IReadOnlyList<FixEntry> entries,
            string name,
            ref IReadOnlyList<FixEntry>? matchedEntries,
            ref string? matchedName,
            ref int matches)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case FixComponentRef componentRef when string.Equals(componentRef.Component.Name, name, System.StringComparison.Ordinal):
                        matchedEntries = componentRef.Component.Entries;
                        matchedName = componentRef.Component.Name;
                        matches++;
                        break;
                    case FixGroupRef groupRef when string.Equals(groupRef.Name, name, System.StringComparison.Ordinal):
                        matchedEntries = groupRef.Entries;
                        matchedName = groupRef.Name;
                        matches++;
                        break;
                }
            }
        }

        /// <summary>Levenshtein edit distance, used for the FIX012 "did you mean" suggestion.</summary>
        public static int LevenshteinDistance(string a, string b)
        {
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = System.Math.Min(
                        System.Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }

            return d[a.Length, b.Length];
        }

        /// <summary>Finds the closest known field name within an edit-distance threshold, or null.</summary>
        public static string? FindClosest(string name, IEnumerable<string> candidates, int maxDistance = 3)
        {
            string? best = null;
            int bestDistance = maxDistance + 1;
            foreach (var candidate in candidates)
            {
                int distance = LevenshteinDistance(name, candidate);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return bestDistance <= maxDistance ? best : null;
        }
    }
}
