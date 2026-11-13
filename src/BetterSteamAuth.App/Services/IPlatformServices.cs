namespace BetterSteamAuth.App.Services;

/// <summary>Window-level services view models need: clipboard and file pickers.</summary>
public interface IPlatformServices
{
    Task CopyTextAsync(string text);

    Task<string?> PickFolderAsync(string title);

    Task<IReadOnlyList<string>> PickMaFilesAsync(string title);
}
