using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Native = FixSourceGenerator.Benchmarks.Generated.Fix.V42;
using Runtime = FixSourceGenerator.Benchmarks.Generated.Fix.V42.Runtime;

namespace FixSourceGenerator.Benchmarks;

internal enum DirectEdit { None, Scalar, Grow, Shrink, FourEdits, FilterSome, FilterAll, FilterNone }
internal enum DirectDensity { Dense, Sparse, Mixed }

// Full controlled UND messages only. Source bytes are immutable and never alias the destination.
internal sealed class DirectWriterWorkload
{
    private static readonly string[] Replacements =
        ["G", "IDENTIFIER-GROWN-0000000000000000000000000000", "MID", "FINAL-IDENTIFIER"];
    private static readonly byte[][] ReplacementBytes = Replacements.Select(Encoding.UTF8.GetBytes).ToArray();
    private const string GrowingId = "AÇÃO-IDENTIFIER-GROWN-0000000000000000000000";
    private static readonly byte[] GrowingBytes = Encoding.UTF8.GetBytes(GrowingId);
    private readonly NativeRoundtripWorkload _adapter;
    private readonly Runtime.FixWriterState[] _state =
        new Runtime.FixWriterState[Native.NativeEnvelopeWriter.RequiredStateLength];
    internal readonly byte[] Input;
    internal byte[] Output;
    internal readonly int Count;
    internal readonly DirectEdit Edit;
    internal readonly DirectDensity Density;
    internal readonly bool FreshOutput;
    internal readonly byte[] Expected;
    internal readonly int Survivors;

    internal DirectWriterWorkload(int count, DirectEdit edit, DirectDensity density = DirectDensity.Dense,
        bool freshOutput = false)
    {
        Count = count;
        Edit = edit;
        Density = density;
        FreshOutput = freshOutput;
        _adapter = new NativeRoundtripWorkload(count, density != DirectDensity.Sparse);
        Runtime.FixWriterState.Initialize(_state);
        if (density == DirectDensity.Mixed)
            for (int i = 0; i < count; i++)
                _adapter.Dto.Entries[i].Materialize($"ENTRY-{i:D3}",
                    i % 3 == 0 ? null : i % 3 == 1 ? 0m : 101.25m + i,
                    i % 3 == 0 ? 0m : i % 3 == 1 ? null : 100m);
        Input = _adapter.Output.AsSpan(0, _adapter.Write(_adapter.Dto, _adapter.Output)).ToArray();
        Output = _adapter.Output;
        NativeDtoExperiments.Require(Input.SequenceEqual(NativeRoundtripWorkload.Oracle(_adapter.Dto)),
            "Direct source canonical oracle");
        // Expected semantics start from the fixture's native values, NOT from either decoder.
        // Deliberately do not call the timed edit or predicate helpers here.
        for (int i = _adapter.Dto.Entries.Count - 1; i >= 0; i--)
        {
            var row = _adapter.Dto.Entries[i];
            bool remove = edit == DirectEdit.FilterNone ||
                (edit == DirectEdit.FilterSome &&
                 ((row.Presence & NativeEntryDto.PriceBit) == 0 || row.Price < 120m));
            if (remove) _adapter.Dto.Entries.RemoveAt(i);
        }
        if (_adapter.Dto.Entries.Count > 0)
        {
            var first = _adapter.Dto.Entries[0];
            switch (edit)
            {
                case DirectEdit.Scalar: first.SetPrice(0m); first.SetSize(null); break;
                case DirectEdit.Grow: first.SetId(GrowingId); break;
                case DirectEdit.Shrink: first.SetId("Z"); break;
                case DirectEdit.FourEdits: first.SetId("FINAL-IDENTIFIER"); first.SetPrice(102.5m); break;
            }
        }
        Survivors = _adapter.Dto.Entries.Count;
        Expected = NativeRoundtripWorkload.Oracle(_adapter.Dto);
    }

    private readonly ref struct Header
    {
        internal readonly ReadOnlySpan<byte> Sender, Target;
        internal readonly int Sequence, Count;
        internal readonly DateTime SendingTime;

        internal Header(ReadOnlySpan<byte> source)
        {
            Sender = Target = default;
            Sequence = Count = 0;
            SendingTime = default;
            int position = 0;
            while (Runtime.FixSpanReader.TryReadField(source, position, out int tag,
                out int start, out int length, out int next))
            {
                var value = source.Slice(start, length);
                switch (tag)
                {
                    case 49: Sender = value; break;
                    case 56: Target = value; break;
                    case 34: Sequence = Runtime.FixSpanReader.ParseInt(value); break;
                    case 52: SendingTime = Runtime.FixSpanReader.ParseDateTime(value); break;
                    case 100: Count = Runtime.FixSpanReader.ParseInt(value); return;
                }
                position = next;
            }
            throw new InvalidOperationException("Controlled UND header is incomplete");
        }
    }

    private bool IsFilter => Edit is DirectEdit.FilterSome or DirectEdit.FilterAll or DirectEdit.FilterNone;
    private bool Keep(decimal? price) => Edit switch
    {
        DirectEdit.FilterNone => false,
        DirectEdit.FilterSome => price.HasValue && price.Value >= 120m,
        _ => true,
    };

    private void Numbers(ref decimal? price, ref decimal? size, int step)
    {
        if (Edit == DirectEdit.Scalar) { price = 0m; size = null; }
        if (Edit == DirectEdit.FourEdits)
            price = step == 1 ? null : step == 2 ? 0m : 102.5m;
    }

    internal int DirectReadWrite()
    {
        var header = new Header(Input);
        int count = header.Count;
        if (IsFilter)
        {
            count = 0;
            foreach (var row in new Native.NativeEnvelopeReader(Input).NoEntries)
                if (Keep(row.Price)) count++;
        }
        if (FreshOutput) Output = new byte[_adapter.Output.Length];
        var writer = new Native.NativeEnvelopeWriter(Output, _state,
            header.Sender, header.Target, header.Sequence, header.SendingTime);
        var group = writer.BeginNoEntries(count);
        int index = 0;
        foreach (var row in new Native.NativeEnvelopeReader(Input).NoEntries)
        {
            decimal? price = row.Price;
            if (IsFilter && !Keep(price)) continue;
            decimal? size = row.Size;
            ReadOnlySpan<byte> id = row.EntryID;
            if (index++ == 0)
            {
                for (int step = 0; step < (Edit == DirectEdit.FourEdits ? 4 : 1); step++)
                {
                    Numbers(ref price, ref size, step);
                    if (Edit == DirectEdit.Grow) id = GrowingBytes;
                    if (Edit == DirectEdit.Shrink) id = "Z"u8;
                    if (Edit == DirectEdit.FourEdits) id = ReplacementBytes[step];
                }
            }
            var entry = group.BeginEntry(id);
            if (price.HasValue) entry.SetPrice(price.Value);
            if (size.HasValue) entry.SetSize(size.Value);
            group = entry.EndEntry();
        }
        return Native.FixWriterScopeExtensions.EndGroup(group).Finish();
    }

    internal int DtoFreshReadWrite()
    {
        // Same header scan/native parsers as Direct; no old timestamp-string parsing penalty.
        var header = new Header(Input);
        var dto = new NativeMessageDto(header.Count)
        {
            Sender = Encoding.UTF8.GetString(header.Sender),
            Target = Encoding.UTF8.GetString(header.Target),
            Sequence = header.Sequence, SendingTime = header.SendingTime,
        };
        foreach (var row in new Native.NativeEnvelopeReader(Input).NoEntries)
        {
            var owned = new NativeEntryDto();
            owned.Materialize(Encoding.UTF8.GetString(row.EntryID), row.Price, row.Size);
            dto.Entries.Add(owned);
        }
        if (IsFilter)
        {
            int kept = 0;
            for (int i = 0; i < dto.Entries.Count; i++)
            {
                var row = dto.Entries[i];
                if (Keep((row.Presence & NativeEntryDto.PriceBit) != 0 ? row.Price : null))
                    dto.Entries[kept++] = row;
            }
            dto.Entries.RemoveRange(kept, dto.Entries.Count - kept);
        }
        if (dto.Entries.Count > 0)
        {
            var first = dto.Entries[0];
            decimal? price = (first.Presence & NativeEntryDto.PriceBit) != 0 ? first.Price : null;
            decimal? size = (first.Presence & NativeEntryDto.SizeBit) != 0 ? first.Size : null;
            for (int step = 0; step < (Edit == DirectEdit.FourEdits ? 4 : 1); step++)
            {
                Numbers(ref price, ref size, step);
                if (Edit is DirectEdit.Scalar or DirectEdit.FourEdits)
                {
                    first.SetPrice(price);
                    first.SetSize(size);
                }
                if (Edit == DirectEdit.Grow) first.SetId(GrowingId);
                if (Edit == DirectEdit.Shrink) first.SetId("Z");
                if (Edit == DirectEdit.FourEdits) first.SetId(Replacements[step]);
            }
        }
        if (FreshOutput) Output = new byte[_adapter.Output.Length];
        return _adapter.Write(dto, Output);
    }
}

internal static class DirectWriterExperiments
{
    internal static void Check()
    {
        int checks = 0;
        foreach (int count in new[] { 0, 1, 10, 50 })
            foreach (var density in Enum.GetValues<DirectDensity>())
                foreach (var edit in Enum.GetValues<DirectEdit>())
                    foreach (bool fresh in new[] { false, true })
                    {
                        var work = new DirectWriterWorkload(count, edit, density, fresh);
                        byte[] original = work.Input.ToArray();
                        for (int repeat = 0; repeat < 3; repeat++)
                            foreach (var action in new Func<int>[] { work.DirectReadWrite, work.DtoFreshReadWrite })
                            {
                                int length = action();
                                var output = work.Output.AsSpan(0, length);
                                NativeDtoExperiments.Require(!work.Input.AsSpan().Overlaps(output) &&
                                    work.Input.SequenceEqual(original), "Source lifetime/nonaliasing/immutability");
                                NativeDtoExperiments.Require(output.SequenceEqual(work.Expected), "Independent complete byte oracle");
                                if (edit == DirectEdit.None)
                                    NativeDtoExperiments.Require(output.SequenceEqual(original), "Canonical full no-edit roundtrip");
                                CheckEnvelope(output);
                                int rows = 0;
                                foreach (var row in new Native.NativeEnvelopeReader(output).NoEntries)
                                {
                                    NativeDtoExperiments.Require(!row.EntryID.IsEmpty, "Required identifier");
                                    rows++;
                                }
                                NativeDtoExperiments.Require(rows == work.Survivors, "Actual filtered group count");
                                checks++;
                            }
                    }
        Console.WriteLine($"Direct writer checks: {checks} full oracle frames; dense/sparse/mixed, zero/all/some survivors, UTF8, absence/zero, fresh/reused output and writer state.");
    }

    private static void CheckEnvelope(ReadOnlySpan<byte> frame)
    {
        int first = frame.IndexOf((byte)1), second = frame[(first + 1)..].IndexOf((byte)1) + first + 1;
        int trailer = frame.Length - 7;
        int declared = int.Parse(frame[(first + 3)..second], CultureInfo.InvariantCulture);
        int sum = 0;
        foreach (byte value in frame[..trailer]) sum += value;
        NativeDtoExperiments.Require(frame.Slice(trailer, 3).SequenceEqual("10="u8) &&
            frame[^1] == 1 && declared == trailer - second - 1 &&
            int.Parse(frame.Slice(trailer + 3, 3), CultureInfo.InvariantCulture) == (sum & 255),
            "Independent BodyLength/checksum");
    }

    internal static void Load(bool confirmation = false)
    {
        Check();
        double warmup = confirmation ? 1 : .2, block = confirmation ? 1 : .1;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Environment = "direct-writer", Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ServerGC = System.Runtime.GCSettings.IsServerGC, Stopwatch.Frequency,
            Confirmation = confirmation, WarmupSeconds = warmup, BlockSeconds = block, Rounds = 8,
        }));
        var cases = new List<Case>();
        if (confirmation)
        {
            foreach (var edit in new[] { DirectEdit.None, DirectEdit.FourEdits, DirectEdit.FilterSome })
                cases.Add(new(new(50, edit)));
            cases.Add(new(new(50, DirectEdit.FilterSome, DirectDensity.Mixed)));
        }
        else
        {
            foreach (int count in new[] { 1, 10, 50 })
                foreach (var edit in new[] { DirectEdit.None, DirectEdit.Scalar, DirectEdit.Grow,
                    DirectEdit.Shrink, DirectEdit.FourEdits, DirectEdit.FilterSome })
                    cases.Add(new(new(count, edit)));
            foreach (var edit in new[] { DirectEdit.None, DirectEdit.Scalar, DirectEdit.FilterSome })
                cases.Add(new(new(50, edit, DirectDensity.Mixed)));
            cases.Add(new(new(50, DirectEdit.FourEdits, freshOutput: true)));
        }
        long sink = 0;
        foreach (var item in cases)
            foreach (var action in item.Actions) Measure(action, warmup, ref sink);
        for (int round = 0; round < 8; round++)
            foreach (var item in cases)
                for (int turn = 0; turn < 2; turn++)
                {
                    int variant = (round + turn) % 2;
                    item.Samples[variant].Add(Measure(item.Actions[variant], block, ref sink));
                }
        foreach (var item in cases)
            for (int variant = 0; variant < 2; variant++)
            {
                var samples = item.Samples[variant];
                double[] ordered = samples.Select(sample => sample.Us).Order().ToArray();
                var work = item.Work;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    Case = $"UND{work.Count}/{work.Density}/{work.Edit}/{(work.FreshOutput ? "fresh-output" : "reuse-output")}",
                    Method = variant == 0 ? "DirectReadWrite" : "DtoFreshReadWrite",
                    InputBytes = work.Input.Length, OutputBytes = work.Expected.Length,
                    InputEntries = work.Count, OutputEntries = work.Survivors,
                    DirectPriceParses = work.Edit == DirectEdit.FilterSome ? 2 * work.Count : work.Count,
                    DirectSizeParses = work.Survivors, DtoPriceParses = work.Count, DtoSizeParses = work.Count,
                    MedianBlockUs = (ordered[3] + ordered[4]) / 2, MinBlockUs = ordered[0], MaxBlockUs = ordered[^1],
                    Samples = samples,
                    BytesPerOperation = samples.Sum(sample => sample.Bytes) / (double)samples.Sum(sample => sample.Operations),
                    Gen0 = samples.Sum(sample => sample.Gen0), Gen1 = samples.Sum(sample => sample.Gen1),
                    Gen2 = samples.Sum(sample => sample.Gen2),
                }));
            }
        Console.WriteLine($"Sink={sink}");
    }

    private static Sample Measure(Func<int> action, double seconds, ref long sink)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), operations = 0;
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long start = Stopwatch.GetTimestamp();
        do
        {
            for (int i = 0; i < 32; i++) sink += action();
            operations += 32;
        } while (Stopwatch.GetElapsedTime(start).TotalSeconds < seconds);
        double us = Stopwatch.GetElapsedTime(start).TotalSeconds * 1e6 / operations;
        return new(us, operations, GC.GetAllocatedBytesForCurrentThread() - allocated,
            GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
    }

    private sealed record Sample(double Us, long Operations, long Bytes, int Gen0, int Gen1, int Gen2);
    private sealed class Case(DirectWriterWorkload work)
    {
        internal DirectWriterWorkload Work => work;
        internal Func<int>[] Actions { get; } = [work.DirectReadWrite, work.DtoFreshReadWrite];
        internal List<Sample>[] Samples { get; } = [new(8), new(8)];
    }
}
