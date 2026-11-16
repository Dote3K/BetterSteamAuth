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
/// Restore from backup into this app: adds the accounts it doesn't have and keeps the rest as they are.
/// </summary>
public sealed partial class RestoreViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat BackupSummaryFormat = CompositeFormat.Parse(Strings.BackupSummaryFormat);
    private static readonly CompositeFormat WillAddFormat = CompositeFormat.Parse(Strings.RestoreWillAddFormat);
    private static readonly CompositeFormat AlreadyHereFormat = CompositeFormat.Parse(Strings.RestoreAlreadyHereFormat);
    private static readonly CompositeFormat RestoredFormat = CompositeFormat.Parse(Strings.RestoredFormat);

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private readonly Action _back;
    private OpenedBackup? _backup;
    private int _toAdd;

    public RestoreViewModel(ShellViewModel shell, Action back)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        _back = back;
    }

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
            int added = await Task.Run(() => _vault.MergeFrom(source)).ConfigureAwait(true);
            DoneText = string.Format(CultureInfo.CurrentCulture, RestoredFormat, added);
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
        var candidates = _vault.PreviewMerge(_backup!.Vault);
        _toAdd = candidates.Count(c => !c.AlreadyHere);
        int here = candidates.Count - _toAdd;
        WillAdd = _toAdd > 0 ? string.Format(CultureInfo.CurrentCulture, WillAddFormat, _toAdd) : Strings.RestoreNothingToAdd;
        AlreadyHere = here > 0 ? string.Format(CultureInfo.CurrentCulture, AlreadyHereFormat, here) : null;
        CanRestore = _toAdd > 0;
        Step = RestoreStep.Preview;
    }

    private void CloseBackup()
    {
        _backup?.Dispose();
        _backup = null;
    }
}
