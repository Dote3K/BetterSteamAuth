namespace BetterSteamAuth.App.Services;

/// <summary>Window-level services view models need: clipboard and file pickers.</summary>
public interface IPlatformServices
{
    Task CopyTextAsync(string text);

    Task OpenUriAsync(Uri uri);

    Task<string?> PickFolderAsync(string title);

    Task<IReadOnlyList<string>> PickMaFilesAsync(string title);

    /// <summary>Asks for one <c>.bsabackup</c> file; null when cancelled.</summary>
    Task<string?> PickBackupAsync(string title);

    /// <summary>Asks where to save a file; null when cancelled.</summary>
    Task<string?> SaveFileAsync(string title, string suggestedName);

    /// <summary>Opens the file's folder with the file selected.</summary>
    void RevealFile(string path);
}
