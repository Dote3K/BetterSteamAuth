using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.App.Services;
using BetterSteamAuth.Steam.Network;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Settings page: security, confirmations, Advanced → Network.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly CompositeFormat MinutesFormat = CompositeFormat.Parse(Strings.MinutesFormat);
    private static readonly int[] AutoLockMinutes = [5, 15, 30, 60];

    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        var current = shell.NetworkOptions;
        SniBypass = SniBypassChoices.Single(c => c.Value == current.SniBypass);
        EncryptedDns = EncryptedDnsChoices.Single(c => c.Value == current.EncryptedDns);

        AutoLockChoices = [new(0, Strings.Never), .. AutoLockMinutes.Select(m => new Choice<int>(m, string.Format(CultureInfo.CurrentCulture, MinutesFormat, m)))];
        int minutes = shell.AutoLockMinutes;
        AutoLock = AutoLockChoices.FirstOrDefault(c => c.Value == minutes) ?? AutoLockChoices.Single(c => c.Value == ShellViewModel.DefaultAutoLockMinutes);
    }

    /// <summary>Security settings only apply when the data has a password.</summary>
    public bool HasPassword => _shell.Vault?.IsEncrypted == true;

    public bool CanRemember => HasPassword && RememberedKey.IsSupported;

    public bool RememberOnDevice
    {
        get => _shell.IsRemembered;
        set
        {
            if (value != _shell.IsRemembered)
            {
                _shell.SetRemembered(value);
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<Choice<int>> AutoLockChoices { get; }

    [ObservableProperty]
    public partial Choice<int> AutoLock { get; set; }

    partial void OnAutoLockChanged(Choice<int> value)
    {
        if (value is not null && value.Value != _shell.AutoLockMinutes)
        {
            _shell.AutoLockMinutes = value.Value;
        }
    }

    public IReadOnlyList<Choice<SniBypass>> SniBypassChoices { get; } =
    [
        new(Steam.Network.SniBypass.Auto, Strings.Auto),
        new(Steam.Network.SniBypass.On, Strings.On),
        new(Steam.Network.SniBypass.Off, Strings.Off),
    ];

    public IReadOnlyList<Choice<EncryptedDns>> EncryptedDnsChoices { get; } =
    [
        new(Steam.Network.EncryptedDns.Off, Strings.Off),
        new(Steam.Network.EncryptedDns.Cloudflare, "Cloudflare"),
        new(Steam.Network.EncryptedDns.Google, "Google"),
    ];

    [ObservableProperty]
    public partial Choice<SniBypass> SniBypass { get; set; }

    [ObservableProperty]
    public partial Choice<EncryptedDns> EncryptedDns { get; set; }

    partial void OnSniBypassChanged(Choice<SniBypass> value) => Apply();

    partial void OnEncryptedDnsChanged(Choice<EncryptedDns> value) => Apply();

    public bool AcceptTradesInBulk
    {
        get => _shell.ConfirmationPolicy.AcceptTradesInBulk;
        set
        {
            if (value != _shell.ConfirmationPolicy.AcceptTradesInBulk)
            {
                _shell.SetAcceptTradesInBulk(value);
                OnPropertyChanged();
            }
        }
    }

    [RelayCommand]
    private void Back() => _shell.ShowMain();

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
