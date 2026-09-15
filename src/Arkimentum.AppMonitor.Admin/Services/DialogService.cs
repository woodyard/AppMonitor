using System.Diagnostics;
using System.IO;
using System.Windows;
using Arkimentum.AppMonitor.Admin.Resources;
using Arkimentum.AppMonitor.Admin.ViewModels;
using Arkimentum.AppMonitor.Admin.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Arkimentum.AppMonitor.Admin.Services;

/// <summary>Everything the view models need from the shell: modal windows, file pickers, the clipboard, Explorer.</summary>
public interface IDialogService
{
    /// <summary>The window modal children are owned by. Set once the main window exists.</summary>
    Window? Owner { get; set; }

    void ShowMessage(string title, string message, DialogTone tone = DialogTone.Info);

    bool Confirm(string title, string message, string acceptText, string cancelText, DialogTone tone = DialogTone.Warning);

    /// <summary>Shows the window that belongs to <paramref name="viewModel"/>; true when it was accepted.</summary>
    bool ShowDialog(DialogViewModel viewModel);

    string? SaveFile(string fileName, string filter, string defaultExtension);

    string? OpenFile(string filter);

    string? PickPath(string? initial, bool folder);

    void OpenFolder(string path);

    void CopyToClipboard(string text);
}

/// <inheritdoc />
public sealed class DialogService : IDialogService
{
    private readonly ILogger<DialogService> _log;

    public DialogService(ILogger<DialogService> log) => _log = log;

    public Window? Owner { get; set; }

    public void ShowMessage(string title, string message, DialogTone tone = DialogTone.Info) =>
        ShowDialog(new MessageViewModel(title, message, tone, Strings.ButtonOk, null));

    public bool Confirm(string title, string message, string acceptText, string cancelText, DialogTone tone = DialogTone.Warning) =>
        ShowDialog(new MessageViewModel(title, message, tone, acceptText, cancelText));

    public bool ShowDialog(DialogViewModel viewModel)
    {
        Window window = viewModel switch
        {
            MessageViewModel => new MessageWindow(),
            TextInputViewModel => new TextInputWindow(),
            AddFromCatalogViewModel => new CatalogPickerWindow(),
            DiscoverViewModel => new DiscoverWindow(),
            AboutViewModel => new AboutWindow(),
            _ => throw new ArgumentOutOfRangeException(nameof(viewModel), viewModel.GetType().Name, "No window is registered for this view model."),
        };

        window.DataContext = viewModel;
        if (Owner is { IsVisible: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        void OnRequestClose(bool accepted)
        {
            window.DialogResult = accepted;
        }

        viewModel.RequestClose += OnRequestClose;
        try
        {
            var result = window.ShowDialog();
            _log.LogDebug("{Window} closed with result {Result}.", window.GetType().Name, result);
            return result == true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "{Window} failed.", window.GetType().Name);
            throw;
        }
        finally { viewModel.RequestClose -= OnRequestClose; }
    }

    public string? SaveFile(string fileName, string filter, string defaultExtension)
    {
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            Filter = filter,
            DefaultExt = defaultExtension,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? OpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(Owner) == true ? dialog.FileName : null;
    }

    public string? PickPath(string? initial, bool folder)
    {
        try
        {
            if (folder)
            {
                var picker = new OpenFolderDialog { Multiselect = false };
                TrySetInitial(initial, d => picker.InitialDirectory = d);
                return picker.ShowDialog(Owner) == true ? picker.FolderName : null;
            }

            var file = new OpenFileDialog { CheckFileExists = false, Filter = "All files (*.*)|*.*" };
            TrySetInitial(initial, d => file.InitialDirectory = d);
            return file.ShowDialog(Owner) == true ? file.FileName : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The file picker could not be shown.");
            return null;
        }
    }

    public void OpenFolder(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            Directory.CreateDirectory(path);
            _log.LogInformation("Opening folder {Path}.", path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not open folder {Path}.", path);
            ShowMessage(Strings.ProductName, ex.Message, DialogTone.Warning);
        }
    }

    public void CopyToClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not write to the clipboard."); }
    }

    private static void TrySetInitial(string? initial, Action<string> set)
    {
        if (string.IsNullOrWhiteSpace(initial)) return;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(initial);
            var directory = Directory.Exists(expanded) ? expanded : Path.GetDirectoryName(expanded);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) set(directory);
        }
        catch { /* an initial directory is a convenience, never a requirement */ }
    }
}
