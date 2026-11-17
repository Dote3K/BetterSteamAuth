using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Security;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public enum RestoreStep
{
    Pick,
    Password,
    Preview,
    Done,
}

/// <summary>
/// Restore from backup. Into this app (merge): adds the accounts it doesn't have and keeps the rest as they are.
/// Replacing (forgot password, first run): the backup becomes the app's data; the old folder is kept aside.
/// </summary>
public sealed partial class RestoreViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat BackupSummaryFormat = CompositeFormat.Parse(Strings.BackupSummaryFormat);
    private static readonly CompositeFormat WillAddFormat = CompositeFormat.Parse(Strings.RestoreWillAddFormat);
    private static readonly CompositeFormat AlreadyHereFormat = CompositeFormat.Parse(Strings.RestoreAlreadyHereFormat);
    private static readonly CompositeFormat RestoredFormat = CompositeFormat.Parse(Strings.RestoredFormat);
    private static readonly CompositeFormat KeptFormat = CompositeFormat.Parse(Strings.RestoreKeptFormat);

    private readonly ShellViewModel _shell;
    private readonly Vault? _vault;
    private readonly Action _back;
    private readonly bool _replace;
    private readonly string _keptFolder;
    private OpenedBackup? _backup;

    public RestoreViewModel(ShellViewModel shell, Action back, bool replace)
    {
        _shell = shell;
        _replace = replace;
        _vault = replace ? null : shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _back = back;
        _keptFolder = VaultBackup.KeptFolderFor(shell.DataFolder, DateTime.Now);
    }

    public string Body => _replace ? Strings.RestoreReplaceBody : Strings.RestoreBody;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPick), nameof(IsPassword), nameof(IsPreview), nameof(IsDone))]
    public partial RestoreStep Step { get; private set; } = RestoreStep.Pick;

    public bool IsPick => Step == RestoreStep.Pick;

    public bool IsPassword => Step == RestoreStep.Password;

    public bool IsPreview => Step == RestoreStep.Preview;

    public bool IsDone => Step == RestoreStep.Done;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial string? WillAdd { get; private set; }
    [ObservableProperty] public partial string? AlreadyHere { get; private set; }
    [ObservableProperty] public partial bool CanRestore { get; private set; }
    [ObservableProperty] public partial string DoneText { get; private set; } = "";
    [ObservableProperty] public partial string? DoneNote { get; private set; }

    partial void OnPasswordChanged(string value) => Error = null;

    public void Dispose() => CloseBackup();

    [RelayCommand]
    private void Back() => _back();

    [RelayCommand]
    private async Task PickAsync()
    {
        string? path = await _shell.Platform.PickBackupAsync(Strings.RestoreFromBackup).ConfigureAwait(true);
        if (path is null)
        {
            return;
        }

        CloseBackup();
        IsBusy = true;
        Error = null;
        try
        {
            _backup = await Task.Run(() => VaultBackup.Open(path)).ConfigureAwait(true);
            Summary = string.Format(CultureInfo.CurrentCulture, BackupSummaryFormat,
                _backup.Manifest.Created.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture), _backup.Manifest.Accounts);
            if (_backup.Vault.IsUnlocked)
            {
                ShowPreview();
            }
            else
            {
                Password = "";
                Step = RestoreStep.Password;
            }
        }
        catch (VaultTooNewException)
        {
            CloseBackup();
            Error = Strings.BackupTooNew;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            CloseBackup();
            Error = Strings.NotABackup;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        if (_backup is null || IsBusy || string.IsNullOrEmpty(Password))
        {
            return;
        }

        IsBusy = true;
        string password = Password;
        try
        {
            await Task.Run(() => _backup.Vault.Unlock(password)).ConfigureAwait(true);
            Password = "";
            ShowPreview();
        }
        catch (WrongPasswordException)
        {
            Error = Strings.WrongPassword;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (_backup is null || IsBusy || !CanRestore)
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var source = _backup.Vault;
            if (_replace)
            {
                // On the UI thread: it closes and reopens the app's data. The files are small.
                var backup = _backup;
                string? kept = _shell.ReplaceDataWithBackup(backup, _keptFolder);
                DoneText = string.Format(CultureInfo.CurrentCulture, RestoredFormat, backup.Manifest.Accounts);
                DoneNote = kept is null ? null : string.Format(CultureInfo.CurrentCulture, KeptFormat, kept);
            }
            else
            {
                int added = await Task.Run(() => _vault!.MergeFrom(source)).ConfigureAwait(true);
                DoneText = string.Format(CultureInfo.CurrentCulture, RestoredFormat, added);
            }

            CloseBackup();
            Step = RestoreStep.Done;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = Strings.RestoreFailed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Open() => _shell.ShowMain();

    private void ShowPreview()
    {
        if (_replace)
        {
            WillAdd = Strings.RestoreReplacesData;
            bool hasData = Directory.Exists(_shell.DataFolder) && Directory.EnumerateFileSystemEntries(_shell.DataFolder).Any();
            AlreadyHere = hasData ? string.Format(CultureInfo.CurrentCulture, KeptFormat, _keptFolder) : null;
            CanRestore = true;
            Step = RestoreStep.Preview;
            return;
        }

        var candidates = _vault!.PreviewMerge(_backup!.Vault);
        int toAdd = candidates.Count(c => !c.AlreadyHere);
        int here = candidates.Count - toAdd;
        WillAdd = toAdd > 0 ? string.Format(CultureInfo.CurrentCulture, WillAddFormat, toAdd) : Strings.RestoreNothingToAdd;
        AlreadyHere = here > 0 ? string.Format(CultureInfo.CurrentCulture, AlreadyHereFormat, here) : null;
        CanRestore = toAdd > 0;
        Step = RestoreStep.Preview;
    }

    private void CloseBackup()
    {
        _backup?.Dispose();
        _backup = null;
    }
}
