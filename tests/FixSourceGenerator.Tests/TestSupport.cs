using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FixSourceGenerator;
using FixSourceGenerator.Generators;
using FixSourceGenerator.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FixSourceGenerator.Tests;

/// <summary>
/// Shared helpers for the codegen tests: sample dictionaries, running <see cref="FixCodeGenerator"/>,
/// and compiling (and optionally loading) the generated C# against the real net9.0 reference set.
/// </summary>
internal static class TestSupport
{
    public const string Namespace = "Acme.Fix.V44";
    public const byte Soh = 0x01;

    private static readonly IReadOnlyDictionary<string, FixFieldDef> EmptyByName =
        new Dictionary<string, FixFieldDef>();
    private static readonly IReadOnlyDictionary<int, FixFieldDef> EmptyByNumber =
        new Dictionary<int, FixFieldDef>();
    private static readonly IReadOnlyDictionary<string, FixComponentDef> EmptyComponents =
        new Dictionary<string, FixComponentDef>();

    private static readonly MetadataReference[] References = BuildReferences();

    private static MetadataReference[] BuildReferences()
    {
        var tpa = (string)System.AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return tpa
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToArray();
    }

    /// <summary>
    /// A NewOrderSingle-like dictionary exercising: required STRING (ClOrdID), optional PRICE
    /// (decimal?), enumerated CHAR (Side), a reusable component (Instrument), and a repeating group
    /// (NoAllocs) that itself contains a nested group (NoNested) — two levels of nesting.
    /// </summary>
    public static FixDictionary BuildSampleDictionary()
    {
        var clOrdId = new FixFieldDef(11, "ClOrdID", "STRING", new List<FixValueDef>());
        var symbol = new FixFieldDef(55, "Symbol", "STRING", new List<FixValueDef>());
        var securityId = new FixFieldDef(48, "SecurityID", "STRING", new List<FixValueDef>());
        var side = new FixFieldDef(54, "Side", "CHAR", new List<FixValueDef>
        {
            new FixValueDef("1", "BUY"),
            new FixValueDef("2", "SELL"),
        });
        var price = new FixFieldDef(44, "Price", "PRICE", new List<FixValueDef>());
        var orderQty = new FixFieldDef(38, "OrderQty", "QTY", new List<FixValueDef>());
        var transactTime = new FixFieldDef(60, "TransactTime", "UTCTIMESTAMP", new List<FixValueDef>());

        var noAllocs = new FixFieldDef(78, "NoAllocs", "NUMINGROUP", new List<FixValueDef>());
        var allocAccount = new FixFieldDef(79, "AllocAccount", "STRING", new List<FixValueDef>());
        var allocQty = new FixFieldDef(80, "AllocQty", "QTY", new List<FixValueDef>());

        var noNested = new FixFieldDef(756, "NoNested", "NUMINGROUP", new List<FixValueDef>());
        var nestedPartyId = new FixFieldDef(757, "NestedPartyID", "STRING", new List<FixValueDef>());

        var instrument = new FixComponentDef("Instrument", new List<FixEntry>
        {
            new FixFieldRef(symbol, required: true),
            new FixFieldRef(securityId, required: false),
        });

        var nestedGroup = new FixGroupRef(
            "NoNested",
            noNested,
            new List<FixEntry> { new FixFieldRef(nestedPartyId, required: true) },
            required: false);

        var allocsGroup = new FixGroupRef(
            "NoAllocs",
            noAllocs,
            new List<FixEntry>
            {
                new FixFieldRef(allocAccount, required: true),
                new FixFieldRef(allocQty, required: true),
                nestedGroup,
            },
            required: false);

        var message = new FixMessageDef("NewOrderSingle", "D", "app", new List<FixEntry>
        {
            new FixFieldRef(clOrdId, required: true),
            new FixComponentRef(instrument, required: true),
            new FixFieldRef(side, required: true),
            new FixFieldRef(orderQty, required: true),
            new FixFieldRef(price, required: false),
            new FixFieldRef(transactTime, required: false),
            allocsGroup,
        });

        return new FixDictionary(
            "FIX", 4, 4, 0,
            header: new List<FixEntry>(),
            trailer: new List<FixEntry>(),
            messages: new List<FixMessageDef> { message },
            componentsByName: EmptyComponents,
            fieldsByName: EmptyByName,
            fieldsByNumber: EmptyByNumber);
    }

    /// <summary>A dictionary whose enum field exercises all-caps, leading-digit and underscore normalization.</summary>
    public static FixDictionary BuildEnumEdgeCaseDictionary()
    {
        var ordStatus = new FixFieldDef(39, "OrdStatus", "CHAR", new List<FixValueDef>
        {
            new FixValueDef("0", "NEW"),
            new FixValueDef("1", "PARTIALLY_FILLED"),
            new FixValueDef("2", "9WEST"),
        });

        var message = new FixMessageDef("ExecutionReport", "8", "app", new List<FixEntry>
        {
            new FixFieldRef(ordStatus, required: true),
        });

        return new FixDictionary(
            "FIX", 4, 4, 0,
            header: new List<FixEntry>(),
            trailer: new List<FixEntry>(),
            messages: new List<FixMessageDef> { message },
            componentsByName: EmptyComponents,
            fieldsByName: EmptyByName,
            fieldsByNumber: EmptyByNumber);
    }

    /// <summary>A single-message dictionary whose sole body field has an unrecognized FIX type.</summary>
    public static FixDictionary BuildUnknownTypeDictionary()
    {
        var weird = new FixFieldDef(5000, "VendorThing", "FOOBAR", new List<FixValueDef>());
        var message = new FixMessageDef("VendorMessage", "U1", "app", new List<FixEntry>
        {
            new FixFieldRef(weird, required: true),
        });

        return new FixDictionary(
            "FIX", 4, 4, 0,
            header: new List<FixEntry>(),
            trailer: new List<FixEntry>(),
            messages: new List<FixMessageDef> { message },
            componentsByName: EmptyComponents,
            fieldsByName: EmptyByName,
            fieldsByNumber: EmptyByNumber);
    }

    public static FixDictionary BuildDiffDictionary(
        IEnumerable<FixMessageDef>? messages = null,
        IReadOnlyDictionary<string, FixComponentDef>? components = null,
        IReadOnlyDictionary<string, FixFieldDef>? fields = null)
    {
        var resolvedFields = fields ?? BuildDiffFields();
        var resolvedComponents = components ?? BuildDiffComponents(resolvedFields);
        var resolvedMessages = messages?.ToList() ?? new List<FixMessageDef> { BuildExecutionReport(fieldMap: resolvedFields, componentMap: resolvedComponents, includeInstrument: resolvedComponents.ContainsKey("Instrument")) };

        return new FixDictionary(
            "FIX", 4, 4, 0,
            header: new List<FixEntry>(),
            trailer: new List<FixEntry>(),
            messages: resolvedMessages,
            componentsByName: resolvedComponents,
            fieldsByName: resolvedFields,
            fieldsByNumber: resolvedFields.Values.ToDictionary(field => field.Number));
    }

    public static IReadOnlyDictionary<string, FixFieldDef> BuildDiffFields(
        string symbolType = "STRING",
        IReadOnlyList<FixValueDef>? sideValues = null)
    {
        var values = sideValues ?? new List<FixValueDef>
        {
            new FixValueDef("1", "BUY"),
            new FixValueDef("2", "SELL"),
        };

        var fields = new[]
        {
            new FixFieldDef(35, "MsgType", "STRING", new List<FixValueDef>()),
            new FixFieldDef(37, "OrderID", "STRING", new List<FixValueDef>()),
            new FixFieldDef(54, "Side", "CHAR", values),
            new FixFieldDef(55, "Symbol", symbolType, new List<FixValueDef>()),
            new FixFieldDef(58, "Text", "STRING", new List<FixValueDef>()),
            new FixFieldDef(448, "PartyID", "STRING", new List<FixValueDef>()),
            new FixFieldDef(452, "PartyRole", "INT", new List<FixValueDef>()),
            new FixFieldDef(453, "NoPartyIDs", "NUMINGROUP", new List<FixValueDef>()),
        };

        return fields.ToDictionary(field => field.Name);
    }

    public static IReadOnlyDictionary<string, FixComponentDef> BuildDiffComponents(IReadOnlyDictionary<string, FixFieldDef>? fieldMap = null)
    {
        var fields = fieldMap ?? BuildDiffFields();
        var instrument = new FixComponentDef("Instrument", new List<FixEntry>
        {
            new FixFieldRef(fields["Symbol"], required: true),
        });

        return new Dictionary<string, FixComponentDef>
        {
            [instrument.Name] = instrument,
        };
    }

    public static FixMessageDef BuildExecutionReport(
        string msgType = "8",
        bool includeSymbol = true,
        bool includeText = true,
        bool includeOptionalOrderId = true,
        bool includePartyRole = true,
        bool includeInstrument = true,
        IReadOnlyDictionary<string, FixComponentDef>? componentMap = null,
        IReadOnlyDictionary<string, FixFieldDef>? fieldMap = null)
    {
        var fields = fieldMap ?? BuildDiffFields();
        var components = componentMap ?? BuildDiffComponents(fields);
        var entries = new List<FixEntry>();

        if (includeSymbol)
        {
            entries.Add(new FixFieldRef(fields["Symbol"], required: true));
        }

        entries.Add(new FixFieldRef(fields["Side"], required: true));

        if (includeOptionalOrderId)
        {
            entries.Add(new FixFieldRef(fields["OrderID"], required: false));
        }

        if (includeText)
        {
            entries.Add(new FixFieldRef(fields["Text"], required: true));
        }

        if (includeInstrument)
        {
            entries.Add(new FixComponentRef(components["Instrument"], required: false));
        }
        entries.Add(new FixGroupRef(
            "NoPartyIDs",
            fields["NoPartyIDs"],
            includePartyRole
                ? new List<FixEntry>
                {
                    new FixFieldRef(fields["PartyID"], required: true),
                    new FixFieldRef(fields["PartyRole"], required: false),
                }
                : new List<FixEntry>
                {
                    new FixFieldRef(fields["PartyID"], required: true),
                },
            required: false));

        return new FixMessageDef("ExecutionReport", msgType, "app", entries);
    }

    public static List<(string hintName, string content)> Generate(FixDictionary dictionary, out List<Diagnostic> diagnostics)
    {
        var diags = new List<Diagnostic>();
        var context = new GenerationContext(diags.Add);
        var result = new FixCodeGenerator().Generate(Namespace, dictionary, context).ToList();
        diagnostics = diags;
        return result;
    }

    public static CSharpCompilation Compile(IEnumerable<string> sources) => Compile(sources, "GeneratedFixAssembly");

    public static CSharpCompilation Compile(IEnumerable<string> sources, string assemblyName)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s, parseOptions));
        return CSharpCompilation.Create(
            assemblyName,
            trees,
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));
    }

    public static Assembly EmitAndLoad(IEnumerable<string> sources) => EmitAndLoad(sources, "GeneratedFixAssembly");

    public static Assembly EmitAndLoad(IEnumerable<string> sources, string assemblyName)
    {
        var compilation = Compile(sources, assemblyName);
        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        Assert.True(emit.Success, "Emit failed:\n" + string.Join("\n", emit.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())));
        ms.Seek(0, SeekOrigin.Begin);
        return Assembly.Load(ms.ToArray());
    }

    /// <summary>Builds a raw FIX byte buffer from <c>tag=value</c> pieces joined by SOH.</summary>
    public static byte[] Fix(params string[] fields)
    {
        using var ms = new MemoryStream();
        foreach (var f in fields)
        {
            foreach (char c in f)
            {
                ms.WriteByte((byte)c);
            }

            ms.WriteByte(Soh);
        }

        return ms.ToArray();
    }
}
