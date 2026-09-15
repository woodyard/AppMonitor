using System.Windows;
using System.Windows.Controls;
using Arkimentum.AppMonitor.Admin.ViewModels;
using Arkimentum.AppMonitor.Configuration;

namespace Arkimentum.AppMonitor.Admin.Infrastructure;

/// <summary>
/// Picks the editor for a generated row from its <see cref="SettingKind"/>, so the Settings and Applications pages
/// never enumerate settings by hand: the schema decides what each row looks like.
/// Templates are looked up by key, e.g. <c>SettingEditorChoice</c>.
/// </summary>
public sealed class SettingEditorTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not SettingRowViewModel row || container is not FrameworkElement element) return null;
        return element.TryFindResource("SettingEditor" + row.Kind) as DataTemplate
               ?? element.TryFindResource("SettingEditorString") as DataTemplate;
    }
}
