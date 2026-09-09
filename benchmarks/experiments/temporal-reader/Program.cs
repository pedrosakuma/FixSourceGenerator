using System.Globalization;
using System.Reflection;
using System.Text;
using FixSourceGenerator.Benchmarks;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2;
using FixSourceGenerator.Benchmarks.Generated.Fix.V50SP2.Runtime;

string[] timestamps = ["yyyyMMdd-HH:mm:ss.fff", "yyyyMMdd-HH:mm:ss"];
string[] times = ["HH:mm:ss.fff", "HH:mm:ss"];
int cases = 0;

foreach (string input in new[] { "", "00000000", "20240230", "19000229", "20000229",
    "20240101-24:00:00", "20241231-23:59:60", "24:00:00", "23:60:00", "23:59:60",
    "12:34:56.1", "12:34:56.1234567", "20240101-12:34:56.1234567", " 20240101", "20240101 " })
    Check(Encoding.ASCII.GetBytes(input));

foreach (int year in new[] { 1, 4, 100, 400, 1900, 2000, 2024, 2100, 9999 })
    for (int month = 0; month <= 13; month++)
        for (int day = 0; day <= 32; day++)
        {
            string date = $"{year:D4}{month:D2}{day:D2}";
            Check(Encoding.ASCII.GetBytes(date));
            Check(Encoding.ASCII.GetBytes(date + "-23:59:59.999"));
        }

var random = new Random(42);
for (int i = 0; i < 10000; i++)
{
    int year = random.Next(1, 10000), month = random.Next(1, 13);
    var value = new DateTime(year, month, random.Next(1, DateTime.DaysInMonth(year, month) + 1),
        random.Next(24), random.Next(60), random.Next(60), random.Next(1000), DateTimeKind.Utc);
    foreach (string format in new[] { "yyyyMMdd", "yyyyMMdd-HH:mm:ss.fff", "yyyyMMdd-HH:mm:ss", "HH:mm:ss.fff", "HH:mm:ss" })
        Check(Encoding.ASCII.GetBytes(value.ToString(format, CultureInfo.InvariantCulture)));
}

foreach (string seed in new[] { "20240229", "20240229-23:59:59.999", "20240229-00:00:00",
    "23:59:59.999", "00:00:00" })
{
    byte[] input = Encoding.ASCII.GetBytes(seed);
    for (int position = 0; position < input.Length; position++)
    {
        byte original = input[position];
        for (int value = 0; value < 256; value++)
        {
            input[position] = (byte)value;
            Check(input);
        }
        input[position] = original;
    }
    for (int length = 0; length < input.Length; length++)
        Check(input.AsSpan(0, length));
}

DecodeLoad.ValidateMatrix();
foreach (int entries in new[] { 1, 10, 50 })
    foreach (var order in Enum.GetValues<EntryFieldOrder>())
        foreach (string message in new[] { "X", "W" })
            new MarketDataProcessBenchmarks { Entries = entries, Order = order, Message = message }.Setup();

foreach (Type type in new[] { typeof(MDIncGrpReader.NoMDEntriesGroupReader.NoMDEntriesEntryReader),
    typeof(MDFullGrpReader.NoMDEntriesGroupReader.NoMDEntriesEntryReader), typeof(InstrumentReader) })
{
    var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
    int starts = fields.Count(field => field.Name.EndsWith("Start", StringComparison.Ordinal));
    Console.WriteLine($"{type.FullName}: {starts} located fields; {fields.Length} instance backing fields.");
}
Console.WriteLine($"PASS: {cases} inputs compared against the original framework parser contract, plus X/W decode and process matrices.");

void Check(ReadOnlySpan<byte> input)
{
    string text = Encoding.Latin1.GetString(input);
    bool expectedDate = DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date);
    bool expectedTime = TimeOnly.TryParseExact(text, times, CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var time);
    bool expectedTimestamp = DateTime.TryParseExact(text, timestamps, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp);
    bool actualDate = FixSpanReader.TryParseDateOnly(input, out var parsedDate);
    bool actualTime = FixSpanReader.TryParseTimeOnly(input, out var parsedTime);
    bool actualTimestamp = FixSpanReader.TryParseDateTime(input, out var parsedTimestamp);
    if (actualDate != expectedDate || parsedDate != date || actualTime != expectedTime || parsedTime != time
        || actualTimestamp != expectedTimestamp || parsedTimestamp != timestamp || parsedTimestamp.Kind != timestamp.Kind)
        throw new InvalidOperationException($"Temporal parser mismatch: {Convert.ToHexString(input)}.");
    cases++;
}
