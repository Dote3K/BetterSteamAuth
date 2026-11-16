using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BetterSteamAuth.App.ViewModels;

public sealed partial class TradeItemViewModel(TradeItem item) : ObservableObject
{
    public TradeItem Item { get; } = item;

    public string Name => Item.Amount > 1 ? $"{Item.Name} ×{Item.Amount}" : Item.Name;

    public string? Type => Item.Type;

    [ObservableProperty]
    public partial Bitmap? Image { get; set; }
}

/// <summary>
/// What to look at before deciding, like the Steam mobile app's confirmation screen.
/// Everything loads through the account's own connection.
/// </summary>
public sealed partial class ConfirmationDetailViewModel : ObservableObject, IDisposable
{
    private const int NewAccountDays = 30;

    private static readonly CompositeFormat MemberSinceFormat = CompositeFormat.Parse(Strings.MemberSinceFormat);
    private static readonly CompositeFormat NewAccountFormat = CompositeFormat.Parse(Strings.FlagNewAccountFormat);
    private static readonly CompositeFormat GiveFormat = CompositeFormat.Parse(Strings.YouGiveFormat);
    private static readonly CompositeFormat ReceiveFormat = CompositeFormat.Parse(Strings.YouReceiveFormat);

    private readonly ShellViewModel _shell;
    private readonly AccountItemViewModel _account;
    private readonly Action _close;
    private readonly CancellationTokenSource _cts = new();
    private ulong _partnerId;

    public ConfirmationDetailViewModel(ShellViewModel shell, AccountItemViewModel account, ConfirmationItemViewModel item, Action close)
    {
        _shell = shell;
        _account = account;
        _close = close;
        Item = item;

        Title = item.IsTrade ? Strings.TradeOffer : item.IsMarket ? Strings.MarketListing : item.Headline;
        Subtitle = item.MetaWithAccount;
        SummaryLines = item.Confirmation.Summary.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        (DangerTitle, DangerBody) = item.Confirmation.Type switch
        {
            9 => (Strings.DangerApiKeyTitle, Strings.DangerApiKeyBody),
            5 => (Strings.DangerPhoneTitle, Strings.DangerPhoneBody),
            6 => (Strings.DangerRecoveryTitle, Strings.DangerRecoveryBody),
            11 => (Strings.DangerFamilyTitle, Strings.DangerFamilyBody),
            _ => (Strings.DangerGenericTitle, Strings.DangerGenericBody),
        };

        account.Confirmations.CollectionChanged += OnConfirmationsChanged;
        _ = LoadAsync();
    }

    public ConfirmationItemViewModel Item { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string AcceptLabel => Item.IsTrade ? Strings.AcceptTrade : Strings.Accept;

    public IReadOnlyList<string> SummaryLines { get; }

    public string DangerTitle { get; }

    public string DangerBody { get; }

    // Market
    [ObservableProperty] public partial Bitmap? MarketImage { get; private set; }

    // Trade
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool LoadFailed { get; private set; }
    [ObservableProperty] public partial bool HasDetails { get; private set; }
    [ObservableProperty] public partial string? PartnerName { get; private set; }
    [ObservableProperty] public partial Bitmap? PartnerAvatar { get; private set; }
    [ObservableProperty] public partial string? PartnerSince { get; private set; }
    [ObservableProperty] public partial string GiveHeader { get; private set; } = "";
    [ObservableProperty] public partial string ReceiveHeader { get; private set; } = "";
    [ObservableProperty] public partial bool ReceivesNothing { get; private set; }

    public ObservableCollection<string> PartnerFlags { get; } = [];

    public ObservableCollection<TradeItemViewModel> Give { get; } = [];

    public ObservableCollection<TradeItemViewModel> Receive { get; } = [];

    [RelayCommand]
    private void Back() => _close();

    [RelayCommand]
    private void Accept() => Item.AcceptReviewed();

    [RelayCommand]
    private void Reject() => Item.RejectCommand.Execute(null);

    [RelayCommand]
    private void Undo() => Item.UndoCommand.Execute(null);

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private Task OpenProfileAsync()
        => _partnerId == 0
            ? Task.CompletedTask
            : _shell.Platform.OpenUriAsync(new Uri(string.Create(CultureInfo.InvariantCulture, $"https://steamcommunity.com/profiles/{_partnerId}")));

    public void Dispose()
    {
        _account.Confirmations.CollectionChanged -= OnConfirmationsChanged;
        _cts.Cancel();
        _cts.Dispose();
    }

    private void OnConfirmationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Sent or gone from Steam: nothing left to review.
        if (!_account.Confirmations.Contains(Item))
        {
            _close();
        }
    }

    private async Task LoadAsync()
    {
        var token = _cts.Token;
        var connection = _shell.ConnectionFor(_account.SteamId);
        if (Item.IsMarket || Item.IsDangerous)
        {
            MarketImage = await LoadImageAsync(connection, Item.Confirmation.Icon, token).ConfigureAwait(true);
            return;
        }

        IsLoading = true;
        LoadFailed = false;
        try
        {
            var (offer, profile) = await FetchTradeAsync(connection, token).ConfigureAwait(true);
            _partnerId = offer.PartnerSteamId;
            ShowTrade(offer, profile);
            HasDetails = true;

            PartnerAvatar = await LoadImageAsync(connection, profile.AvatarUrl, token).ConfigureAwait(true);
            foreach (var tile in Give.Concat(Receive))
            {
                tile.Image = await LoadImageAsync(connection, tile.Item.ImageUrl, token).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SessionExpiredException
                                       or System.Text.Json.JsonException or System.Xml.XmlException or KeyNotFoundException
                                   && !token.IsCancellationRequested)
        {
            LoadFailed = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<(TradeOfferDetails Offer, PartnerProfile Profile)> FetchTradeAsync(AccountConnection connection, CancellationToken token)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("BSA_SAMPLE_CONFIRMATIONS") == "1")
        {
            await Task.Delay(400, token).ConfigureAwait(true);
            return SampleConfirmations.TradeDetails(Item.Confirmation);
        }
#endif

        var vault = _shell.Vault ?? throw new SessionExpiredException();
        var maFile = vault.Load(_account.SteamId);
        if (await WebSession.EnsureAsync(connection, maFile.Session, token).ConfigureAwait(true))
        {
            vault.Save(maFile);
        }

        var offer = await TradeOffers.GetAsync(connection, maFile.Session!.AccessToken!, Item.Confirmation.CreatorId, token).ConfigureAwait(true);
        var profile = await TradeOffers.GetProfileAsync(connection, offer.PartnerSteamId, token).ConfigureAwait(true);
        return (offer, profile);
    }

    private void ShowTrade(TradeOfferDetails offer, PartnerProfile profile)
    {
        PartnerName = profile.Name;
        PartnerSince = profile.MemberSince is { } since
            ? string.Format(CultureInfo.CurrentCulture, MemberSinceFormat, since.ToString("MMM d, yyyy", CultureInfo.CurrentCulture))
            : null;

        PartnerFlags.Clear();
        if (profile.MemberSince is { } created
            && (DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - created.DayNumber) is var days and < NewAccountDays)
        {
            PartnerFlags.Add(string.Format(CultureInfo.CurrentCulture, NewAccountFormat, Math.Max(days, 0)));
        }

        if (profile.IsPrivate)
        {
            PartnerFlags.Add(Strings.FlagPrivate);
        }

        if (profile.VacBanned)
        {
            PartnerFlags.Add(Strings.FlagVac);
        }

        if (profile.TradeBanned)
        {
            PartnerFlags.Add(Strings.FlagTradeBan);
        }

        if (profile.IsLimited)
        {
            PartnerFlags.Add(Strings.FlagLimited);
        }

        Give.Clear();
        Receive.Clear();
        foreach (var item in offer.ItemsToGive)
        {
            Give.Add(new TradeItemViewModel(item));
        }

        foreach (var item in offer.ItemsToReceive)
        {
            Receive.Add(new TradeItemViewModel(item));
        }

        GiveHeader = Give.Count switch
        {
            0 => Strings.YouGiveNothing,
            1 => Strings.YouGiveOne,
            _ => string.Format(CultureInfo.CurrentCulture, GiveFormat, Give.Count),
        };
        ReceiveHeader = Receive.Count switch
        {
            0 => Strings.YouReceiveNothing,
            1 => Strings.YouReceiveOne,
            _ => string.Format(CultureInfo.CurrentCulture, ReceiveFormat, Receive.Count),
        };
        ReceivesNothing = Receive.Count == 0 && Give.Count > 0;
    }

    private static async Task<Bitmap?> LoadImageAsync(AccountConnection connection, string? url, CancellationToken token)
    {
        byte[]? bytes = await TradeOffers.GetImageAsync(connection, url, token).ConfigureAwait(true);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
