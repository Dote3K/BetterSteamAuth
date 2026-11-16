using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Accounts;
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

/// <summary>
/// Pick → select accounts → connection → done. Never modifies the source, and nothing is sent to Steam
/// before the connection is chosen.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private static readonly CompositeFormat ImportCountFormat = CompositeFormat.Parse(Strings.ImportCountFormat);
    private static readonly CompositeFormat ReadyCountFormat = CompositeFormat.Parse(Strings.AccountsReadyFormat);
    private static readonly CompositeFormat SkippedFormat = CompositeFormat.Parse(Strings.SkippedFormat);
    private static readonly CompositeFormat DirectHintFormat = CompositeFormat.Parse(Strings.DirectHintFormat);
    private static readonly CompositeFormat ProxiesForFormat = CompositeFormat.Parse(Strings.ProxiesForAccountsFormat);
    private static readonly CompositeFormat ExtraProxiesFormat = CompositeFormat.Parse(Strings.ExtraProxiesFormat);
    private static readonly CompositeFormat TooFewProxiesFormat = CompositeFormat.Parse(Strings.TooFewProxiesFormat);

    private readonly ShellViewModel _shell;
    private readonly Action _onDone;
    private string? _folder;
    private List<ImportCandidate> _chosen = [];

    public ImportViewModel(ShellViewModel shell, ImportSource source, Action onDone)
    {
        _shell = shell;
        _onDone = onDone;
        Source = source;
        Title = source == ImportSource.SdaFolder ? Strings.ImportFromSda : Strings.ImportMaFiles;
        Subtitle = source == ImportSource.SdaFolder ? Strings.ImportFromSdaHint : Strings.ImportMaFilesHint;
        Paste = new ProxyPasteViewModel(() => RequireVault().ListProxies().Select(p => new ProxySettings(p.Scheme, p.Host, p.Port, p.Username)));
        Paste.PropertyChanged += (_, _) => UpdateConnection();
    }

    public ImportSource Source { get; }

    public bool IsSdaSource => Source == ImportSource.SdaFolder;

    public string PickLabel => IsSdaSource ? Strings.ChooseFolder : Strings.ChooseFiles;

    public string Title { get; }

    public string Subtitle { get; }

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
    [NotifyPropertyChangedFor(nameof(IsSelecting))]
    public partial bool IsConnection { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelecting))]
    public partial bool IsDone { get; private set; }

    public bool IsSelecting => !IsConnection && !IsDone;

    // Connection step
    public ProxyPasteViewModel Paste { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseDirect))]
    public partial bool UseProxies { get; set; }

    public bool UseDirect
    {
        get => !UseProxies;
        set => UseProxies = !value;
    }

    [ObservableProperty]
    public partial string DirectHint { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxySummary))]
    public partial string? ProxySummary { get; private set; }

    public bool HasProxySummary => ProxySummary is not null;

    [ObservableProperty]
    public partial bool ProxySummaryIsError { get; private set; }

    [ObservableProperty]
    public partial bool CanConnect { get; private set; } = true;

    partial void OnUseProxiesChanged(bool value) => UpdateConnection();

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

    /// <summary>Accounts chosen; ask how they connect before anything is saved or sent.</summary>
    [RelayCommand]
    private void Import()
    {
        _chosen = Items.Where(i => i.IsSelected && i.CanImport).Select(i => i.Candidate).ToList();
        if (_chosen.Count == 0)
        {
            return;
        }

        DirectHint = _chosen.Count == 1
            ? Strings.DirectHintOne
            : string.Format(CultureInfo.CurrentCulture, DirectHintFormat, _chosen.Count);
        UpdateConnection();
        IsConnection = true;
    }

    [RelayCommand]
    private void BackToSelect() => IsConnection = false;

    [RelayCommand]
    private void Connect()
    {
        UpdateConnection();
        if (!CanConnect)
        {
            return;
        }

        var vault = RequireVault();
        int added = SdaImporter.Import(vault, _chosen);
        if (UseProxies)
        {
            // One proxy per account, in the order they were pasted; extras stay in the pool.
            vault.AddProxies(Paste.Proxies);
            var ids = vault.ListProxies().ToDictionary(p => (p.Scheme, p.Host, p.Port, p.Username), p => p.Id);
            foreach (var (candidate, proxy) in _chosen.Zip(Paste.Proxies))
            {
                vault.SetAccountProxy(candidate.SteamId!.Value, ids[(proxy.Scheme, proxy.Host, proxy.Port, proxy.Username)]);
            }
        }

        int duplicates = Items.Count(i => i.Candidate.Status == ImportStatus.AlreadyAdded);
        int unreadable = Items.Count(i => i.Candidate.Status == ImportStatus.Unreadable);

        DoneTitle = string.Format(CultureInfo.CurrentCulture, ReadyCountFormat, added);
        string connection = UseProxies ? Strings.ConnectionProxies : Strings.ConnectionDirect;
        DoneSummary = duplicates + unreadable > 0
            ? connection + " · " + string.Format(CultureInfo.CurrentCulture, SkippedFormat, duplicates, unreadable)
            : connection;
        IsConnection = false;
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

    private void UpdateConnection()
    {
        int accounts = _chosen.Count;
        int proxies = Paste.Count;
        if (!UseProxies)
        {
            ProxySummary = null;
            CanConnect = true;
            return;
        }

        if (proxies == 0 && Paste.ErrorCount == 0)
        {
            ProxySummary = null;
            ProxySummaryIsError = false;
        }
        else if (Paste.ErrorCount > 0)
        {
            ProxySummary = Strings.FixUnreadableLines;
            ProxySummaryIsError = true;
        }
        else if (proxies < accounts)
        {
            ProxySummary = string.Format(CultureInfo.CurrentCulture, TooFewProxiesFormat, accounts, proxies, accounts - proxies);
            ProxySummaryIsError = true;
        }
        else
        {
            ProxySummary = string.Format(CultureInfo.CurrentCulture, ProxiesForFormat, proxies, accounts)
                + (proxies > accounts ? " · " + string.Format(CultureInfo.CurrentCulture, ExtraProxiesFormat, proxies - accounts) : "");
            ProxySummaryIsError = false;
        }

        // An unreadable line would shift the order, so it must be fixed first.
        CanConnect = proxies >= accounts && Paste.ErrorCount == 0;
    }

    private void UpdateButton()
        => ImportButtonText = string.Format(
            CultureInfo.CurrentCulture, ImportCountFormat, Items.Count(i => i.IsSelected && i.CanImport));

    private Vault RequireVault() => _shell.Vault ?? throw new InvalidOperationException("No vault is open.");
}
