using System.Globalization;
using System.Linq;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Configuration;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>
/// One editable registry value, generated from a <see cref="SettingDefinition"/> — never hand-written per setting.
///
/// The row carries three layers: the policy value (locks the editor), the preference value (what this console
/// writes) and the inherited value shown as a hint when <see cref="IsOverridden"/> is off — the built-in default
/// for global settings, the catalog entry or the global <c>Default*</c> value for per-app settings.
/// </summary>
public sealed class SettingRowViewModel : ObservableObject
{
    private bool _isOverridden;
    private bool _boolValue;
    private string _textValue = string.Empty;
    private string? _error;
    private bool _isVisible = true;
    private bool _showAdvanced;
    private string _inheritedText = string.Empty;
    private string _inheritedLabel = Strings.InheritedFromBuiltIn;
    private SettingValue? _savedPreference;
    private readonly string _overriddenBadge;

    /// <param name="overriddenBadge">
    /// What the badge says when the row is overridden: "Preference" for this machine's registry, "Organization" for
    /// the document that lives in the cloud. The row itself has no idea which document it belongs to.
    /// </param>
    public SettingRowViewModel(SettingDefinition definition, SettingValue? preference, SettingValue? policy,
        string? overriddenBadge = null)
    {
        Definition = definition;
        Policy = policy;
        _overriddenBadge = string.IsNullOrEmpty(overriddenBadge) ? Strings.BadgePreference : overriddenBadge;
        Load(preference);
    }

    public SettingDefinition Definition { get; }

    public SettingValue? Policy { get; }

    public string Name => Definition.Name;

    public string Title => Definition.Title;

    public string Description => Definition.Description;

    public string Category => Definition.Category;

    public bool IsAdvanced => Definition.Advanced;

    public SettingKind Kind => Definition.Kind;

    public IReadOnlyList<string> Choices => Definition.Choices ?? [];

    /// <summary>Raised whenever the row's stored value changes, so the owning document can re-evaluate dirtiness.</summary>
    public event Action<SettingRowViewModel>? ValueChanged;

    // ---------------------------------------------------------------- state

    /// <summary>Whether the value is written to the registry at all. Off = the agent uses the inherited value.</summary>
    public bool IsOverridden
    {
        get => _isOverridden;
        set
        {
            if (IsLockedByPolicy || !SetProperty(ref _isOverridden, value)) return;
            // Clearing the override puts the inherited value back on screen, greyed out, as the hint it now is.
            if (!value) ShowInherited();
            OnPropertyChanged(nameof(IsEditorEnabled), nameof(SourceBadge));
            Revalidate();
            ValueChanged?.Invoke(this);
        }
    }

    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (!SetProperty(ref _boolValue, value)) return;
            Revalidate();
            ValueChanged?.Invoke(this);
        }
    }

    public string TextValue
    {
        get => _textValue;
        set
        {
            if (!SetProperty(ref _textValue, value ?? string.Empty)) return;
            Revalidate();
            ValueChanged?.Invoke(this);
        }
    }

    public bool IsLockedByPolicy => Policy is not null;

    public bool IsEditorEnabled => IsOverridden && !IsLockedByPolicy;

    public string SourceBadge => IsLockedByPolicy ? Strings.BadgePolicy
        : IsOverridden ? _overriddenBadge
        : Strings.BadgeDefault;

    public string? PolicyTooltip => IsLockedByPolicy ? Strings.PolicyValueTooltip(Format(Policy)) : null;

    /// <summary>Set by the owner when another row (typically Source) changes which fields apply.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value)) OnPropertyChanged(nameof(IsShown));
        }
    }

    /// <summary>Set by the page's "Show advanced" toggle.</summary>
    public bool ShowAdvanced
    {
        get => _showAdvanced;
        set
        {
            if (SetProperty(ref _showAdvanced, value)) OnPropertyChanged(nameof(IsShown));
        }
    }

    /// <summary>Whether the row appears on the page at all.</summary>
    public bool IsShown => IsVisible && (!IsAdvanced || ShowAdvanced);

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    // ---------------------------------------------------------------- hints

    /// <summary>The value the agent uses when the row is not overridden, and where it comes from.</summary>
    public void SetInherited(string? text, string label)
    {
        _inheritedText = text ?? string.Empty;
        _inheritedLabel = label;
        OnPropertyChanged(nameof(HintText), nameof(PlaceholderText), nameof(InheritedBoolValue));
        if (!IsOverridden && !IsLockedByPolicy) ShowInherited();
    }

    /// <summary>Puts the inherited value into the (disabled) editor so it reads as the placeholder it is.</summary>
    private void ShowInherited()
    {
        _boolValue = InheritedBoolValue;
        _textValue = _inheritedText;
        OnPropertyChanged(nameof(BoolValue), nameof(TextValue));
    }

    public string HintText => Strings.InheritedHint(_inheritedText, _inheritedLabel);

    /// <summary>Greyed text shown inside an empty, non-overridden editor.</summary>
    public string PlaceholderText => _inheritedText;

    /// <summary>What a non-overridden toggle shows.</summary>
    public bool InheritedBoolValue =>
        _inheritedText.Equals("true", StringComparison.OrdinalIgnoreCase) || _inheritedText == "1";

    public string RangeHint => Definition.IsNumeric && Definition.Kind == SettingKind.Int
        ? Strings.RangeHint(Definition.Min, Definition.Max)
        : string.Empty;

    public bool HasRangeHint => RangeHint.Length > 0;

    /// <summary>Path rows whose value names a folder rather than a file get the folder browser.</summary>
    public bool IsFolderPath =>
        Definition.Name.EndsWith("Directory", StringComparison.OrdinalIgnoreCase) ||
        Definition.Name.EndsWith("Folder", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- conversion

    /// <summary>Fills the editor from a stored value (null = not overridden).</summary>
    public void Load(SettingValue? preference)
    {
        _savedPreference = preference;
        _isOverridden = preference is not null;
        var source = Policy ?? preference;
        _boolValue = source?.AsBool() ?? AsBool(Definition.Default);
        _textValue = source is null ? DefaultText() : ToText(source);
        Revalidate();
        OnPropertyChanged(nameof(IsOverridden), nameof(BoolValue), nameof(TextValue), nameof(IsEditorEnabled), nameof(SourceBadge));
    }

    /// <summary>
    /// The value to store, or null when the row is not overridden.
    /// A policy-locked row passes its saved preference value through untouched: the policy layer is read-only here,
    /// and silently deleting the preference value underneath it would be a surprise.
    /// </summary>
    public SettingValue? ToValue() => IsLockedByPolicy ? _savedPreference : IsOverridden ? Build() : null;

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
        SettingKind.StringList => string.Join(Environment.NewLine, value.AsStringList()),
        SettingKind.IntList => string.Join(",", value.AsIntList()),
        SettingKind.Bool => value.AsBool() is true ? "1" : "0",
        _ => value.AsString() ?? string.Empty,
    };

    private string DefaultText() => Definition.Default switch
    {
        null => string.Empty,
        bool b => b ? "1" : "0",
        _ => Definition.Default.ToString() ?? string.Empty,
    };

    private static bool AsBool(object? value) => value switch
    {
        bool b => b,
        int i => i != 0,
        string s => s is "1" or "true" or "True",
        _ => false,
    };

    /// <summary>Formats any stored value for a tooltip or a hint line.</summary>
    public string Format(SettingValue? value) => value is null ? string.Empty : Definition.Kind switch
    {
        SettingKind.Bool => value.AsBool() is true ? Strings.Yes : Strings.No,
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

    private void Revalidate()
    {
        Error = !IsOverridden || IsLockedByPolicy ? null : Validate();
        OnPropertyChanged(nameof(HasError));
    }

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
