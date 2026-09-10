using FixSourceGenerator.Attributes;

[FixView("NewOrderSingle")]
internal readonly ref partial struct OrderRoutingView
{
    public partial ReadOnlySpan<byte> ClOrdID { get; }
    public partial decimal? Price { get; }
    public partial Acme.Fix.V44.NoPartyIDsGroupReader NoPartyIDs { get; }
}

[FixView("NewOrderSingle.NoPartyIDs")]
internal readonly ref partial struct PartyView
{
    public partial ReadOnlySpan<byte> PartyID { get; }
    public partial int? PartyRole { get; }
}

internal static class ProjectionExample
{
    internal static void Check(ReadOnlySpan<byte> frame, decimal? price)
    {
        var order = new OrderRoutingView(frame);
        if (!order.ClOrdID.SequenceEqual("42"u8) || order.Price != price || order.NoPartyIDs.Count != 1)
            throw new InvalidOperationException("Projected order values differ.");
        var iterator = order.NoPartyIDs.GetEnumerator();
        int entries = 0;
        while (iterator.MoveNext())
        {
            var party = new PartyView(iterator.CurrentSpan);
            if (!party.PartyID.SequenceEqual("PARTY-1"u8) || party.PartyRole != 1)
                throw new InvalidOperationException("Projected party values differ.");
            entries++;
        }
        if (entries != 1)
            throw new InvalidOperationException("Expected one projected party entry.");
    }
}
