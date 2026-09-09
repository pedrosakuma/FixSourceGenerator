using System.Reflection;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;

DecodeLoad.ValidateMatrix();
CheckMembership(typeof(MDIncGrpReader.NoMDEntriesGroupReader));
CheckMembership(typeof(MDFullGrpReader.NoMDEntriesGroupReader));
Console.WriteLine("PASS: X/W selected fields, group counts, optional absence and UTC Kind; decimal/scaled frames at 1/10/50 entries; membership including gaps and out-of-range tags.");

static void CheckMembership(Type group)
{
    const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
    var tags = (int[])group.GetField("EntryTags", flags)!.GetValue(null)!;
    var expected = new HashSet<int>(tags);
    var hash = (HashSet<int>?)group.GetField("EntryTagSet", flags)?.GetValue(null);
    var bits = (ulong[]?)group.GetField("EntryTagBits", flags)?.GetValue(null);
    if (hash is null && bits is null)
        throw new InvalidOperationException("Experimental lookup metadata is missing.");
    for (int tag = -1; tag <= tags.Max() + 128; tag++)
    {
        int bucket = tag >> 6;
        bool actual = hash is not null ? hash.Contains(tag)
            : (uint)bucket < (uint)bits!.Length && (bits[bucket] & (1UL << (tag & 63))) != 0;
        if (actual != expected.Contains(tag))
            throw new InvalidOperationException($"Membership mismatch: {group.Name}, tag {tag}.");
    }
}
