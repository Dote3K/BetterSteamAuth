using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Export for SDA / ASF: a maFiles folder, plain or passkey-encrypted.</summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private static readonly CompositeFormat ExportedFormat = CompositeFormat.Parse(Strings.ExportedFormat);

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private string? _exportedFolder;

    public ExportViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        foreach (var entry in _vault.ListAccounts())
        {
            string name = entry.AccountName ?? TryName(entry.SteamId) ?? entry.SteamId.ToString(CultureInfo.InvariantCulture);
            var item = new ExportItem(entry.SteamId, name);
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));
            Accounts.Add(item);
        }
    }

    public ObservableCollection<ExportItem> Accounts { get; } = [];

    public int SelectedCount => Accounts.Count(a => a.IsSelected);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Plain))]
    public partial bool SdaEncrypted { get; set; }

    public bool Plain
    {
        get => !SdaEncrypted;
        set => SdaEncrypted = !value;
    }

    [ObservableProperty] public partial string Passkey { get; set; } = "";
    [ObservableProperty] public partial string ConfirmPasskey { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string? Result { get; private set; }

    public bool HasResult => Result is not null;

    partial void OnPasskeyChanged(string value) => Error = null;

    partial void OnConfirmPasskeyChanged(string value) => Error = null;

    [RelayCommand]
    private void Back() => _shell.ShowSettings();

    [RelayCommand]
    private void SelectAll() => Select(true);

    [RelayCommand]
    private void SelectNone() => Select(false);

    private void Select(bool selected)
    {
        foreach (var item in Accounts)
        {
            item.IsSelected = selected;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var ids = Accounts.Where(a => a.IsSelected).Select(a => a.SteamId).ToList();
        if (ids.Count == 0)
        {
            Error = Strings.ExportChooseAccounts;
            return;
        }

        if (SdaEncrypted && (string.IsNullOrEmpty(Passkey) || Passkey != ConfirmPasskey))
        {
            Error = string.IsNullOrEmpty(Passkey) ? Strings.ExportPasskeyNeeded : Strings.PasswordsDontMatch;
            return;
        }

        string? folder = await _shell.Platform.PickFolderAsync(Strings.ExportPickFolder).ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        Result = null;
        string? passkey = SdaEncrypted ? Passkey : null;
        try
        {
            _exportedFolder = await Task.Run(() => SdaExporter.Export(_vault, ids, folder, passkey)).ConfigureAwait(true);
            Result = string.Format(CultureInfo.CurrentCulture, ExportedFormat, ids.Count);
        }
        catch (SdaFolderNotEmptyException)
        {
            Error = Strings.ExportFolderHasSda;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Strings.ExportFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (_exportedFolder is not null)
        {
            _shell.Platform.RevealFile(Path.Combine(_exportedFolder, SdaImporter.ManifestFileName));
        }
    }

    private string? TryName(ulong steamId)
    {
        try
        {
            return _vault.Load(steamId).AccountName;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or VaultLockedException)
        {
            return null;
        }
    }
}

/// <summary>One account in the export checklist.</summary>
public sealed partial class ExportItem(ulong steamId, string name) : ObservableObject
{
    public ulong SteamId { get; } = steamId;

    public string Name { get; } = name;

    [ObservableProperty] public partial bool IsSelected { get; set; } = true;
}
