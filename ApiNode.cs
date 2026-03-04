using System.Text.Json.Serialization;

namespace UnsafeApiBrowser;

[JsonConverter(typeof(JsonStringEnumConverter<ApiNodeKind>))]
public enum ApiNodeKind
{
    Namespace,
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Record,
    RecordStruct,
    Method,
    Property,
    Field,
    Event,
    Constructor,
    Operator,
    Indexer,
    EnumMember,
}

public sealed class ApiNode
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sig { get; set; }
    public required ApiNodeKind Kind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsMarked { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsUnsafe { get; set; }
    [JsonIgnore]
    public string? SourceFile { get; set; }
    [JsonIgnore]
    public int SourceLine { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Src { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Line { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ApiNode>? Children { get; set; }

    public void SortChildren()
    {
        if (Children is null) return;

        // Namespaces first, then types, then members — alphabetical within each group
        Children.Sort((a, b) =>
        {
            int ka = KindOrder(a.Kind), kb = KindOrder(b.Kind);
            if (ka != kb) return ka.CompareTo(kb);
            return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
        });

        foreach (var child in Children)
            child.SortChildren();
    }

    static int KindOrder(ApiNodeKind kind) => kind switch
    {
        ApiNodeKind.Namespace => 0,
        ApiNodeKind.Class or ApiNodeKind.Struct or ApiNodeKind.Interface
            or ApiNodeKind.Enum or ApiNodeKind.Delegate
            or ApiNodeKind.Record or ApiNodeKind.RecordStruct => 1,
        _ => 2,
    };
}
