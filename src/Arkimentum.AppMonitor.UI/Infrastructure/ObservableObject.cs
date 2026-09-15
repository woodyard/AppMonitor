using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Arkimentum.AppMonitor.UI;

/// <summary>Minimal INotifyPropertyChanged base; the project does not need a full MVVM framework.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void OnPropertyChanged(params string[] propertyNames)
    {
        foreach (var name in propertyNames) OnPropertyChanged(name);
    }

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
