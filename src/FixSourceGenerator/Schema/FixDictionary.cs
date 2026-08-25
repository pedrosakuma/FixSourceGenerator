using System.Collections.Generic;

namespace FixSourceGenerator.Schema
{
    /// <summary>
    /// The fully parsed, resolved representation of a QuickFIX-style DataDictionary XML document
    /// (see docs/CONTRACT.md §1). All &lt;field&gt;/&lt;component&gt;/&lt;group&gt; references inside
    /// header/trailer/messages/components are already resolved to their definitions by the time
    /// this model is produced by <see cref="SchemaReader"/> — there are no dangling names left to
    /// look up at codegen time.
    /// </summary>
    public sealed class FixDictionary
    {
        public FixDictionary(
            string fixType,
            int major,
            int minor,
            int servicePack,
            IReadOnlyList<FixEntry> header,
            IReadOnlyList<FixEntry> trailer,
            IReadOnlyList<FixMessageDef> messages,
            IReadOnlyDictionary<string, FixComponentDef> componentsByName,
            IReadOnlyDictionary<string, FixFieldDef> fieldsByName,
            IReadOnlyDictionary<int, FixFieldDef> fieldsByNumber)
        {
            FixType = fixType;
            Major = major;
            Minor = minor;
            ServicePack = servicePack;
            Header = header;
            Trailer = trailer;
            Messages = messages;
            ComponentsByName = componentsByName;
            FieldsByName = fieldsByName;
            FieldsByNumber = fieldsByNumber;
        }

        /// <summary>"FIX" or "FIXT", from the &lt;fix type="..."&gt; attribute (defaults to "FIX").</summary>
        public string FixType { get; }

        public int Major { get; }

        public int Minor { get; }

        /// <summary>Defaults to 0 when the &lt;fix servicepack="..."&gt; attribute is absent.</summary>
        public int ServicePack { get; }

        public IReadOnlyList<FixEntry> Header { get; }

        public IReadOnlyList<FixEntry> Trailer { get; }

        public IReadOnlyList<FixMessageDef> Messages { get; }

        public IReadOnlyDictionary<string, FixComponentDef> ComponentsByName { get; }

        public IReadOnlyDictionary<string, FixFieldDef> FieldsByName { get; }

        public IReadOnlyDictionary<int, FixFieldDef> FieldsByNumber { get; }

        /// <summary>
        /// Version token used for the generated namespace suffix (docs/CONTRACT.md §5/§7),
        /// e.g. "V44" for major=4/minor=4/servicepack=0, "V50SP2" for major=5/minor=0/servicepack=2,
        /// "FIXT11" for type="FIXT"/major=1/minor=1.
        /// </summary>
        public string VersionToken
        {
            get
            {
                if (string.Equals(FixType, "FIXT", System.StringComparison.OrdinalIgnoreCase))
                {
                    return $"FIXT{Major}{Minor}";
                }

                string token = $"V{Major}{Minor}";
                return ServicePack > 0 ? $"{token}SP{ServicePack}" : token;
            }
        }
    }
}
