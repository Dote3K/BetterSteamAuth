using BetterSteamAuth.Steam.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// Settings reachable from the welcome page, before any account or vault exists: how the app reaches Steam. The choice
/// applies right away and is saved into the vault once setup creates it.
/// </summary>
public sealed partial class SetupSettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Action _back;

    public SetupSettingsViewModel(ShellViewModel shell, Action back)
    {
        _shell = shell;
        _back = back;
        var current = shell.NetworkOptions;
        SniBypass = SettingsViewModel.SniBypassChoices.Single(c => c.Value == current.SniBypass);
        EncryptedDns = SettingsViewModel.EncryptedDnsChoices.Single(c => c.Value == current.EncryptedDns);
    }

    [ObservableProperty]
    public partial Choice<SniBypass> SniBypass { get; set; }

    [ObservableProperty]
    public partial Choice<EncryptedDns> EncryptedDns { get; set; }

    partial void OnSniBypassChanged(Choice<SniBypass> value) => Apply();

    partial void OnEncryptedDnsChanged(Choice<EncryptedDns> value) => Apply();

    [RelayCommand]
    private void Back() => _back();

    private void Apply()
    {
        // Both are set in the constructor; ignore the first change notifications.
        if (SniBypass is null || EncryptedDns is null)
        {
            return;
        }

        var options = new NetworkOptions(SniBypass.Value, EncryptedDns.Value);
        if (options != _shell.NetworkOptions)
        {
            _shell.ApplyNetworkOptions(options);
        }
    }
}
