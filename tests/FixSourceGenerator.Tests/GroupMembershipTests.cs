using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FixSourceGenerator.Schema;

namespace FixSourceGenerator.Tests;

public class GroupMembershipTests
{
    private const string Driver = """
        using System;
        using System.Collections.Generic;
        using System.Text;
        using Acme.Fix.V44;
        using Acme.Fix.V44.Runtime;

        public static class GroupDriver
        {
            public static string[] Slice(byte[] frame, int[] tags, bool sorted)
            {
                var iterator = sorted
                    ? new FixGroupEnumerator(frame, 500, 9000, tags, sortedEntryTags: true)
                    : new FixGroupEnumerator(frame, 500, 9000, tags);
                var entries = new List<string>();
                while (iterator.MoveNext())
                    entries.Add(Encoding.ASCII.GetString(iterator.Current).Replace('\x01', '|'));
                return entries.ToArray();
            }

            public static string[] Read(byte[] frame)
            {
                var entries = new List<string>();
                foreach (var entry in new LargeMessageReader(frame).NoEntries)
                {
                    string value = Encoding.ASCII.GetString(entry.Delimiter)
                        + ":" + entry.Fields.Low + ":" + entry.Fields.High;
                    foreach (var nested in entry.NoNested)
                        value += ":" + Encoding.ASCII.GetString(nested.NestedID);
                    entries.Add(value);
                }
                return entries.ToArray();
            }
        }
        """;

    private static readonly Lazy<Type> DriverType = new(() =>
    {
        var files = TestSupport.Generate(Dictionary(), out _);
        return TestSupport.EmitAndLoad(files.Select(f => f.content).Append(Driver))
            .GetType("GroupDriver")!;
    });

    private static FixDictionary Dictionary()
    {
        static FixFieldDef Field(int tag, string name, string type = "INT")
            => new(tag, name, type, new List<FixValueDef>());

        var component = new FixComponentDef("Fields", new List<FixEntry>
        {
            new FixFieldRef(Field(1062, "High"), required: true),
            new FixFieldRef(Field(1000, "Low"), required: true),
        });
        var entries = new List<FixEntry>
        {
            new FixFieldRef(Field(9000, "Delimiter", "STRING"), required: true),
            new FixComponentRef(component, required: true),
        };
        for (int tag = 1060; tag > 1000; tag -= 2)
            entries.Add(new FixFieldRef(Field(tag, "Value" + tag), required: false));
        entries.Add(new FixGroupRef("NoNested", Field(9100, "NoNested", "NUMINGROUP"),
            new List<FixEntry>
            {
                new FixFieldRef(Field(9101, "NestedID", "STRING"), required: true),
            }, required: false));
        var group = new FixGroupRef("NoEntries", Field(500, "NoEntries", "NUMINGROUP"),
            entries, required: false);
        return new FixDictionary("FIX", 4, 4, 0,
            header: new List<FixEntry>(), trailer: new List<FixEntry>(),
            messages: new List<FixMessageDef>
            {
                new("LargeMessage", "U1", "app", new List<FixEntry> { group }),
            },
            componentsByName: new Dictionary<string, FixComponentDef> { ["Fields"] = component },
            fieldsByName: new Dictionary<string, FixFieldDef>(),
            fieldsByNumber: new Dictionary<int, FixFieldDef>());
    }

    private static byte[] Frame(string text) => Encoding.ASCII.GetBytes(text.Replace('|', '\x01'));

    private static string[] Slice(string frame, int[] tags, bool sorted)
        => (string[])DriverType.Value.GetMethod("Slice")!
            .Invoke(null, new object[] { Frame(frame), tags, sorted })!;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(32)]
    public void Runtime_PreservesUnsortedApi_AndSortedLookupBoundaries(int size)
    {
        var tags = Enumerable.Range(0, size).Select(i => 1000 + i * 2).ToArray();
        var unsorted = tags.Reverse().ToArray();
        string fields = string.Concat(unsorted.Select(tag => $"{tag}=1|"));
        string first = "9000=A|" + fields;
        string second = "9000=B|" + fields;
        foreach (int unknown in new[] { 999, 1001, 8999 })
        {
            string frame = "500=3|" + first + second + $"{unknown}=outside|9000=C|";
            Assert.Equal(new[] { first, second }, Slice(frame, tags, sorted: true));
            Assert.Equal(new[] { first, second }, Slice(frame, unsorted, sorted: false));
        }
        Assert.Equal(new[] { first, second }, Slice("500=2|" + first + second, tags, sorted: true));
        Assert.Equal(new[] { first }, Slice("500=1|" + first + second, tags, sorted: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("500=0|9000=A|")]
    [InlineData("500=-1|9000=A|")]
    [InlineData("500=1|1000=1|")]
    [InlineData("500=1|")]
    public void Runtime_EmptyOrMissingGroupDoesNotYieldEntries(string frame)
    {
        var tags = Enumerable.Range(0, 32).Select(i => 1000 + i * 2).ToArray();
        Assert.Empty(Slice(frame, tags, sorted: true));
        Assert.Empty(Slice(frame, tags.Reverse().ToArray(), sorted: false));
    }

    [Fact]
    public void GeneratedLargeGroup_PreservesDelimiter_Components_AndNestedGroups()
    {
        var files = TestSupport.Generate(Dictionary(), out _);
        string source = string.Join("\n", files.Select(f => f.content));
        Assert.Contains("EntryTags = new int[] { 1000, 1002, 1004", source);
        Assert.Contains("500, 9000, EntryTags, sortedEntryTags: true", source);
        var frame = Frame("500=3|9000=A|1062=62|1000=1|9100=2|9101=N1|9101=N2|"
            + "9000=B|1000=2|1062=63|9100=1|9101=N3|999=outside|9000=C|");
        var result = (string[])DriverType.Value.GetMethod("Read")!.Invoke(null, new object[] { frame })!;
        Assert.Equal(new[] { "A:1:62:N1:N2", "B:2:63:N3" }, result);
    }
}
