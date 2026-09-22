using System.Globalization;
using Arkimentum.AppMonitor.Api.Contracts;

namespace Arkimentum.AppMonitor.Web.Editing;

/// <summary>
/// One editable value of the organization configuration, generated from a <see cref="SettingDefinition"/> - never
/// hand-written per setting.
///
/// <para>
/// A row carries two layers: the value the document sets (<see cref="IsOverridden"/>) and the value the agent falls
/// back to when it does not (<see cref="InheritedText"/> - the built-in default for a global setting, the catalog
/// entry or the matching global <c>Default*</c> value for a per-application setting). Turning the override off
/// removes the key from the document entirely, which is what "reset to default" means here.
/// </para>
///
/// <para>
/// Ported from <c>src/Arkimentum.AppMonitor.Admin/ViewModels/SettingRowViewModel.cs</c>, minus the WPF plumbing and
/// minus the policy/organization lock layers: the organization document has nothing above it, so no row is ever
/// read-only here. No Blazor dependency - the pages only render what this class computes.
/// </para>
/// </summary>
public sealed class SettingRow
{
    private bool _isOverridden;
    private bool _boolValue;
    private string _textValue = string.Empty;

    public SettingRow(SettingDefinition definition, SettingValue? value)
    {
        Definition = definition;
        Load(value);
    }

    /// <summary>Raised whenever the stored value changes, so the owning document can re-evaluate dirtiness.</summary>
    public event Action<SettingRow>? ValueChanged;

    public SettingDefinition Definition { get; }

    public string Name => Definition.Name;
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string Category => Definition.Category;
    public SettingKind Kind => Definition.Kind;
    public bool IsAdvanced => Definition.Advanced;
    public IReadOnlyList<string> Choices => Definition.Choices ?? [];

    // ---------------------------------------------------------------- state

    /// <summary>Whether the document carries this value at all. Off = the agent uses the inherited value.</summary>
    public bool IsOverridden
    {
        get => _isOverridden;
        set
        {
            if (_isOverridden == value) return;
            _isOverridden = value;
            // Clearing the override puts the inherited value back on screen, greyed out, as the hint it now is.
            if (!value) ShowInherited();
            Revalidate();
            ValueChanged?.Invoke(this);
        }
    }

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (_boolValue == value) return;
            _boolValue = value;
            Revalidate();
            ValueChanged?.Invoke(this);
        }
    }

    public string TextValue
    {
        get => _textValue;
        set
        {
            value ??= string.Empty;
            if (_textValue == value) return;
            _textValue = value;
            Revalidate();
            ValueChanged?.Invoke(this);
        }
    }

    /// <summary>Set by the owning application editor when the effective Source decides which fields apply.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Whether the row appears on the page, given the page's "Show advanced" toggle.</summary>
    public bool IsShown(bool showAdvanced) => IsVisible && (!IsAdvanced || showAdvanced);

    public string? Error { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>What the badge next to the row says: where the effective value comes from.</summary>
    public string SourceBadge => IsOverridden ? "Set" : "Default";

    // ---------------------------------------------------------------- inherited value

    /// <summary>The value the agent uses when the row is not overridden.</summary>
    public string InheritedText { get; private set; } = string.Empty;

    /// <summary>Where that value comes from: "the built-in default", "the catalog", "the global setting".</summary>
    public string InheritedLabel { get; private set; } = InheritedFromBuiltIn;

    public const string InheritedFromBuiltIn = "the built-in default";
    public const string InheritedFromCatalog = "the catalog";
    public const string InheritedFromGlobal = "the global setting";

    public string HintText => string.IsNullOrEmpty(InheritedText)
        ? $"Not set ({InheritedLabel})."
        : $"Inherits {InheritedText} from {InheritedLabel}.";

    /// <summary>What a non-overridden toggle shows.</summary>
    public bool InheritedBoolValue =>
        InheritedText.Equals("true", StringComparison.OrdinalIgnoreCase) || InheritedText == "1";

    public void SetInherited(string? text, string label)
    {
        InheritedText = text ?? string.Empty;
        InheritedLabel = label;
        if (!IsOverridden) ShowInherited();
    }

    private void ShowInherited()
    {
        _boolValue = InheritedBoolValue;
        _textValue = InheritedText;
    }

    public string RangeHint => Definition.Kind == SettingKind.Int && Definition.Min != int.MinValue
        ? $"{Definition.Min} – {Definition.Max}"
        : string.Empty;

    public bool HasRangeHint => RangeHint.Length > 0;

    // ---------------------------------------------------------------- conversion

    /// <summary>Fills the editor from a stored value (null = not overridden).</summary>
    public void Load(SettingValue? value)
    {
        _isOverridden = value is not null;
        _boolValue = value?.AsBool() ?? AsBool(Definition.Default);
        _textValue = value is null ? DefaultText() : ToText(value);
        Revalidate();
    }

    /// <summary>The value to store, or null when the row is not overridden and the key must be absent.</summary>
    public SettingValue? ToValue() => IsOverridden ? Build() : null;

    private SettingValue Build() => Definition.Kind switch
    {
        SettingKind.Bool => SettingValue.From(BoolValue),
        SettingKind.Int => long.TryParse(TextValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? SettingValue.From(n)
            : SettingValue.From(TextValue.Trim()),
        SettingKind.StringList => SettingValue.From(Lines(TextValue)),
        SettingKind.IntList => SettingValue.From(string.Join(",", Numbers(TextValue))),
        _ => SettingValue.From(TextValue.Trim()),
    };

    private string ToText(SettingValue value) => Definition.Kind switch
    {
        SettingKind.StringList => string.Join("\n", value.AsStringList()),
        SettingKind.IntList => string.Join(",", value.AsIntList()),
        SettingKind.Bool => value.AsBool() is true ? "1" : "0",
        _ => value.AsString() ?? string.Empty,
    };

    /// <summary>The built-in default rendered the way the editor shows values.</summary>
    public string DefaultText() => Definition.Default switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        var v => v.ToString() ?? string.Empty,
    };

    private static bool AsBool(object? value) => value switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => s is "1" or "true" or "True",
        _ => false,
    };

    /// <summary>Formats any stored value for a hint line.</summary>
    public string Format(SettingValue? value) => value is null ? string.Empty : Definition.Kind switch
    {
        SettingKind.Bool => value.AsBool() is true ? "Yes" : "No",
        SettingKind.StringList => string.Join(", ", value.AsStringList()),
        _ => value.AsString() ?? string.Empty,
    };

    public static IReadOnlyList<string> Lines(string text) =>
        text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IReadOnlyList<int> Numbers(string text) =>
        text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out var i) ? i : -1)
            .Where(i => i > 0).Distinct().OrderBy(i => i).ToList();

    // ---------------------------------------------------------------- validation

    private void Revalidate() => Error = IsOverridden ? Validate() : null;

    private string? Validate()
    {
        var text = TextValue.Trim();
        switch (Definition.Kind)
        {
            case SettingKind.Int:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return $"'{Title}' must be a whole number.";
                if (n < Definition.Min || n > Definition.Max)
                    return $"'{Title}' must be between {Definition.Min} and {Definition.Max}.";
                return null;
            case SettingKind.Choice:
                if (Definition.Choices is { Count: > 0 } choices && !choices.Contains(text, StringComparer.OrdinalIgnoreCase))
                    return $"'{Title}' must be one of: {string.Join(", ", choices)}.";
                return null;
            case SettingKind.IntList:
                var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) return $"'{Title}' must list at least one number of minutes.";
                if (parts.Any(p => !int.TryParse(p, out var i) || i <= 0))
                    return $"'{Title}' must be a comma-separated list of positive whole numbers.";
                return null;
            default:
                return null;
        }
    }
}

/// <summary>A category of rows, as the Settings page and the application editor group them.</summary>
public sealed class SettingGroup
{
    public SettingGroup(string category, IReadOnlyList<SettingRow> rows)
    {
        Category = category;
        Rows = rows;
    }

    public string Category { get; }

    public IReadOnlyList<SettingRow> Rows { get; }

    /// <summary>Whether the group has anything to show at the current "Show advanced" setting.</summary>
    public bool IsShown(bool showAdvanced) => Rows.Any(r => r.IsShown(showAdvanced));

    /// <summary>How many rows of the group the document sets, for the group header.</summary>
    public int OverriddenCount => Rows.Count(r => r.IsOverridden);
}
