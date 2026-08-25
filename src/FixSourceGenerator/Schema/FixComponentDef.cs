using System.Collections.Generic;

namespace FixSourceGenerator.Schema
{
    /// <summary>A reusable &lt;component name="Instrument"&gt;...&lt;/component&gt; definition from &lt;components&gt;.</summary>
    public sealed class FixComponentDef
    {
        public FixComponentDef(string name, IReadOnlyList<FixEntry> entries)
        {
            Name = name;
            Entries = entries;
        }

        public string Name { get; }

        public IReadOnlyList<FixEntry> Entries { get; }
    }
}
