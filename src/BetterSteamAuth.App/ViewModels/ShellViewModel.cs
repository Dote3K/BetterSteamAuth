using BetterSteamAuth.App.Services;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Owns the vault and decides which page fills the window: first run, unlock, import or main.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    public ShellViewModel(string dataFolder, IPlatformServices platform)
    {
        DataFolder = dataFolder;
        Platform = platform;

        if (!Vault.Exists(dataFolder))
        {
            CurrentPage = new FirstRunViewModel(this);
            return;
        }

        Vault = Vault.Open(dataFolder);
        if (Vault.IsUnlocked)
        {
            ShowMain();
        }
        else
        {
            ShowUnlock();
        }
    }

    public string DataFolder { get; }

    public IPlatformServices Platform { get; }

    public Vault? Vault { get; private set; }

    [ObservableProperty]
    public partial ObservableObject? CurrentPage { get; private set; }

    public void UseVault(Vault vault) => Vault = vault;

    public void ShowMain() => SwitchTo(new MainViewModel(this));

    public void ShowUnlock() => SwitchTo(new UnlockViewModel(this));

    public void ShowImport(ImportSource source) => SwitchTo(new ImportViewModel(this, source, onDone: ShowMain));

    public void LockApp()
    {
        if (Vault is not { IsEncrypted: true })
        {
            return;
        }

        Vault.Lock();
        ShowUnlock();
    }

    public void Dispose()
    {
        (CurrentPage as IDisposable)?.Dispose();
        Vault?.Dispose();
    }

    private void SwitchTo(ObservableObject page)
    {
        (CurrentPage as IDisposable)?.Dispose();
        CurrentPage = page;
    }
}
