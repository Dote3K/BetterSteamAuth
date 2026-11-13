using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public enum ImportSource
{
    SdaFolder,
    MaFiles,
}

public sealed partial class ImportItemViewModel(ImportCandidate candidate) : ObservableObject
{
    public ImportCandidate Candidate { get; } = candidate;

    public string Name => Candidate.DisplayName;

    public string? Problem => Candidate.Problem;

    public bool CanImport => Candidate.Status == ImportStatus.Ready;

    public bool IsError => Candidate.Status == ImportStatus.Unreadable;

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = candidate.Status == ImportStatus.Ready;
}

/// <summary>Pick → select accounts → done. Never modifies the source.</summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private static readonly CompositeFormat ImportCountFormat = CompositeFormat.Parse(Strings.ImportCountFormat);
    private static readonly CompositeFormat ReadyCountFormat = CompositeFormat.Parse(Strings.AccountsReadyFormat);
    private static readonly CompositeFormat SkippedFormat = CompositeFormat.Parse(Strings.SkippedFormat);

    private readonly ShellViewModel _shell;
    private readonly Action _onDone;
    private string? _folder;

    public ImportViewModel(ShellViewModel shell, ImportSource source, Action onDone)
    {
        _shell = shell;
        _onDone = onDone;
        Source = source;
        Title = source == ImportSource.SdaFolder ? Strings.ImportFromSda : Strings.ImportMaFiles;
    }

    public ImportSource Source { get; }

    public bool IsSdaSource => Source == ImportSource.SdaFolder;

    public string Title { get; }

    public ObservableCollection<ImportItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string? SourceLabel { get; private set; }

    [ObservableProperty]
    public partial bool NeedsPasskey { get; private set; }

    [ObservableProperty]
    public partial string Passkey { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    public partial int ItemCount { get; private set; }

    public bool HasItems => ItemCount > 0;

    [ObservableProperty]
    public partial string ImportButtonText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    [ObservableProperty]
    public partial string? DoneTitle { get; private set; }

    [ObservableProperty]
    public partial string? DoneSummary { get; private set; }

    partial void OnPasskeyChanged(string value) => Error = null;

    [RelayCommand]
    private async Task PickAsync()
    {
        Error = null;
        if (Source == ImportSource.SdaFolder)
        {
            string? folder = await _shell.Platform.PickFolderAsync(Strings.ImportFromSda).ConfigureAwait(true);
            if (folder is null)
            {
                return;
            }

            if (!SdaImporter.IsSdaFolder(folder))
            {
                Error = Strings.NotAnSdaFolder;
                return;
            }

            _folder = folder;
            SourceLabel = folder;
            NeedsPasskey = SdaImporter.IsEncrypted(folder);
            if (!NeedsPasskey)
            {
                ReadFolder();
            }
        }
        else
        {
            var files = await _shell.Platform.PickMaFilesAsync(Strings.ImportMaFiles).ConfigureAwait(true);
            if (files.Count == 0)
            {
                return;
            }

            SourceLabel = string.Join(", ", files.Select(Path.GetFileName));
            Show(SdaImporter.ReadFiles(files, RequireVault()));
        }
    }

    [RelayCommand]
    private void UnlockSda() => ReadFolder();

    [RelayCommand]
    private void Import()
    {
        var chosen = Items.Where(i => i.IsSelected && i.CanImport).Select(i => i.Candidate).ToList();
        int added = SdaImporter.Import(RequireVault(), chosen);
        int duplicates = Items.Count(i => i.Candidate.Status == ImportStatus.AlreadyAdded);
        int unreadable = Items.Count(i => i.Candidate.Status == ImportStatus.Unreadable);

        DoneTitle = string.Format(CultureInfo.CurrentCulture, ReadyCountFormat, added);
        DoneSummary = duplicates + unreadable > 0
            ? string.Format(CultureInfo.CurrentCulture, SkippedFormat, duplicates, unreadable)
            : null;
        IsDone = true;
    }

    [RelayCommand]
    private void Finish() => _onDone();

    [RelayCommand]
    private void Cancel() => _onDone();

    private void ReadFolder()
    {
        if (_folder is null)
        {
            return;
        }

        try
        {
            Show(SdaImporter.ReadFolder(_folder, NeedsPasskey ? Passkey : null, RequireVault()));
            NeedsPasskey = false;
        }
        catch (WrongPasswordException)
        {
            Error = Strings.WrongPasskey;
        }
    }

    private void Show(IReadOnlyList<ImportCandidate> candidates)
    {
        Items.Clear();
        foreach (var candidate in candidates)
        {
            var item = new ImportItemViewModel(candidate);
            item.PropertyChanged += (_, _) => UpdateButton();
            Items.Add(item);
        }

        ItemCount = Items.Count;
        UpdateButton();
    }

    private void UpdateButton()
        => ImportButtonText = string.Format(
            CultureInfo.CurrentCulture, ImportCountFormat, Items.Count(i => i.IsSelected && i.CanImport));

    private Vault RequireVault() => _shell.Vault ?? throw new InvalidOperationException("No vault is open.");
}
