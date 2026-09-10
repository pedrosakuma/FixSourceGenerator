using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EagerProjection.Experiment;

// Deliberately separate from the production recursive dictionary model and public FixView API.
[Generator]
public sealed class EagerProjectionGenerator : ISourceGenerator
{
    private static readonly DiagnosticDescriptor Invalid = new("EAGER001", "Unsupported eager projection",
        "{0}", "Experiment", DiagnosticSeverity.Error, true);
    private const string AttributeName = "EagerProjection.Experiment.ExperimentalEagerAttribute";
    private const string Attributes = """
        namespace EagerProjection.Experiment
        {
            [System.AttributeUsage(System.AttributeTargets.Struct)]
            internal sealed class ExperimentalEagerAttribute : System.Attribute
            {
                public ExperimentalEagerAttribute(string dictionary, string message, string group, string runtimeNamespace) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Property)]
            internal sealed class ExperimentalFieldAttribute : System.Attribute
            {
                public ExperimentalFieldAttribute(string name) { }
            }
            internal static class ExperimentalWire
            {
                internal static bool Read(System.ReadOnlySpan<byte> source, int position,
                    out int tag, out int start, out int length, out int next)
                {
                    tag = start = length = 0; next = position;
                    int digits = 0;
                    while (position < source.Length && source[position] != (byte)'=')
                    {
                        uint digit = (uint)(source[position++] - (byte)'0');
                        if (digit > 9 || tag > (int.MaxValue - digit) / 10) return false;
                        tag = tag * 10 + (int)digit; digits++;
                    }
                    if (digits == 0 || tag == 0 || position >= source.Length) return false;
                    start = ++position;
                    while (position < source.Length && source[position] != 1) position++;
                    if (position == source.Length) return false;
                    length = position - start; next = position + 1; return true;
                }
            }
        }
        """;

    public void Initialize(GeneratorInitializationContext context) =>
        context.RegisterForPostInitialization(c => c.AddSource("ExperimentalEager.Attributes.g.cs", Attributes));

    public void Execute(GeneratorExecutionContext context)
    {
        var visited = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var tree in context.Compilation.SyntaxTrees)
        {
            var semantic = context.Compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (!(semantic.GetDeclaredSymbol(declaration) is INamedTypeSymbol type) || !visited.Add(type)) continue;
                var attribute = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == AttributeName);
                if (attribute == null) continue;
                try
                {
                    string Argument(int index) => attribute.ConstructorArguments[index].Value as string
                        ?? throw new Unsupported("Attribute arguments must be non-null strings");
                    Require(attribute.ConstructorArguments.Length == 4, "Expected four attribute arguments");
                    Require(declaration is StructDeclarationSyntax && type.IsRefLikeType && type.IsReadOnly &&
                        type.Arity == 0 && type.ContainingType == null &&
                        !type.ContainingNamespace.IsGlobalNamespace &&
                        declaration.Modifiers.Any(SyntaxKind.PartialKeyword),
                        "Use a namespace-level, non-generic readonly ref partial struct");
                    string runtime = Argument(3);
                    Require(runtime.Split('.').All(Identifier), "Invalid runtime namespace");
                    var dictionaries = context.AdditionalFiles.Where(f => Path.GetFileName(f.Path) == Argument(0)).ToArray();
                    Require(dictionaries.Length == 1, "Dictionary filename must identify exactly one AdditionalFile");
                    var schema = ReadSchema(dictionaries[0].GetText(context.CancellationToken)?.ToString() ?? "",
                        Argument(1), Argument(2));
                    var properties = type.GetMembers().OfType<IPropertySymbol>().ToArray();
                    Require(properties.Length > 0 && type.GetMembers().All(m => m.IsImplicitlyDeclared ||
                        m is IPropertySymbol || m is IMethodSymbol method && method.MethodKind == MethodKind.PropertyGet),
                        "Only projection property declarations are supported");
                    var selected = new List<Selection>();
                    foreach (var property in properties)
                    {
                        var syntax = property.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().Single();
                        Require(property.IsPartialDefinition && !property.IsStatic && property.DeclaredAccessibility == Accessibility.Public &&
                            syntax.AccessorList?.Accessors.Count == 1 && syntax.AccessorList.Accessors[0].IsKind(SyntaxKind.GetAccessorDeclaration) &&
                            syntax.AccessorList.Accessors[0].Body == null && syntax.AccessorList.Accessors[0].ExpressionBody == null,
                            "Properties must be public partial getter-only definitions");
                        string fieldName = property.GetAttributes().FirstOrDefault(a =>
                            a.AttributeClass?.ToDisplayString() == "EagerProjection.Experiment.ExperimentalFieldAttribute")
                            ?.ConstructorArguments[0].Value as string ?? property.Name;
                        var field = schema.Fields.SingleOrDefault(f => f.Name == fieldName);
                        Require(field != null, "Selected field is not a direct member: " + fieldName);
                        string expected = field!.Text ? "System.ReadOnlySpan<byte>" : "decimal?";
                        Require(property.Type.ToDisplayString() == expected, "Expected " + expected + " for " + fieldName);
                        selected.Add(new Selection(property.Name, field));
                    }
                    var names = new[] { "Enumerate", "Enumerator", type.Name }.Concat(selected.SelectMany(s =>
                        new[] { s.Name, "Has" + s.Name })).ToArray();
                    Require(names.Distinct().Count() == names.Length && names.All(Identifier),
                        "Projection members collide with generated members or use unsupported identifiers");
                    context.AddSource(type.ToDisplayString() + ".Eager.g.cs", Emit(type, schema, selected, runtime));
                }
                catch (Exception exception) when (exception is Unsupported || exception is System.Xml.XmlException ||
                    exception is InvalidOperationException || exception is ArgumentException || exception is FormatException ||
                    exception is OverflowException)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Invalid, declaration.Identifier.GetLocation(), exception.Message));
                }
            }
        }
    }

    private static bool Identifier(string name) => SyntaxFacts.IsValidIdentifier(name) &&
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None && !name.StartsWith("_", StringComparison.Ordinal);
    private static void Require(bool condition, string reason) { if (!condition) throw new Unsupported(reason); }
    private sealed class Unsupported(string message) : Exception(message) { }
    private sealed class Field(string name, int tag, bool text)
    {
        internal readonly string Name = name;
        internal readonly int Tag = tag;
        internal readonly bool Text = text;
    }
    private sealed class Selection(string name, Field field)
    {
        internal readonly string Name = name;
        internal readonly Field Field = field;
        internal string Type => Field.Text ? "global::System.ReadOnlySpan<byte>" : "decimal?";
    }
    private sealed class Schema(int count, int delimiter, string messageType, List<Field> fields, int[] header)
    {
        internal readonly int Count = count, Delimiter = delimiter;
        internal readonly string MessageType = messageType;
        internal readonly List<Field> Fields = fields;
        internal readonly int[] Header = header;
    }

    private static Schema ReadSchema(string xml, string messageName, string groupName)
    {
        var root = XDocument.Parse(xml).Root;
        Require(root?.Name == "fix", "Expected an unqualified <fix> dictionary");
        var definitions = root!.Element("fields")?.Elements("field").ToArray() ?? Array.Empty<XElement>();
        Require(definitions.Length > 0, "Missing field definitions");
        string Name(XElement element) => (string?)element.Attribute("name") ?? "";
        int Tag(XElement element) => int.Parse((string?)element.Attribute("number") ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
        Require(definitions.All(f => Identifier(Name(f)) && Tag(f) > 0) &&
            definitions.Select(Name).Distinct().Count() == definitions.Length &&
            definitions.Select(Tag).Distinct().Count() == definitions.Length, "Invalid or duplicate field names/tags");
        XElement Resolve(XElement reference) => definitions.SingleOrDefault(f => Name(f) == Name(reference))
            ?? throw new Unsupported("Unresolved field " + Name(reference));
        var message = root.Element("messages")?.Elements("message").SingleOrDefault(m => Name(m) == messageName);
        Require(message != null, "Message not found");
        var members = message!.Elements().ToArray();
        Require(members.Length == 1 && members[0].Name == "group" && Name(members[0]) == groupName,
            "Only messages containing exactly one direct group are supported (no root fields/components/siblings)");
        var group = members[0];
        Require((string?)Resolve(group).Attribute("type") == "NUMINGROUP" &&
            (string?)group.Attribute("required") == "Y", "Group must be required NUMINGROUP");
        var entries = group.Elements().ToArray();
        Require(entries.Length > 0 && entries.All(e => e.Name == "field"),
            "Nested groups/components are unsupported; scope cannot be flattened");
        Require(entries.Select(Name).Distinct().Count() == entries.Length, "Duplicate group members");
        Require((string?)entries[0].Attribute("required") == "Y" &&
            (string?)Resolve(entries[0]).Attribute("type") == "STRING" &&
            entries.Skip(1).All(e => (string?)e.Attribute("required") == "N"),
            "Require a required STRING delimiter followed only by optional fields");
        var fields = new List<Field>();
        foreach (var entry in entries)
        {
            var definition = Resolve(entry);
            string kind = (string?)definition.Attribute("type") ?? "";
            Require(kind == "STRING" || kind == "PRICE" || kind == "QTY" || kind == "AMT" || kind == "FLOAT",
                "Unsupported entry field type " + kind);
            fields.Add(new Field(Name(entry), Tag(definition), kind == "STRING"));
        }
        var header = root.Element("header")?.Elements().ToArray() ?? Array.Empty<XElement>();
        var trailer = root.Element("trailer")?.Elements().ToArray() ?? Array.Empty<XElement>();
        Require(header.All(e => e.Name == "field") && trailer.Length == 1 && trailer[0].Name == "field" &&
            Tag(Resolve(trailer[0])) == 10 && header.Any(e => Tag(Resolve(e)) == 35),
            "Only scalar headers with MsgType(35) and CheckSum(10)-only trailers are supported");
        Require(header.All(e => (string?)Resolve(e).Attribute("type") != "DATA" &&
            (string?)Resolve(e).Attribute("type") != "XMLDATA"), "Length-delimited header data is unsupported");
        var scopeTags = header.Concat(trailer).Select(e => Tag(Resolve(e))).ToArray();
        int count = Tag(Resolve(group));
        Require(scopeTags.Distinct().Count() == scopeTags.Length && !scopeTags.Contains(count) &&
            fields.All(f => !scopeTags.Contains(f.Tag) && f.Tag != count), "Overlapping scoped tags are unsupported");
        string msgtype = (string?)message.Attribute("msgtype") ?? "";
        Require(msgtype.Length > 0 && msgtype.All(c => c >= 33 && c <= 126 && c != '=' && c != '"' && c != '\\'),
            "Unsupported message type literal");
        return new Schema(count, fields[0].Tag, msgtype, fields, header.Select(e => Tag(Resolve(e))).ToArray());
    }

    private static string Emit(INamedTypeSymbol type, Schema schema, List<Selection> selected, string runtime)
    {
        var b = new StringBuilder();
        string r = "global::" + runtime + ".FixSpanReader";
        const string read = "global::EagerProjection.Experiment.ExperimentalWire.Read";
        void Line(string text) => b.AppendLine(text);
        Line("// <auto-generated/>");
        Line("#nullable enable");
        Line("namespace " + type.ContainingNamespace.ToDisplayString() + ";");
        Line((type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal") +
            " readonly ref partial struct " + type.Name + " {");
        for (int i = 0; i < selected.Count; i++)
        {
            var s = selected[i];
            Line($"private readonly {s.Type} _v{i}; private readonly bool _has{i};");
            Line($"public partial {s.Type} {s.Name} {{ get => _v{i}; }}");
            Line($"public bool Has{s.Name} => _has{i};");
        }
        Line($"private {type.Name}(" + string.Join(", ", selected.Select((s, i) => $"{s.Type} v{i}, bool has{i}")) + ") {");
        for (int i = 0; i < selected.Count; i++) Line($"_v{i} = v{i}; _has{i} = has{i};");
        Line("}");
        Line($"public static Enumerator Enumerate(global::System.ReadOnlySpan<byte> message) => new(message);");
        Line("public ref struct Enumerator {");
        Line("private readonly global::System.ReadOnlySpan<byte> _source; private int _position, _remaining; private bool _ended;");
        Line($"public {type.Name} Current {{ get; private set; }}");
        Line("public Enumerator GetEnumerator() => this;");
        Line("internal Enumerator(global::System.ReadOnlySpan<byte> source) {");
        Line("_source = source; _position = _remaining = 0; _ended = false; Current = default; bool messageType = false;");
        Line($"while ({read}(source, _position, out int tag, out int start, out int length, out int next)) {{");
        Line("_position = next;");
        Line($"if (tag == 35) {{ if (messageType || !global::System.MemoryExtensions.SequenceEqual(source.Slice(start, length), \"{schema.MessageType}\"u8)) throw new global::System.FormatException(\"Wrong/duplicate MsgType\"); messageType = true; }}");
        Line($"if (tag == {schema.Count}) {{ if (!messageType || !global::System.Buffers.Text.Utf8Parser.TryParse(source.Slice(start, length), out _remaining, out int consumed) || consumed != length || _remaining < 0) throw new global::System.FormatException(\"Invalid group count\"); return; }}");
        Line($"if (!({string.Join(" || ", schema.Header.Select(tag => "tag == " + tag))})) break;");
        Line("} throw new global::System.FormatException(\"Missing group count\"); }");
        Line("public bool MoveNext() { Current = default; if (_ended) return false;");
        Line("try { return Advance(); } catch { _ended = true; throw; } }");
        Line("private bool Advance() {");
        Line("if (_remaining == 0) {");
        Line($"if (!{read}(_source, _position, out int endTag, out _, out _, out int end) || endTag != 10 || end != _source.Length) throw new global::System.FormatException(\"Invalid group/trailer boundary\");");
        Line("_ended = true; return false; }");
        Line($"if (!{read}(_source, _position, out int tag, out int start, out int length, out int next) || tag != {schema.Delimiter}) throw new global::System.FormatException(\"Missing entry delimiter\");");
        for (int i = 0; i < selected.Count; i++) Line($"{selected[i].Type} v{i} = default; bool has{i} = false;");
        Line("while (true) {");
        Line("switch (tag) {");
        foreach (var field in schema.Fields)
        {
            Line($"case {field.Tag}:");
            foreach (var pair in selected.Select((s, i) => new { s, i }).Where(p => p.s.Field == field))
            {
                int i = pair.i;
                Line($"if (!has{i}) {{ has{i} = true;");
                Line(field.Text ? $"v{i} = _source.Slice(start, length);" :
                    $"v{i} = {r}.TryParseDecimal(_source.Slice(start, length), out decimal parsed{i}) ? parsed{i} : (decimal?)null;");
                Line("}");
            }
            Line("break;");
        }
        Line("default: throw new global::System.FormatException(\"Unknown/out-of-scope entry tag\"); }");
        Line("_position = next;");
        Line($"if (!{read}(_source, _position, out tag, out start, out length, out next)) throw new global::System.FormatException(\"Malformed or truncated entry\");");
        Line($"if (tag == {schema.Delimiter} || tag == 10) break;");
        Line("}");
        Line($"if ((_remaining > 1 && tag != {schema.Delimiter}) || (_remaining == 1 && (tag != 10 || next != _source.Length))) throw new global::System.FormatException(\"Declared count/boundary mismatch\");");
        Line("_remaining--;");
        Line($"Current = new {type.Name}(" + string.Join(", ", selected.Select((s, i) => $"v{i}, has{i}")) + "); return true;");
        Line("} } }");
        return b.ToString();
    }
}
