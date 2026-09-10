using System.Globalization;
using System.Text;
using Acme.Fix.V44;
using Acme.Fix.V44.Runtime;

internal static class TransformationExampleChecks
{
    private static readonly DateTime SendingTime = new(2024, 1, 15, 10, 30, 6, DateTimeKind.Utc);

    internal static void Run()
    {
        Span<byte> sourceBuffer = stackalloc byte[1024];
        Span<byte> output = stackalloc byte[1024];
        Span<FixWriterState> sourceState = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
        Span<FixWriterState> outputState = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
        NewOrderSingleWriter.InitializeState(sourceState);
        NewOrderSingleWriter.InitializeState(outputState);

        foreach (decimal? price in new decimal?[] { null, 0m, 101.25m })
        foreach (bool securityId in new[] { false, true })
        {
            int inputLength = Encode(sourceBuffer, sourceState, price, securityId);
            ReadOnlySpan<byte> source = sourceBuffer[..inputLength];
            byte[] original = source.ToArray();
            foreach (int? filter in new int?[] { null, 1, 2, 9 })
            foreach (decimal delta in new[] { 0m, 0.25m })
            foreach (bool growId in new[] { false, true })
            {
                ReadOnlySpan<byte> newId = growId ? "42-ROUTED-TO-ANOTHER-VENUE"u8 : "X"u8;
                int length = TransformationExample.Rewrite(source, output, outputState,
                    newId, 8, SendingTime, delta, filter);
                Check(output[..length], newId, price.HasValue ? price.Value + delta : null,
                    securityId, filter);
                if (!source.SequenceEqual(original))
                    throw new InvalidOperationException("Transformation modified the input frame.");
            }

            try
            {
                TransformationExample.Rewrite(source, sourceBuffer, outputState, "X"u8,
                    8, SendingTime, 0m, null);
                throw new InvalidOperationException("Overlapping buffers were accepted.");
            }
            catch (ArgumentException error) when (error.ParamName == "destination")
            {
            }

            try
            {
                TransformationExample.Rewrite(source, output[..8], outputState, "X"u8,
                    8, SendingTime, 0m, null);
                throw new InvalidOperationException("An undersized destination was accepted.");
            }
            catch (ArgumentException error) when (error.ParamName == "destination")
            {
            }
        }

        // Optional group presence is deliberately normalized, not preserved byte-for-byte.
        var emptyMessage = new NewOrderSingleWriter(sourceBuffer, sourceState, "SENDER"u8,
            "TARGET"u8, 7, SendingTime, "42"u8);
        int absentLength = emptyMessage.BeginInstrument("MSFT"u8).SkipSecurityID()
            .EndInstrument(Side.Buy, 100m, OrdType.Limit).Finish();
        int emptyLength = TransformationExample.Rewrite(sourceBuffer[..absentLength], output,
            outputState, "X"u8, 8, SendingTime, 0m, null);
        Check(output[..emptyLength], "X"u8, null, false, 9);
        Console.WriteLine("Transformed orders with borrowed text, optional values and counted party filtering.");
    }

    private static int Encode(Span<byte> destination, Span<FixWriterState> state,
        decimal? price, bool securityId)
    {
        var message = new NewOrderSingleWriter(destination, state, "SENDER"u8, "TARGET"u8,
            7, SendingTime, "42"u8);
        var instrument = message.BeginInstrument("MSFT"u8);
        var end = securityId ? instrument.WriteSecurityID("US5949181045"u8) : instrument.SkipSecurityID();
        var tail = end.EndInstrument(Side.Buy, 100m, OrdType.Limit);
        if (price.HasValue)
            tail.SetPrice(price.Value);
        var group = tail.BeginNoPartyIDs(4);
        var first = group.BeginEntry("KEEP-1"u8);
        first.SetPartyIDSource('D');
        first.SetPartyRole(1);
        group = first.EndEntry();
        group = group.BeginEntry("NO-ROLE"u8).EndEntry();
        var second = group.BeginEntry("KEEP-2"u8);
        second.SetPartyRole(1);
        group = second.EndEntry();
        var other = group.BeginEntry("OTHER"u8);
        other.SetPartyIDSource('D');
        other.SetPartyRole(2);
        return other.EndEntry().EndGroup().Finish();
    }

    private static void Check(ReadOnlySpan<byte> frame, ReadOnlySpan<byte> orderId,
        decimal? price, bool securityId, int? filter)
    {
        var reader = new NewOrderSingleReader(frame);
        if (!reader.ClOrdID.SequenceEqual(orderId) || reader.Price != price ||
            reader.Side != Side.Buy || reader.OrdType != OrdType.Limit || reader.OrderQty != 100m ||
            !reader.Instrument.Symbol.SequenceEqual("MSFT"u8) ||
            reader.Instrument.TryGetSecurityID(out var actualSecurityId) != securityId ||
            (securityId && !actualSecurityId.SequenceEqual("US5949181045"u8)))
            throw new InvalidOperationException("Transformed order fields do not match.");

        string[] expectedIds = filter switch
        {
            null => new[] { "KEEP-1", "NO-ROLE", "KEEP-2", "OTHER" },
            1 => new[] { "KEEP-1", "KEEP-2" },
            2 => new[] { "OTHER" },
            _ => Array.Empty<string>(),
        };
        if (reader.NoPartyIDs.Count != expectedIds.Length)
            throw new InvalidOperationException("Transformed party count does not match.");
        int index = 0;
        foreach (var party in reader.NoPartyIDs)
        {
            string id = Encoding.ASCII.GetString(party.PartyID);
            int? expectedRole = id == "NO-ROLE" ? null : id == "OTHER" ? 2 : 1;
            char? expectedSource = id == "KEEP-1" || id == "OTHER" ? 'D' : null;
            if (index >= expectedIds.Length || id != expectedIds[index++] ||
                party.PartyRole != expectedRole || party.PartyIDSource != expectedSource)
                throw new InvalidOperationException("Transformed party fields do not match.");
        }
        if (index != expectedIds.Length)
            throw new InvalidOperationException("Transformed party entries are incomplete.");

        string text = Encoding.ASCII.GetString(frame);
        int lengthStart = text.IndexOf("\x01" + "9=", StringComparison.Ordinal) + 3;
        int bodyStart = text.IndexOf('\x01', lengthStart) + 1;
        int checksumStart = text.LastIndexOf("\x01" + "10=", StringComparison.Ordinal) + 1;
        int bodyLength = int.Parse(text[lengthStart..(bodyStart - 1)], CultureInfo.InvariantCulture);
        int checksum = int.Parse(text[(checksumStart + 3)..^1], CultureInfo.InvariantCulture);
        int sum = 0;
        foreach (byte value in frame[..checksumStart])
            sum += value;
        if (bodyLength != checksumStart - bodyStart || checksum != (sum & 255) ||
            !text.Contains("\x01" + "49=ROUTER\x01", StringComparison.Ordinal) ||
            !text.Contains("\x01" + "56=VENUE\x01", StringComparison.Ordinal) ||
            !text.Contains("\x01" + "34=8\x01", StringComparison.Ordinal) ||
            !text.Contains("\x01" + "52=20240115-10:30:06.000\x01", StringComparison.Ordinal) ||
            !text.Contains("\x01" + "453=" + expectedIds.Length + "\x01", StringComparison.Ordinal))
            throw new InvalidOperationException("Transformed FIX envelope does not match.");
    }
}
