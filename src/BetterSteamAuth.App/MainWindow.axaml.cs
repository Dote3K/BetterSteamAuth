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

    public Task CopyTextAsync(string text) => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    public Task OpenUriAsync(Uri uri) => Launcher.LaunchUriAsync(uri);

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
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
