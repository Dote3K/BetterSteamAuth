using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using BetterSteamAuth.App.Services;
using BetterSteamAuth.App.ViewModels;

namespace BetterSteamAuth.App;

public partial class MainWindow : Window, IPlatformServices
{
    private static readonly FilePickerFileType MaFileType = new("maFile") { Patterns = ["*.maFile"] };
    private static readonly FilePickerFileType BackupFileType = new("BetterSteamAuth backup") { Patterns = ["*" + Storage.VaultBackup.Extension] };

    public MainWindow()
    {
        InitializeComponent();

        // Any input counts as activity for auto-lock, even when a control handles it.
        AddHandler(KeyDownEvent, OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnInput, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnInput(object? sender, RoutedEventArgs e) => (DataContext as ShellViewModel)?.NoteActivity();

    /// <summary>With Keep running in the tray on, closing the window hides it; quitting and Windows shutting down still close.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.CloseReason == WindowCloseReason.WindowClosing && DataContext is ShellViewModel { KeepInTray: true } shell)
        {
            e.Cancel = true;
            Hide();
            shell.HiddenToTray();
        }

        base.OnClosing(e);
    }

    /// <summary>Back from the tray or minimized, in front.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        (DataContext as ShellViewModel)?.SetMinimized(false);
    }

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            // Confirmation checks pause while minimized.
            (DataContext as ShellViewModel)?.SetMinimized(WindowState == WindowState.Minimized);
        }
    }

    /// <summary>Another app can hold the Windows clipboard for a moment, so a failed copy is tried a few more times.</summary>
    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is not { } clipboard)
        {
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await clipboard.SetTextAsync(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 5)
            {
                await Task.Delay(50 * attempt);
            }
        }
    }

    public Task OpenUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri);

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [suggestedName.EndsWith(Storage.VaultBackup.Extension, StringComparison.OrdinalIgnoreCase) ? BackupFileType : MaFileType],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickBackupAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [BackupFileType, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public void RevealFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        else
        {
            _ = Launcher.LaunchUriAsync(new Uri(Path.GetDirectoryName(path)!));
        }
    }

    public async Task<IReadOnlyList<string>> PickMaFilesAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [MaFileType, FilePickerFileTypes.All],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }
}
