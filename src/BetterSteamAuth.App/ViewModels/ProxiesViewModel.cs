using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public sealed partial class ProxyItemViewModel(ProxyEntry entry, string accountsText) : ObservableObject
{
    private static readonly CompositeFormat LatencyFormat = CompositeFormat.Parse(Strings.LatencyFormat);

    public ProxyEntry Entry { get; } = entry;

    public long Id => Entry.Id;

    public string Address => Entry.Address;

    public string AccountsText { get; } = accountsText;

    public bool IsAssigned => Entry.AccountIds.Count > 0;

    public bool IsDead => Entry.Status == ProxyStatus.Dead;

    public string? HealthText => IsDead ? Strings.NotResponding
        : Entry.LatencyMs is { } ms ? string.Format(CultureInfo.CurrentCulture, LatencyFormat, ms)
        : null;

    public string? Error => Entry.LastError;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsChecking { get; set; }
}

public sealed record ProxyPreviewLine(string Text, bool IsValid, bool IsDuplicate)
{
    public bool IsError => !IsValid && !IsDuplicate;
}

/// <summary>Proxy pool: list, add by pasting, auto-assign, check, remove.</summary>
public sealed partial class ProxiesViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat ProxyCountFormat = CompositeFormat.Parse(Strings.ProxyCountFormat);
    private static readonly CompositeFormat DeadFormat = CompositeFormat.Parse(Strings.ProxyDeadFormat);
    private static readonly CompositeFormat DirectFormat = CompositeFormat.Parse(Strings.DirectAccountsFormat);
    private static readonly CompositeFormat SelectedFormat = CompositeFormat.Parse(Strings.SelectedFormat);
    private static readonly CompositeFormat AddCountFormat = CompositeFormat.Parse(Strings.AddCountFormat);
    private static readonly CompositeFormat UnreadableFormat = CompositeFormat.Parse(Strings.LineUnreadableFormat);
    private static readonly CompositeFormat DuplicateFormat = CompositeFormat.Parse(Strings.LineDuplicateFormat);
    private static readonly CompositeFormat AutoTitleFormat = CompositeFormat.Parse(Strings.AutoAssignTitleFormat);
    private static readonly CompositeFormat AutoShortFormat = CompositeFormat.Parse(Strings.AutoAssignShortFormat);
    private static readonly CompositeFormat AssignCountFormat = CompositeFormat.Parse(Strings.AssignCountFormat);
    private static readonly CompositeFormat RemoveInUseFormat = CompositeFormat.Parse(Strings.RemoveInUseFormat);

    private readonly ShellViewModel _shell;
    private readonly Vault _vault;
    private readonly Dictionary<ulong, string> _names = [];
    private List<ProxySettings> _parsed = [];
    private List<long> _removeIds = [];

    public ProxiesViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _vault = shell.Vault ?? throw new InvalidOperationException("No vault is open.");
        foreach (var account in _vault.ListAccounts())
        {
            _names[account.SteamId] = account.AccountName
                ?? _vault.Load(account.SteamId).AccountName
                ?? account.SteamId.ToString(CultureInfo.InvariantCulture);
        }

        _shell.ProxyHealthChanged += OnHealthChanged;
        Reload();
    }

    public ObservableCollection<ProxyItemViewModel> Items { get; } = [];

    public ObservableCollection<ProxyPreviewLine> Preview { get; } = [];

    [ObservableProperty] public partial string SummaryText { get; private set; } = "";
    [ObservableProperty] public partial string? DeadText { get; private set; }
    [ObservableProperty] public partial string? DirectText { get; private set; }
    [ObservableProperty] public partial bool HasItems { get; private set; }
    [ObservableProperty] public partial string? SelectionText { get; private set; }
    [ObservableProperty] public partial bool IsCheckingAll { get; private set; }

    [ObservableProperty] public partial bool IsAdding { get; private set; }
    [ObservableProperty] public partial string AddText { get; set; } = "";
    [ObservableProperty] public partial string AddButtonText { get; private set; } = "";
    [ObservableProperty] public partial bool CanAdd { get; private set; }

    [ObservableProperty] public partial bool IsAutoAssigning { get; private set; }
    [ObservableProperty] public partial string AutoAssignTitle { get; private set; } = "";
    [ObservableProperty] public partial string AutoAssignBody { get; private set; } = "";
    [ObservableProperty] public partial string AutoAssignButtonText { get; private set; } = "";
    [ObservableProperty] public partial bool CanAutoAssign { get; private set; }

    [ObservableProperty] public partial bool IsConfirmingRemove { get; private set; }
    [ObservableProperty] public partial string RemoveText { get; private set; } = "";

    public bool HasDead => DeadText is not null;

    public bool ShowEmpty => !HasItems && !IsAdding;

    partial void OnHasItemsChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    partial void OnIsAddingChanged(bool value) => OnPropertyChanged(nameof(ShowEmpty));

    public bool HasDirect => DirectText is not null;

    public bool HasSelection => SelectionText is not null;

    partial void OnDeadTextChanged(string? value) => OnPropertyChanged(nameof(HasDead));

    partial void OnDirectTextChanged(string? value) => OnPropertyChanged(nameof(HasDirect));

    partial void OnSelectionTextChanged(string? value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnAddTextChanged(string value) => ParsePreview();

    public void Dispose() => _shell.ProxyHealthChanged -= OnHealthChanged;

    [RelayCommand]
    private void Back() => _shell.ShowMain();

    [RelayCommand]
    private async Task CheckAllAsync()
    {
        IsCheckingAll = true;
        foreach (var item in Items)
        {
            item.IsChecking = true;
        }

        await _shell.CheckProxiesAsync().ConfigureAwait(true);
        IsCheckingAll = false;
    }

    [RelayCommand]
    private Task CheckAsync(ProxyItemViewModel item) => CheckItemsAsync([item]);

    [RelayCommand]
    private Task CheckSelectedAsync() => CheckItemsAsync(Items.Where(i => i.IsSelected).ToList());

    [RelayCommand]
    private void ShowAdd()
    {
        IsAutoAssigning = false;
        IsConfirmingRemove = false;
        AddText = "";
        ParsePreview();
        IsAdding = true;
    }

    [RelayCommand]
    private void CancelAdd() => IsAdding = false;

    [RelayCommand]
    private void Add()
    {
        _vault.AddProxies(_parsed);
        IsAdding = false;
        Reload();
    }

    [RelayCommand]
    private void ShowAutoAssign()
    {
        IsAdding = false;
        IsConfirmingRemove = false;
        var (direct, free) = AutoAssignPlan();
        int assign = Math.Min(direct.Count, free.Count);
        AutoAssignTitle = string.Format(CultureInfo.CurrentCulture, AutoTitleFormat, direct.Count);
        AutoAssignBody = free.Count == 0 ? Strings.AutoAssignNone
            : free.Count >= direct.Count ? Strings.AutoAssignEnough
            : string.Format(CultureInfo.CurrentCulture, AutoShortFormat, free.Count, direct.Count - free.Count);
        AutoAssignButtonText = string.Format(CultureInfo.CurrentCulture, AssignCountFormat, assign);
        CanAutoAssign = assign > 0;
        IsAutoAssigning = true;
    }

    [RelayCommand]
    private void CancelAutoAssign() => IsAutoAssigning = false;

    [RelayCommand]
    private void AutoAssign()
    {
        var (direct, free) = AutoAssignPlan();
        foreach (var (steamId, proxyId) in direct.Zip(free))
        {
            _vault.SetAccountProxy(steamId, proxyId);
            _shell.ResetConnection(steamId);
        }

        IsAutoAssigning = false;
        Reload();
    }

    [RelayCommand]
    private void Remove(ProxyItemViewModel item) => RequestRemove([item]);

    [RelayCommand]
    private void RemoveSelected() => RequestRemove(Items.Where(i => i.IsSelected).ToList());

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private void ConfirmRemove()
    {
        foreach (long id in _removeIds)
        {
            var accounts = Items.First(i => i.Id == id).Entry.AccountIds;
            _vault.RemoveProxy(id, switchAccountsToDirect: true);
            foreach (ulong steamId in accounts)
            {
                _shell.ResetConnection(steamId);
            }
        }

        IsConfirmingRemove = false;
        Reload();
    }

    private void RequestRemove(List<ProxyItemViewModel> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        _removeIds = items.Select(i => i.Id).ToList();
        int inUse = items.Sum(i => i.Entry.AccountIds.Count);
        if (inUse == 0)
        {
            ConfirmRemove();
            return;
        }

        IsAdding = false;
        IsAutoAssigning = false;
        RemoveText = string.Format(CultureInfo.CurrentCulture, RemoveInUseFormat, inUse);
        IsConfirmingRemove = true;
    }

    private async Task CheckItemsAsync(List<ProxyItemViewModel> items)
    {
        foreach (var item in items)
        {
            item.IsChecking = true;
        }

        await _shell.CheckProxiesAsync(items.Select(i => i.Id)).ConfigureAwait(true);
    }

    private (List<ulong> Direct, List<long> Free) AutoAssignPlan()
    {
        var direct = _vault.ListAccounts().Where(a => a.ProxyId is null).Select(a => a.SteamId).ToList();
        var free = _vault.ListProxies().Where(p => p.AccountIds.Count == 0 && p.Status != ProxyStatus.Dead).Select(p => p.Id).ToList();
        return (direct, free);
    }

    private void OnHealthChanged(object? sender, EventArgs e) => Reload();

    private void Reload()
    {
        var proxies = _vault.ListProxies();
        Items.Clear();
        foreach (var proxy in proxies)
        {
            string accounts = proxy.AccountIds.Count == 0
                ? Strings.NotAssigned
                : string.Join(", ", proxy.AccountIds.Select(id => _names.GetValueOrDefault(id, id.ToString(CultureInfo.InvariantCulture))));
            var item = new ProxyItemViewModel(proxy, accounts);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProxyItemViewModel.IsSelected))
                {
                    UpdateSelection();
                }
            };
            Items.Add(item);
        }

        int dead = proxies.Count(p => p.Status == ProxyStatus.Dead);
        int direct = _vault.ListAccounts().Count(a => a.ProxyId is null);
        HasItems = proxies.Count > 0;
        SummaryText = string.Format(CultureInfo.CurrentCulture, ProxyCountFormat, proxies.Count);
        DeadText = dead > 0 ? string.Format(CultureInfo.CurrentCulture, DeadFormat, dead) : null;
        DirectText = direct > 0 ? string.Format(CultureInfo.CurrentCulture, DirectFormat, direct) : null;
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        int count = Items.Count(i => i.IsSelected);
        SelectionText = count > 0 ? string.Format(CultureInfo.CurrentCulture, SelectedFormat, count) : null;
    }

    private void ParsePreview()
    {
        var existing = _vault.ListProxies().Select(p => (p.Scheme, p.Host, p.Port, p.Username)).ToHashSet();
        _parsed = [];
        Preview.Clear();
        string[] lines = (AddText ?? "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            int lineNumber = i + 1;
            if (!ProxyParser.TryParse(lines[i], out var proxy) || proxy is null)
            {
                Preview.Add(new ProxyPreviewLine(string.Format(CultureInfo.CurrentCulture, UnreadableFormat, lineNumber), false, false));
            }
            else if (existing.Contains((proxy.Scheme, proxy.Host, proxy.Port, proxy.Username))
                     || _parsed.Any(p => p.Scheme == proxy.Scheme && p.Host == proxy.Host && p.Port == proxy.Port && p.Username == proxy.Username))
            {
                Preview.Add(new ProxyPreviewLine(string.Format(CultureInfo.CurrentCulture, DuplicateFormat, lineNumber), false, true));
            }
            else
            {
                _parsed.Add(proxy);
                Preview.Add(new ProxyPreviewLine(proxy + (proxy.Username is null ? "" : " · " + Strings.WithLogin), true, false));
            }
        }

        AddButtonText = string.Format(CultureInfo.CurrentCulture, AddCountFormat, _parsed.Count);
        CanAdd = _parsed.Count > 0;
    }
}
