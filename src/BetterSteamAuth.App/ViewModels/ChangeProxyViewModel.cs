using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public sealed record ProxyOption(long? ProxyId, string Label, string? Detail, bool IsDead)
{
    public bool IsProxy => ProxyId is not null;
}

/// <summary>Picks the proxy (or direct connection) for one account.</summary>
public sealed partial class ChangeProxyViewModel : ObservableObject
{
    private static readonly CompositeFormat TitleFormat = CompositeFormat.Parse(Strings.ProxyForFormat);
    private static readonly CompositeFormat UsedByFormat = CompositeFormat.Parse(Strings.UsedByFormat);
    private static readonly CompositeFormat LatencyFormat = CompositeFormat.Parse(Strings.LatencyFormat);

    private readonly ShellViewModel _shell;
    private readonly ulong _steamId;

    public ChangeProxyViewModel(ShellViewModel shell, ulong steamId, string accountName)
    {
        _shell = shell;
        _steamId = steamId;
        Title = string.Format(CultureInfo.CurrentCulture, TitleFormat, accountName);

        var vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        long? current = vault.GetAccountProxyId(steamId);
        Options.Add(new ProxyOption(null, Strings.DirectConnection, null, false));
        foreach (var proxy in vault.ListProxies())
        {
            string? detail = proxy.Status == ProxyStatus.Dead ? Strings.NotResponding
                : proxy.AccountIds.Count(id => id != steamId) is > 0 and var others ? string.Format(CultureInfo.CurrentCulture, UsedByFormat, others)
                : proxy.LatencyMs is { } ms ? string.Format(CultureInfo.CurrentCulture, LatencyFormat, ms)
                : null;
            Options.Add(new ProxyOption(proxy.Id, proxy.Address, detail, proxy.Status == ProxyStatus.Dead));
        }

        Selected = Options.First(o => o.ProxyId == current);
    }

    public string Title { get; }

    public List<ProxyOption> Options { get; } = [];

    [ObservableProperty]
    public partial ProxyOption Selected { get; set; }

    [RelayCommand]
    private void Save()
    {
        _shell.Vault?.SetAccountProxy(_steamId, Selected.ProxyId);
        _shell.ResetConnection(_steamId);
        _shell.ShowMain();
    }

    [RelayCommand]
    private void Cancel() => _shell.ShowMain();
}
