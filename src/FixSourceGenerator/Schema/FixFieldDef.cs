using System.Collections.Generic;

namespace FixSourceGenerator.Schema
{
    /// <summary>
    /// A field definition from the dictionary's &lt;fields&gt; section: &lt;field number="11" name="ClOrdID" type="STRING"/&gt;.
    /// This is the global, dictionary-wide definition — required-ness is NOT here, it lives on
    /// the <see cref="FixFieldRef"/> that references this definition from a message/component/group.
    /// </summary>
    public sealed class FixFieldDef
    {
        public FixFieldDef(int number, string name, string type, IReadOnlyList<FixValueDef> values)
        {
            Number = number;
            Name = name;
            Type = type;
            Values = values;
        }

        public int Number { get; }

        public string Name { get; }

        /// <summary>Raw FIX type name as declared in the dictionary, e.g. "STRING", "PRICE", "NUMINGROUP".</summary>
        public string Type { get; }

        /// <summary>Enumerated &lt;value&gt; children, if any. Empty when the field has no fixed value domain.</summary>
        public IReadOnlyList<FixValueDef> Values { get; }

        public bool HasValues => Values.Count > 0;
    }
}
