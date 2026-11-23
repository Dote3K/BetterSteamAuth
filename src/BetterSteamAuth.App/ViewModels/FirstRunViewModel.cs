using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public enum FirstRunStep
{
    Welcome,
    Password,
}

public enum StartChoice
{
    ImportFromSda,
    ImportMaFiles,
    Link,
    AddLater,
}

/// <summary>Welcome and password steps of first run.</summary>
public sealed partial class FirstRunViewModel(ShellViewModel shell) : ObservableObject
{
    private const int MinPasswordLength = 8;

    private static readonly CompositeFormat DataFolderFormat = CompositeFormat.Parse(Strings.DataFolderFormat);
    private static readonly CompositeFormat DataFolderFailedFormat = CompositeFormat.Parse(Strings.DataFolderFailedFormat);

    public string DataFolder => shell.DataFolder;

    [RelayCommand]
    private void RestoreFromBackup() => shell.ShowRestoreReplacing(fromFirstRun: true);

    public string DataFolderText => string.Format(CultureInfo.CurrentCulture, DataFolderFormat, shell.DataFolder);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsPassword))]
    public partial FirstRunStep Step { get; private set; } = FirstRunStep.Welcome;

    public bool IsWelcome => Step == FirstRunStep.Welcome;

    public bool IsPassword => Step == FirstRunStep.Password;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoiceIsSda), nameof(ChoiceIsFiles), nameof(ChoiceIsLink), nameof(ChoiceIsLater))]
    public partial StartChoice Choice { get; set; } = StartChoice.ImportFromSda;

    public bool ChoiceIsSda
    {
        get => Choice == StartChoice.ImportFromSda;
        set { if (value) Choice = StartChoice.ImportFromSda; }
    }

    public bool ChoiceIsFiles
    {
        get => Choice == StartChoice.ImportMaFiles;
        set { if (value) Choice = StartChoice.ImportMaFiles; }
    }

    public bool ChoiceIsLink
    {
        get => Choice == StartChoice.Link;
        set { if (value) Choice = StartChoice.Link; }
    }

    public bool ChoiceIsLater
    {
        get => Choice == StartChoice.AddLater;
        set { if (value) Choice = StartChoice.AddLater; }
    }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool RememberOnDevice { get; set; } = true;

    [ObservableProperty]
    public partial bool ShowNoPasswordNote { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    partial void OnPasswordChanged(string value) => Error = null;

    partial void OnConfirmPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private void Continue() => Step = FirstRunStep.Password;

    [RelayCommand]
    private void Back()
    {
        Step = FirstRunStep.Welcome;
        ShowNoPasswordNote = false;
    }

    [RelayCommand]
    private async Task SetPasswordAsync()
    {
        if (Password.Length < MinPasswordLength)
        {
            Error = Strings.PasswordTooShort;
            return;
        }

        if (Password != ConfirmPassword)
        {
            Error = Strings.PasswordsDontMatch;
            return;
        }

        await CreateVaultAsync(Password).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenSettings() => shell.ShowSetupSettings(this);

    /// <summary>First click shows the warning; the second click continues.</summary>
    [RelayCommand]
    private async Task ContinueWithoutPasswordAsync()
    {
        if (!ShowNoPasswordNote)
        {
            ShowNoPasswordNote = true;
            return;
        }

        await CreateVaultAsync(password: null).ConfigureAwait(true);
    }

    private async Task CreateVaultAsync(string? password)
    {
        IsBusy = true;
        try
        {
            var vault = await Task.Run(() => Vault.Create(shell.DataFolder, password)).ConfigureAwait(true);
            shell.UseVault(vault);
            if (password is not null && RememberOnDevice)
            {
                shell.SetRemembered(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = string.Format(CultureInfo.CurrentCulture, DataFolderFailedFormat, shell.DataFolder);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        switch (Choice)
        {
            case StartChoice.ImportFromSda:
                shell.ShowImport(ImportSource.SdaFolder);
                break;
            case StartChoice.ImportMaFiles:
                shell.ShowImport(ImportSource.MaFiles);
                break;
            case StartChoice.Link:
                shell.ShowLink();
                break;
            default:
                shell.ShowMain();
                break;
        }
    }
}
