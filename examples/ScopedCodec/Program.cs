using System.Buffers.Text;
using Acme.Fix.V44;
using Acme.Fix.V44.Runtime;

Span<byte> destination = stackalloc byte[512];
Span<FixWriterState> state = stackalloc FixWriterState[NewOrderSingleWriter.RequiredStateLength];
NewOrderSingleWriter.InitializeState(state);
Span<byte> scratch = stackalloc byte[20];

foreach (decimal? price in new decimal?[] { null, 0m, 101.25m })
{
    if (!Utf8Formatter.TryFormat(42L, scratch, out int written))
        throw new InvalidOperationException("Order ID formatting failed.");
    int length = CodecExample.Encode(destination, state, scratch[..written], price);
    scratch.Clear();
    ReadOnlySpan<byte> frame = destination[..length];
    CodecExample.Check(frame, price);
#if NET9_0_OR_GREATER
    ProjectionExample.Check(frame, price);
#endif
    Console.WriteLine($"Encoded {length} bytes; price={price?.ToString() ?? "absent"}.");
}
