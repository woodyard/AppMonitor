using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Arkimentum.AppMonitor.Configuration;

/// <summary>
/// A distributable settings profile: the raw values of one registry layer (preferences or policy), without defaults.
/// Values are stored in natural JSON types (bool, number, string, string[]) and validated against <see cref="SettingsSchema"/>.
/// </summary>
public sealed class SettingsDocument
{
    public const string CurrentSchema = "arkimentum-appmonitor-settings/1";

    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = CurrentSchema;
    public string? Description { get; set; }
    public DateTimeOffset? ExportedUtc { get; set; }
    public string? ExportedBy { get; set; }
    public string? ExportedFrom { get; set; }
    public string? ProductVersion { get; set; }

    /// <summary>Global values by registry value name.</summary>
    public SortedDictionary<string, SettingValue> Global { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-application values by AppId, then by registry value name.</summary>
    public SortedDictionary<string, SortedDictionary<string, SettingValue>> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SortedDictionary<string, SettingValue> GetOrAddApp(string appId)
    {
        if (!Apps.TryGetValue(appId, out var values)) Apps[appId] = values = new SortedDictionary<string, SettingValue>(StringComparer.OrdinalIgnoreCase);
        return values;
    }

    [JsonIgnore]
    public bool IsEmpty => Global.Count == 0 && Apps.Count == 0;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new SettingValueJsonConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static SettingsDocument FromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<SettingsDocument>(json, Json) ?? throw new InvalidDataException("Empty settings document.");
        if (!string.Equals(doc.Schema, CurrentSchema, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported settings document schema '{doc.Schema}' (expected '{CurrentSchema}').");
        doc.Global = new SortedDictionary<string, SettingValue>(doc.Global, StringComparer.OrdinalIgnoreCase);
        doc.Apps = new SortedDictionary<string, SortedDictionary<string, SettingValue>>(
            doc.Apps.ToDictionary(k => k.Key, v => new SortedDictionary<string, SettingValue>(v.Value, StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        return doc;
    }

    /// <summary>Returns validation problems (unknown names, out-of-range numbers, invalid choices). Empty = valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var (name, value) in Global)
        {
            var def = SettingsSchema.FindGlobal(name);
            if (def is null) { problems.Add($"Unknown global setting '{name}'."); continue; }
            problems.AddRange(ValidateValue(def, value, name));
        }
        foreach (var (appId, values) in Apps)
        {
            if (string.IsNullOrWhiteSpace(appId) || appId.IndexOfAny(['\\', '/']) >= 0) problems.Add($"Invalid AppId '{appId}'.");
            foreach (var (name, value) in values)
            {
                var def = SettingsSchema.FindApp(name);
                if (def is null) { problems.Add($"Unknown setting '{name}' for app '{appId}'."); continue; }
                problems.AddRange(ValidateValue(def, value, $"{appId}\\{name}"));
            }
        }
        return problems;
    }

    private static IEnumerable<string> ValidateValue(SettingDefinition def, SettingValue value, string label)
    {
        switch (def.Kind)
        {
            case SettingKind.Int:
                if (value.AsInt() is not { } i) yield return $"'{label}' must be a whole number.";
                else if (i < def.Min || i > def.Max) yield return $"'{label}' must be between {def.Min} and {def.Max}.";
                break;
            case SettingKind.Bool:
                if (value.AsBool() is null) yield return $"'{label}' must be true or false.";
                break;
            case SettingKind.Choice:
                if (def.Choices is not null && !def.Choices.Contains(value.AsString() ?? "", StringComparer.OrdinalIgnoreCase))
                    yield return $"'{label}' must be one of: {string.Join(", ", def.Choices)}.";
                break;
            case SettingKind.IntList:
                if (value.AsIntList().Count == 0) yield return $"'{label}' must be a list of positive whole numbers.";
                break;
        }
    }
}

/// <summary>A single setting value with lenient conversions between the JSON, registry and text representations.</summary>
public sealed class SettingValue : IEquatable<SettingValue>
{
    public object Raw { get; }

    public SettingValue(object raw)
    {
        Raw = raw switch
        {
            int i => (long)i,
            uint u => (long)u,
            long or bool or string or string[] => raw,
            IEnumerable<string> e => e.ToArray(),
            null => throw new ArgumentNullException(nameof(raw)),
            _ => raw.ToString() ?? string.Empty,
        };
    }

    public static SettingValue From(bool b) => new(b);
    public static SettingValue From(long n) => new(n);
    public static SettingValue From(string s) => new(s);
    public static SettingValue From(IEnumerable<string> list) => new(list.ToArray());
    public static SettingValue From(IEnumerable<int> list) => new(string.Join(",", list));

    public bool? AsBool() => Raw switch
    {
        bool b => b,
        long n => n != 0,
        string s when bool.TryParse(s, out var b) => b,
        string s when long.TryParse(s, out var n) => n != 0,
        _ => null,
    };

    public long? AsInt() => Raw switch
    {
        long n => n,
        bool b => b ? 1 : 0,
        string s when long.TryParse(s.Trim(), out var n) => n,
        _ => null,
    };

    public string? AsString() => Raw switch
    {
        string s => s,
        string[] a => string.Join(",", a),
        bool b => b ? "1" : "0",
        long n => n.ToString(),
        _ => null,
    };

    public IReadOnlyList<string> AsStringList() => Raw switch
    {
        string[] a => a,
        string s => s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        _ => [],
    };

    public IReadOnlyList<int> AsIntList() => AsStringList()
        .Select(p => int.TryParse(p, out var i) ? i : -1).Where(i => i > 0).Distinct().OrderBy(i => i).ToList();

    /// <summary>Converts to the object that should be written to the registry for <paramref name="def"/>.</summary>
    public object ToRegistryValue(SettingDefinition def) => def.Kind switch
    {
        SettingKind.Bool => AsBool() is true ? 1 : 0,
        SettingKind.Int => (int)Math.Clamp(AsInt() ?? 0, int.MinValue, int.MaxValue),
        SettingKind.StringList => AsStringList().ToArray(),
        SettingKind.IntList => string.Join(",", AsIntList()),
        _ => AsString() ?? string.Empty,
    };

    /// <summary>Creates a value from what the registry returned for <paramref name="def"/>.</summary>
    public static SettingValue? FromRegistryValue(SettingDefinition def, object? raw)
    {
        if (raw is null) return null;
        return def.Kind switch
        {
            SettingKind.Bool => raw switch { int i => From(i != 0), string s => From(s), _ => From(raw.ToString() ?? "") },
            SettingKind.Int => raw switch { int i => From(i), long l => From(l), string s when long.TryParse(s, out var n) => From(n), _ => From(raw.ToString() ?? "") },
            SettingKind.StringList => raw switch { string[] a => From(a), string s => From(s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)), _ => From(raw.ToString() ?? "") },
            _ => raw switch { string[] a => From(string.Join(",", a)), _ => From(raw.ToString() ?? "") },
        };
    }

    public override string ToString() => AsString() ?? string.Empty;

    public bool Equals(SettingValue? other)
    {
        if (other is null) return false;
        if (Raw is string[] a && other.Raw is string[] b) return a.SequenceEqual(b, StringComparer.Ordinal);
        return Equals(Raw, other.Raw);
    }

    public override bool Equals(object? obj) => Equals(obj as SettingValue);
    public override int GetHashCode() => Raw is string[] a ? string.Join("", a).GetHashCode() : Raw.GetHashCode();
}

public sealed class SettingValueJsonConverter : JsonConverter<SettingValue>
{
    public override SettingValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonNode.Parse(ref reader);
        return node switch
        {
            null => null,
            JsonArray arr => new SettingValue(arr.Select(n => n?.ToString() ?? string.Empty).ToArray()),
            JsonValue v when v.TryGetValue<bool>(out var b) => new SettingValue(b),
            JsonValue v when v.TryGetValue<long>(out var n) => new SettingValue(n),
            JsonValue v when v.TryGetValue<double>(out var d) => new SettingValue((long)d),
            JsonValue v when v.TryGetValue<string>(out var s) => new SettingValue(s),
            _ => new SettingValue(node.ToJsonString()),
        };
    }

    public override void Write(Utf8JsonWriter writer, SettingValue value, JsonSerializerOptions options)
    {
        switch (value.Raw)
        {
            case bool b: writer.WriteBooleanValue(b); break;
            case long n: writer.WriteNumberValue(n); break;
            case string[] a:
                writer.WriteStartArray();
                foreach (var s in a) writer.WriteStringValue(s);
                writer.WriteEndArray();
                break;
            default: writer.WriteStringValue(value.AsString()); break;
        }
    }
}
