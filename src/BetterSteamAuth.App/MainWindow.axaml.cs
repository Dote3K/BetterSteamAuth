using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using BetterSteamAuth.App.Services;

namespace BetterSteamAuth.App;

public partial class MainWindow : Window, IPlatformServices
{
    private static readonly FilePickerFileType MaFileType = new("maFile") { Patterns = ["*.maFile"] };

    public MainWindow()
    {
        InitializeComponent();
    }

    public Task CopyTextAsync(string text) => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

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
