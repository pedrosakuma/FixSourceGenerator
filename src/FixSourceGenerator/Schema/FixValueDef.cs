namespace FixSourceGenerator.Schema
{
    /// <summary>
    /// A single &lt;value enum="" description=""/&gt; entry under a field definition.
    /// Used to generate an enum member.
    /// </summary>
    public sealed class FixValueDef
    {
        public FixValueDef(string enumValue, string description)
        {
            EnumValue = enumValue;
            Description = description;
        }

        /// <summary>The wire value, e.g. "1" for a CHAR/INT field or "BUY" for a STRING field.</summary>
        public string EnumValue { get; }

        /// <summary>The human-readable name, e.g. "BUY". Normalized to PascalCase for the C# enum member.</summary>
        public string Description { get; }
    }
}
