namespace FixSourceGenerator.Schema
{
    /// <summary>
    /// Base type for an entry inside a message, component or group: a field reference,
    /// a component reference, or a nested group. Order is preserved as declared in the XML,
    /// since FIX field ordering is wire-significant for encoding.
    /// </summary>
    public abstract class FixEntry
    {
        /// <summary>Whether this entry is required ("Y") or optional ("N") in this specific context.</summary>
        public bool Required { get; }

        protected FixEntry(bool required)
        {
            Required = required;
        }
    }

    /// <summary>A &lt;field name="X" required="Y|N"/&gt; reference resolved against the dictionary's &lt;fields&gt; table.</summary>
    public sealed class FixFieldRef : FixEntry
    {
        public FixFieldRef(FixFieldDef field, bool required) : base(required)
        {
            Field = field;
        }

        public FixFieldDef Field { get; }
    }

    /// <summary>A &lt;component name="X" required="Y|N"/&gt; reference resolved against the dictionary's &lt;components&gt; table.</summary>
    public sealed class FixComponentRef : FixEntry
    {
        public FixComponentRef(FixComponentDef component, bool required) : base(required)
        {
            Component = component;
        }

        public FixComponentDef Component { get; }
    }

    /// <summary>
    /// A &lt;group name="X" required="Y|N"&gt;...&lt;/group&gt; declaration. <see cref="CounterField"/> is the
    /// NUMINGROUP field with the same name as the group (e.g. "NoAllocs"), resolved against &lt;fields&gt;.
    /// </summary>
    public sealed class FixGroupRef : FixEntry
    {
        public FixGroupRef(string name, FixFieldDef counterField, System.Collections.Generic.IReadOnlyList<FixEntry> entries, bool required)
            : base(required)
        {
            Name = name;
            CounterField = counterField;
            Entries = entries;
        }

        public string Name { get; }

        /// <summary>The NUMINGROUP field definition that carries this group's entry count on the wire.</summary>
        public FixFieldDef CounterField { get; }

        /// <summary>Entries of a single group row/entry (fields/components/nested groups), in wire order.</summary>
        public System.Collections.Generic.IReadOnlyList<FixEntry> Entries { get; }
    }
}
