using Acme.Fix.V44;
using Acme.Fix.V44.Runtime;

internal static class TransformationExample
{
    // Input must already be a validated order for FIX44-mini, not arbitrary FIX traffic.
    internal static int Rewrite(ReadOnlySpan<byte> source, Span<byte> destination,
        Span<FixWriterState> state, scoped ReadOnlySpan<byte> newOrderId,
        int outgoingSequence, DateTime sendingTime, decimal priceDelta, int? partyRoleFilter)
    {
        if (source.Overlaps(destination))
            throw new ArgumentException("Input and output buffers must not overlap.", nameof(destination));

        var order = new NewOrderSingleReader(source);
        var instrument = order.Instrument;
        decimal? price = order.Price;
        decimal quantity = order.OrderQty;
        var side = order.Side;
        var orderType = order.OrdType;
        if (price.HasValue)
            price += priceDelta;

        int count = order.NoPartyIDs.Count;
        if (partyRoleFilter.HasValue)
        {
            // The writer needs the output count before the first entry is emitted.
            count = 0;
            foreach (var party in order.NoPartyIDs)
                if (party.PartyRole == partyRoleFilter.Value)
                    count++;
        }

        var message = new NewOrderSingleWriter(destination, state, "ROUTER"u8, "VENUE"u8,
            outgoingSequence, sendingTime, newOrderId);
        var instrumentWriter = message.BeginInstrument(instrument.Symbol);
        var instrumentEnd = instrument.TryGetSecurityID(out var securityId)
            ? instrumentWriter.WriteSecurityID(securityId)
            : instrumentWriter.SkipSecurityID();
        var tail = instrumentEnd.EndInstrument(side, quantity, orderType);
        if (price.HasValue)
            tail.SetPrice(price.Value);

        // This example normalizes an absent group to an explicit count of zero.
        var group = tail.BeginNoPartyIDs(count);
        foreach (var party in order.NoPartyIDs)
        {
            int? role = party.PartyRole;
            if (partyRoleFilter.HasValue && role != partyRoleFilter.Value)
                continue;

            char? idSource = party.PartyIDSource;
            var entry = group.BeginEntry(party.PartyID);
            if (idSource.HasValue)
                entry.SetPartyIDSource(idSource.Value);
            if (role.HasValue)
                entry.SetPartyRole(role.Value);
            group = entry.EndEntry();
        }
        return group.EndGroup().Finish();
    }
}
