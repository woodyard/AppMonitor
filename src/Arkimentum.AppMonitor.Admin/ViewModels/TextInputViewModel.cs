using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>A one-line prompt with live validation — "Add custom…" and "Duplicate".</summary>
public sealed class TextInputViewModel : DialogViewModel
{
    private readonly Func<string, string?> _validate;
    private readonly RelayCommand _acceptCommand;
    private string _text;
    private string? _error;

    public TextInputViewModel(string title, string prompt, string initial, string acceptText, Func<string, string?> validate)
    {
        Title = title;
        Prompt = prompt;
        AcceptText = acceptText;
        _validate = validate;
        _text = initial;
        _acceptCommand = new RelayCommand(() => Close(true), () => !HasError);
        CancelCommand = new RelayCommand(() => Close(false));
        Revalidate();
    }

    public string Title { get; }

    public string Prompt { get; }

    public string AcceptText { get; }

    public string CancelText => Strings.Cancel;

    public string Text
    {
        get => _text;
        set
        {
            if (!SetProperty(ref _text, value ?? string.Empty)) return;
            Revalidate();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public ICommand AcceptCommand => _acceptCommand;

    public ICommand CancelCommand { get; }

    private void Revalidate()
    {
        Error = _validate(_text);
        _acceptCommand.RaiseCanExecuteChanged();
    }
}
