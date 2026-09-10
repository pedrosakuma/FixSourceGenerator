using Acme.Fix.V44;
using Acme.Fix.V44.Runtime;

internal static class CodecExample
{
    internal static int Encode(Span<byte> destination, Span<FixWriterState> state,
        scoped ReadOnlySpan<byte> orderId, decimal? price)
    {
        var message = new NewOrderSingleWriter(destination, state, "SENDER"u8, "TARGET"u8, 7,
            new DateTime(2024, 1, 15, 10, 30, 5, DateTimeKind.Utc), orderId);
        var instrument = message.BeginInstrument("MSFT"u8);
        var tail = instrument.SkipSecurityID().EndInstrument(Side.Buy,
            FixDecimal.FromScaled(10000, 2), OrdType.Limit);
        if (price.HasValue)
            tail.SetPrice(price.Value);
        var group = tail.BeginNoPartyIDs(1);
        var entry = group.BeginEntry("PARTY-1"u8);
        entry.SetPartyIDSource('D');
        entry.SetPartyRole(1);
        group = entry.EndEntry();
        return group.EndGroup().Finish();
    }

    internal static void Check(ReadOnlySpan<byte> frame, decimal? price)
    {
        var order = new NewOrderSingleReader(frame);
        if (!order.ClOrdID.SequenceEqual("42"u8) || order.Price != price ||
            order.OrderQty != 100m || order.Side != Side.Buy ||
            !order.Instrument.Symbol.SequenceEqual("MSFT"u8) ||
            order.Instrument.TryGetSecurityID(out _) || order.NoPartyIDs.Count != 1)
            throw new InvalidOperationException("Full reader values do not match the encoded order.");
        int entries = 0;
        foreach (var party in order.NoPartyIDs)
        {
            if (!party.PartyID.SequenceEqual("PARTY-1"u8) || party.PartyRole != 1)
                throw new InvalidOperationException("Party values do not match the encoded entry.");
            entries++;
        }
        if (entries != 1)
            throw new InvalidOperationException("Expected one complete party entry.");
    }
}
