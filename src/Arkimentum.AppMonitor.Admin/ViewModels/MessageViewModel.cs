using System.Windows.Input;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.UI;

namespace Arkimentum.AppMonitor.Admin.ViewModels;

/// <summary>How a message reads: which glyph and which accent the brand-styled message window uses.</summary>
public enum DialogTone
{
    Info,
    Success,
    Warning,
    Critical,
}

/// <summary>The brand-styled replacement for MessageBox: a title, a body, and one or two buttons.</summary>
public sealed class MessageViewModel : DialogViewModel
{
    public MessageViewModel(string title, string message, DialogTone tone, string acceptText, string? cancelText)
    {
        Title = title;
        Message = message;
        Tone = tone;
        AcceptText = acceptText;
        CancelText = cancelText;
        AcceptCommand = new RelayCommand(() => Close(true));
        CancelCommand = new RelayCommand(() => Close(false));
    }

    public string Title { get; }

    public string Message { get; }

    public DialogTone Tone { get; }

    public string AcceptText { get; }

    public string? CancelText { get; }

    public bool HasCancel => !string.IsNullOrEmpty(CancelText);

    public string Glyph => Tone switch
    {
        DialogTone.Success => Strings.GlyphSuccess,
        DialogTone.Warning => Strings.GlyphWarning,
        DialogTone.Critical => Strings.GlyphError,
        _ => Strings.GlyphAbout,
    };

    public bool IsCritical => Tone == DialogTone.Critical;

    public bool IsWarning => Tone == DialogTone.Warning;

    public ICommand AcceptCommand { get; }

    public ICommand CancelCommand { get; }
}
