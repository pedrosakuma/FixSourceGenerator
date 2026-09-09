using System;
using System.Buffers.Text;
using Compatibility.Fix.V44;
using Compatibility.Fix.V44.Runtime;

namespace FixSourceGenerator.Compatibility;

// Build-only consumer: the solution's CI build checks actual .NET 6 reference assemblies and C# 11.
public static class StackInputCompilation
{
    public static int Encode(Span<byte> destination, long orderId)
    {
        var writer = new NewOrderSingleWriter(destination);
        Span<byte> scratch = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(orderId, scratch, out int written))
            throw new InvalidOperationException("ID formatting failed.");
        writer.WriteClOrdID(scratch[..written]);
        writer.WriteSenderCompID(scratch[..written]);
        writer.WriteSecurityID(scratch[..written]);
        writer.WritePrice(long.MinValue, 18);
        writer.WriteOrderQty(long.MaxValue);
        writer.WritePrice(123.4500m);
        WriteParty(ref writer, orderId);
        return writer.Finish();
    }

    private static void WriteParty(ref NewOrderSingleWriter writer, long partyId)
    {
        Span<byte> scratch = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(partyId, scratch, out int written))
            throw new InvalidOperationException("ID formatting failed.");
        writer.WriteNoPartyIDs(1);
        writer.WritePartyID(scratch[..written]);
    }

    public static int EncodeRuntime(Span<byte> destination)
    {
        var writer = new FixSpanWriter(destination);
        Span<byte> begin = stackalloc byte[] { 70, 73, 88, 46, 52, 46, 52 };
        Span<byte> type = stackalloc byte[] { 68 };
        writer.BeginMessage(begin, type);
        WriteRuntimeId(ref writer);
        writer.WriteField(52, DateTime.MinValue);
        writer.WriteField(75, DateOnly.MinValue);
        writer.WriteField(273, TimeOnly.MinValue);
        return writer.Finish();
    }

    private static void WriteRuntimeId(ref FixSpanWriter writer)
    {
        Span<byte> scratch = stackalloc byte[20];
        if (!Utf8Formatter.TryFormat(long.MinValue, scratch, out int written))
            throw new InvalidOperationException("ID formatting failed.");
        writer.WriteField(11, scratch[..written]);
    }
}
