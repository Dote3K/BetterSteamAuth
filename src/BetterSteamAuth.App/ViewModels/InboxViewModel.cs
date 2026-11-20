using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using BetterSteamAuth.App.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public enum InboxFilter
{
    All,
    Trades,
    Market,
    Account,
}

public sealed partial class InboxEntryViewModel(AccountItemViewModel account, ConfirmationItemViewModel item) : ObservableObject
{
    public AccountItemViewModel Account { get; } = account;

    public ConfirmationItemViewModel Item { get; } = item;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Every account's pending confirmations in one list, with filters, multi-select and one shared
/// undo window per batch.
/// </summary>
public sealed partial class InboxViewModel : ObservableObject, IDisposable
{
    private static readonly CompositeFormat AllFormat = CompositeFormat.Parse(Strings.FilterAllFormat);
    private static readonly CompositeFormat TradesFormat = CompositeFormat.Parse(Strings.FilterTradesFormat);
    private static readonly CompositeFormat MarketFormat = CompositeFormat.Parse(Strings.FilterMarketFormat);
    private static readonly CompositeFormat AccountFormat = CompositeFormat.Parse(Strings.FilterAccountFormat);
    private static readonly CompositeFormat SelectedFormat = CompositeFormat.Parse(Strings.SelectedFormat);
    private static readonly CompositeFormat AcceptFormat = CompositeFormat.Parse(Strings.AcceptCountFormat);
    private static readonly CompositeFormat RejectFormat = CompositeFormat.Parse(Strings.RejectCountFormat);
    private static readonly CompositeFormat SkippedFormat = CompositeFormat.Parse(Strings.NeedReviewFormat);
    private static readonly CompositeFormat FailedFormat = CompositeFormat.Parse(Strings.AccountsFailedFormat);
    private static readonly CompositeFormat AcceptingFormat = CompositeFormat.Parse(Strings.AcceptingBatchFormat);
    private static readonly CompositeFormat RejectingFormat = CompositeFormat.Parse(Strings.RejectingBatchFormat);

    private readonly IReadOnlyList<AccountItemViewModel> _accounts;
    private readonly Func<AccountItemViewModel, IReadOnlyList<ConfirmationItemViewModel>, bool, Task> _sendBatch;
    private readonly Func<Task> _refreshAll;
    private readonly Action _close;
    private readonly ConfirmationPolicy _policy;
    private DispatcherTimer? _batchTimer;
    private List<InboxEntryViewModel> _batch = [];
    private bool _batchAccept;
    private int _batchSecondsLeft;
    private bool _updatingSelectAll;

    public InboxViewModel(
        IReadOnlyList<AccountItemViewModel> accounts,
        Func<AccountItemViewModel, IReadOnlyList<ConfirmationItemViewModel>, bool, Task> sendBatch,
        Func<Task> refreshAll,
        Action close,
        ConfirmationPolicy policy)
    {
        _accounts = accounts;
        _policy = policy;
        _sendBatch = sendBatch;
        _refreshAll = refreshAll;
        _close = close;
        foreach (var account in accounts)
        {
            account.Confirmations.CollectionChanged += OnConfirmationsChanged;
            account.PropertyChanged += OnAccountChanged;
        }

        Rebuild();
    }

    public ObservableCollection<InboxEntryViewModel> Entries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsTrades), nameof(IsMarket), nameof(IsAccount))]
    public partial InboxFilter Filter { get; set; }

    public bool IsAll { get => Filter == InboxFilter.All; set { if (value) Filter = InboxFilter.All; } }

    public bool IsTrades { get => Filter == InboxFilter.Trades; set { if (value) Filter = InboxFilter.Trades; } }

    public bool IsMarket { get => Filter == InboxFilter.Market; set { if (value) Filter = InboxFilter.Market; } }

    public bool IsAccount { get => Filter == InboxFilter.Account; set { if (value) Filter = InboxFilter.Account; } }

    [ObservableProperty] public partial int TotalCount { get; private set; }
    [ObservableProperty] public partial string AllLabel { get; private set; } = "";
    [ObservableProperty] public partial string TradesLabel { get; private set; } = "";
    [ObservableProperty] public partial string MarketLabel { get; private set; } = "";
    [ObservableProperty] public partial string AccountLabel { get; private set; } = "";
    [ObservableProperty] public partial bool HasTrades { get; private set; }
    [ObservableProperty] public partial bool HasMarket { get; private set; }
    [ObservableProperty] public partial bool HasAccountItems { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    /// <summary>Checkboxes and Select all only make sense with two or more items.</summary>
    [ObservableProperty] public partial bool CanMultiSelect { get; private set; }

    [ObservableProperty] public partial string? FailedText { get; private set; }
    [ObservableProperty] public partial string? FailedNames { get; private set; }

    public bool HasFailed => FailedText is not null;

    partial void OnFailedTextChanged(string? value) => OnPropertyChanged(nameof(HasFailed));

    [ObservableProperty] public partial bool SelectAll { get; set; }
    [ObservableProperty] public partial bool HasSelection { get; private set; }
    [ObservableProperty] public partial string SelectionText { get; private set; } = "";
    [ObservableProperty] public partial string? SkipText { get; private set; }
    [ObservableProperty] public partial string AcceptLabel { get; private set; } = "";
    [ObservableProperty] public partial string RejectLabel { get; private set; } = "";
    [ObservableProperty] public partial bool CanAcceptSelection { get; private set; }

    [ObservableProperty] public partial bool IsBatchPending { get; private set; }
    [ObservableProperty] public partial string BatchText { get; private set; } = "";

    public bool ShowSelectionBar => HasSelection && !IsBatchPending;

    partial void OnHasSelectionChanged(bool value) => OnPropertyChanged(nameof(ShowSelectionBar));

    partial void OnIsBatchPendingChanged(bool value) => OnPropertyChanged(nameof(ShowSelectionBar));

    partial void OnFilterChanged(InboxFilter value)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = false;
        }

        Rebuild();
    }

    partial void OnSelectAllChanged(bool value)
    {
        if (_updatingSelectAll)
        {
            return;
        }

        foreach (var entry in Entries.Where(e => e.Item.IsIdle))
        {
            entry.IsSelected = value;
        }
    }

    public void Dispose()
    {
        foreach (var account in _accounts)
        {
            account.Confirmations.CollectionChanged -= OnConfirmationsChanged;
            account.PropertyChanged -= OnAccountChanged;
        }

        // Closing during a batch's undo window sends nothing.
        StopBatchTimer();
    }

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private Task RefreshAsync() => _refreshAll();

    [RelayCommand]
    private void AcceptSelected() => StartBatch(accept: true, Selected().Where(e => e.Item.CanBulkAccept).ToList());

    [RelayCommand]
    private void RejectSelected() => StartBatch(accept: false, Selected().ToList());

    [RelayCommand]
    private void UndoBatch()
    {
        StopBatchTimer();
        foreach (var entry in _batch)
        {
            entry.Item.LeaveBatch();
        }

        _batch = [];
        IsBatchPending = false;
    }

    private IEnumerable<InboxEntryViewModel> Selected() => Entries.Where(e => e.IsSelected && e.Item.IsIdle);

    private void StartBatch(bool accept, List<InboxEntryViewModel> entries)
    {
        if (entries.Count == 0 || IsBatchPending)
        {
            return;
        }

        _batch = entries;
        _batchAccept = accept;
        _batchSecondsLeft = ConfirmationItemViewModel.UndoSeconds;
        foreach (var entry in Entries)
        {
            entry.IsSelected = false;
        }

        foreach (var entry in entries)
        {
            entry.Item.JoinBatch(accept);
        }

        if (!_policy.UndoEnabled)
        {
            _ = SendBatchAsync();
            return;
        }

        IsBatchPending = true;
        UpdateBatchText();
        _batchTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, async (_, _) => await TickAsync().ConfigureAwait(true));
        _batchTimer.Start();
    }

    private async Task TickAsync()
    {
        _batchSecondsLeft--;
        if (_batchSecondsLeft > 0)
        {
            UpdateBatchText();
            return;
        }

        StopBatchTimer();
        IsBatchPending = false;
        await SendBatchAsync().ConfigureAwait(true);
    }

    private async Task SendBatchAsync()
    {
        // Rows undone one by one during the window are no longer in the batch.
        var groups = _batch.Where(e => e.Item.InBatch).GroupBy(e => e.Account).ToList();
        _batch = [];
        // Each account sends through its own connection, so groups go out in parallel, a little
        // apart so accounts don't hit Steam in lockstep.
        bool accept = _batchAccept;
        var sends = groups.Select(async group =>
        {
            var items = group.Select(e => e.Item).ToList();
            foreach (var item in items)
            {
                item.MarkSending();
            }

            await Task.Delay(Random.Shared.Next(0, 1500)).ConfigureAwait(true);
            await _sendBatch(group.Key, items, accept).ConfigureAwait(true);
        });
        await Task.WhenAll(sends).ConfigureAwait(true);
    }

    private void StopBatchTimer()
    {
        _batchTimer?.Stop();
        _batchTimer = null;
    }

    private void UpdateBatchText()
        => BatchText = string.Format(CultureInfo.CurrentCulture, _batchAccept ? AcceptingFormat : RejectingFormat, _batch.Count, _batchSecondsLeft);

    private void OnConfirmationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountItemViewModel.Problem))
        {
            UpdateFailed();
        }
    }

    private void Rebuild()
    {
        var selected = Entries.Where(e => e.IsSelected).Select(e => e.Item).ToHashSet();
        foreach (var entry in Entries)
        {
            entry.PropertyChanged -= OnEntryChanged;
        }

        Entries.Clear();
        var all = _accounts.SelectMany(a => a.Confirmations.Select(c => (Account: a, Item: c))).ToList();
        foreach (var (account, item) in all
                     .Where(x => Matches(x.Item))
                     .OrderByDescending(x => x.Item.Confirmation.CreationTime))
        {
            var entry = new InboxEntryViewModel(account, item) { IsSelected = selected.Contains(item) };
            entry.PropertyChanged += OnEntryChanged;
            Entries.Add(entry);
        }

        int trades = all.Count(x => x.Item.IsTrade);
        int market = all.Count(x => x.Item.IsMarket);
        int accountItems = all.Count(x => x.Item.IsDangerous);
        TotalCount = all.Count;
        AllLabel = string.Format(CultureInfo.CurrentCulture, AllFormat, all.Count);
        TradesLabel = string.Format(CultureInfo.CurrentCulture, TradesFormat, trades);
        MarketLabel = string.Format(CultureInfo.CurrentCulture, MarketFormat, market);
        AccountLabel = string.Format(CultureInfo.CurrentCulture, AccountFormat, accountItems);
        HasTrades = trades > 0;
        HasMarket = market > 0;
        HasAccountItems = accountItems > 0;
        IsEmpty = Entries.Count == 0;
        CanMultiSelect = Entries.Count > 1;
        UpdateSelection();
        UpdateFailed();
    }

    private bool Matches(ConfirmationItemViewModel item) => Filter switch
    {
        InboxFilter.Trades => item.IsTrade,
        InboxFilter.Market => item.IsMarket,
        InboxFilter.Account => item.IsDangerous,
        _ => true,
    };

    private void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InboxEntryViewModel.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        var selected = Selected().ToList();
        int acceptable = selected.Count(e => e.Item.CanBulkAccept);
        int skipped = selected.Count - acceptable;

        HasSelection = selected.Count > 0;
        SelectionText = string.Format(CultureInfo.CurrentCulture, SelectedFormat, selected.Count);
        AcceptLabel = string.Format(CultureInfo.CurrentCulture, AcceptFormat, acceptable);
        RejectLabel = string.Format(CultureInfo.CurrentCulture, RejectFormat, selected.Count);
        CanAcceptSelection = acceptable > 0;
        SkipText = skipped > 0 ? string.Format(CultureInfo.CurrentCulture, SkippedFormat, skipped) : null;

        _updatingSelectAll = true;
        SelectAll = Entries.Count > 0 && Entries.Where(e => e.Item.IsIdle).All(e => e.IsSelected) && selected.Count > 0;
        _updatingSelectAll = false;
    }

    private void UpdateFailed()
    {
        var failed = _accounts.Where(a => a.HasProblem).Select(a => a.Name).ToList();
        FailedText = failed.Count > 0 ? string.Format(CultureInfo.CurrentCulture, FailedFormat, failed.Count) : null;
        FailedNames = failed.Count > 0 ? string.Join(", ", failed) : null;
    }
}
