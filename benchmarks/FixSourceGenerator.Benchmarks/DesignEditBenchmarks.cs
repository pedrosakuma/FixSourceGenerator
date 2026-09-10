using System.Buffers.Text;
using System.Runtime.CompilerServices;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;

namespace FixSourceGenerator.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0)]
public class DesignEditBenchmarks
{
    private byte[] _original = null!, _output = null!;
    private Field[] _fields = null!;
    private int[] _starts = null!, _lengths = null!, _lastEdit = null!;
    private Edit[] _edits = null!;
    private int _length;
    private int _bodyIndex, _checksumIndex;

    [Params("Small", "X1", "X10", "X50", "W1", "W10", "W50")]
    public string Scenario { get; set; } = "Small";

    [Params("None", "Equal1", "Grow1", "Shrink1", "Equal8", "Grow8", "Shrink8")]
    public string Change { get; set; } = "None";

    [Params("First", "Last", "Spread")]
    public string Placement { get; set; } = "First";

    internal long CopiedBytes { get; private set; }
    internal int IndexedBytes { get; private set; }
    internal int DistinctEditedFields => _edits.Select(edit => edit.Field).Distinct().Count();
    internal int RetainedPayloadBytes => _original.Length + _output.Length +
        _fields.Length * Unsafe.SizeOf<Field>() + (_starts.Length + _lengths.Length + _lastEdit.Length) * sizeof(int) +
        _edits.Length * Unsafe.SizeOf<Edit>() + _edits.Sum(edit => edit.Value.Length);
    internal int InputBytes => _original.Length;
    internal int OutputCapacity => _output.Length;

    [GlobalSetup]
    public void Setup() => Initialize(new CombinedCodecWorkload(Scenario).Frame.ToArray());

    internal void Initialize(byte[] original)
    {
        _original = original;
        var fields = new List<Field>();
        int pos = 0;
        while (FixSpanReader.TryReadField(original, pos, out int tag, out int start, out int length, out int next))
        {
            fields.Add(new(tag, pos, start, length, next));
            pos = next;
        }
        if (pos != original.Length || fields.Count < 4 || fields[1].Tag != 9 || fields[^1].Tag != 10)
            throw new InvalidOperationException("Edit fixture is not a complete FIX envelope.");
        _fields = fields.ToArray();
        _bodyIndex = 1;
        _checksumIndex = fields.Count - 1;
        int[] eligible = fields.Select((field, index) => (field, index))
            .Where(item => item.field.Tag is 11 or 37 or 55 or 101 or 278 or 448)
            .Select(item => item.index).ToArray();
        if (eligible.Length == 0)
            throw new InvalidOperationException("No editable fixture identifiers.");
        int count = Change == "None" ? 0 : Change.EndsWith('8') ? 8 : 1;
        if (Change != "None" && !new[] { "Equal1", "Grow1", "Shrink1", "Equal8", "Grow8", "Shrink8" }.Contains(Change))
            throw new ArgumentException("Unknown edit scenario.", nameof(Change));
        _edits = new Edit[count];
        for (int i = 0; i < count; i++)
        {
            int unique = Math.Min(count, eligible.Length);
            int index = Placement switch
            {
                "First" => eligible[i % eligible.Length],
                "Last" => eligible[eligible.Length - 1 - i % eligible.Length],
                "Spread" => eligible[(i % unique) * (eligible.Length - 1) / Math.Max(1, unique - 1)],
                _ => throw new ArgumentException("Unknown edit placement.", nameof(Placement)),
            };
            int length = Change.StartsWith("Grow", StringComparison.Ordinal) ? fields[index].Length + 32 * (i + 1) :
                Change.StartsWith("Shrink", StringComparison.Ordinal) ? Math.Max(1, fields[index].Length - i - 1) :
                fields[index].Length;
            _edits[i] = new(index, Enumerable.Repeat((byte)('A' + i), length).ToArray());
        }
        _output = new byte[original.Length + _edits.Sum(edit => edit.Value.Length) + 32];
        _starts = new int[fields.Count];
        _lengths = new int[fields.Count];
        _lastEdit = new int[fields.Count];
        byte[] expected = Oracle();
        foreach (Action run in new Action[] { () => ShiftEach(), () => RebuildIndexed(), () => ScanAndRebuild(), () => ScanAndShift() })
        {
            run();
            if (!_output.AsSpan(0, _length).SequenceEqual(expected))
                throw new InvalidOperationException("Edit result differs from independent envelope reconstruction.");
        }
    }

    [Benchmark(Baseline = true)]
    public int ShiftEach()
    {
        CopiedBytes = IndexedBytes = 0;
        _original.CopyTo(_output, 0);
        CopiedBytes = _length = _original.Length;
        for (int i = 0; i < _fields.Length; i++)
        {
            _starts[i] = _fields[i].ValueStart;
            _lengths[i] = _fields[i].Length;
        }
        foreach (var edit in _edits)
            Replace(edit.Field, edit.Value);
        return Finish();
    }

    [Benchmark]
    public int RebuildIndexed()
    {
        CopiedBytes = IndexedBytes = 0;
        return Rebuild();
    }

    [Benchmark]
    public int ScanAndRebuild()
    {
        CopiedBytes = IndexedBytes = 0;
        Index();
        return Rebuild();
    }

    [Benchmark]
    public int ScanAndShift()
    {
        Index();
        int length = ShiftEach();
        IndexedBytes = _original.Length;
        return length;
    }

    private void Index()
    {
        int pos = 0, index = 0;
        while (FixSpanReader.TryReadField(_original, pos, out int tag, out int start, out int length, out int next))
        {
            if (index >= _fields.Length)
                throw new InvalidOperationException("Fixture field count changed.");
            _fields[index++] = new(tag, pos, start, length, next);
            pos = next;
        }
        if (pos != _original.Length || index != _fields.Length)
            throw new InvalidOperationException("Fixture indexing failed.");
        IndexedBytes = pos;
    }

    private int Rebuild()
    {
        Array.Fill(_lastEdit, -1);
        for (int i = 0; i < _edits.Length; i++)
            _lastEdit[_edits[i].Field] = i;
        _length = 0;
        int cursor = 0;
        for (int i = 0; i < _fields.Length; i++)
        {
            int edit = _lastEdit[i];
            if (edit >= 0)
            {
                var field = _fields[i];
                Copy(_original.AsSpan(cursor, field.ValueStart - cursor));
                Copy(_edits[edit].Value);
                cursor = field.ValueStart + field.Length;
            }
        }
        Copy(_original.AsSpan(cursor));
        _starts[_bodyIndex] = _fields[_bodyIndex].ValueStart;
        _lengths[_bodyIndex] = _fields[_bodyIndex].Length;
        _starts[_checksumIndex] = _fields[_checksumIndex].ValueStart + _length - _original.Length;
        _lengths[_checksumIndex] = _fields[_checksumIndex].Length;
        return Finish();
    }

    private void Copy(ReadOnlySpan<byte> value)
    {
        value.CopyTo(_output.AsSpan(_length));
        _length += value.Length;
        CopiedBytes += value.Length;
    }

    private void Replace(int field, ReadOnlySpan<byte> value)
    {
        int start = _starts[field], oldLength = _lengths[field];
        int delta = value.Length - oldLength;
        if (delta != 0)
        {
            int tail = start + oldLength, count = _length - tail;
            _output.AsSpan(tail, count).CopyTo(_output.AsSpan(tail + delta));
            CopiedBytes += count;
            _length += delta;
            for (int i = field + 1; i < _starts.Length; i++)
                _starts[i] += delta;
        }
        value.CopyTo(_output.AsSpan(start));
        CopiedBytes += value.Length;
        _lengths[field] = value.Length;
    }

    private int Finish()
    {
        int bodyStart = _starts[_bodyIndex] + _lengths[_bodyIndex] + 1;
        int checksumStart = _starts[_checksumIndex] - 3;
        Span<byte> digits = stackalloc byte[16];
        if (!Utf8Formatter.TryFormat(checksumStart - bodyStart, digits, out int written))
            throw new InvalidOperationException("BodyLength formatting failed.");
        Replace(_bodyIndex, digits[..written]);
        checksumStart = _starts[_checksumIndex] - 3;
        uint sum = 0;
        foreach (byte value in _output.AsSpan(0, checksumStart))
            sum += value;
        sum &= 255;
        digits[0] = (byte)('0' + sum / 100);
        digits[1] = (byte)('0' + sum / 10 % 10);
        digits[2] = (byte)('0' + sum % 10);
        Replace(_checksumIndex, digits[..3]);
        return _length;
    }

    private byte[] Oracle()
    {
        string[] fields = Encoding.ASCII.GetString(_original).Split('\x01', StringSplitOptions.RemoveEmptyEntries);
        foreach (var edit in _edits)
        {
            int equals = fields[edit.Field].IndexOf('=');
            fields[edit.Field] = fields[edit.Field][..(equals + 1)] + Encoding.ASCII.GetString(edit.Value);
        }
        string body = string.Join('\x01', fields[2..^1]) + '\x01';
        string prefix = fields[0] + "\x01" + "9=" + Encoding.ASCII.GetByteCount(body) + "\x01" + body;
        int checksum = Encoding.ASCII.GetBytes(prefix).Sum(value => value) & 255;
        return Encoding.ASCII.GetBytes(prefix + "10=" + checksum.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "\x01");
    }

    private readonly record struct Field(int Tag, int Start, int ValueStart, int Length, int End);
    private readonly record struct Edit(int Field, byte[] Value);
}
