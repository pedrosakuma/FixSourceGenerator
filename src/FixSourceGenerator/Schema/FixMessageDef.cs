using System.Collections.Generic;

namespace FixSourceGenerator.Schema
{
    /// <summary>A &lt;message name="NewOrderSingle" msgtype="D" msgcat="app"&gt;...&lt;/message&gt; definition.</summary>
    public sealed class FixMessageDef
    {
        public FixMessageDef(string name, string msgType, string? msgCat, IReadOnlyList<FixEntry> entries)
        {
            Name = name;
            MsgType = msgType;
            MsgCat = msgCat;
            Entries = entries;
        }

        public string Name { get; }

        public string MsgType { get; }

        public string? MsgCat { get; }

        public IReadOnlyList<FixEntry> Entries { get; }
    }
}
