using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.App.Services;

/// <summary>
/// Turns the monitor's events into Windows notifications: new confirmations, and what automation accepted or
/// couldn't. Only with notifications on in Settings and on the account. Clicking one opens the account.
/// </summary>
public sealed class ConfirmationNotifier
{
    private static readonly CompositeFormat NewOne = CompositeFormat.Parse(Strings.NotifyNewOneFormat);
    private static readonly CompositeFormat NewMany = CompositeFormat.Parse(Strings.NotifyNewManyFormat);
    private static readonly CompositeFormat AutoOne = CompositeFormat.Parse(Strings.NotifyAutoOneFormat);
    private static readonly CompositeFormat AutoMany = CompositeFormat.Parse(Strings.NotifyAutoManyFormat);
    private static readonly CompositeFormat FailedOne = CompositeFormat.Parse(Strings.NotifyAutoFailedOneFormat);
    private static readonly CompositeFormat FailedMany = CompositeFormat.Parse(Strings.NotifyAutoFailedManyFormat);

    private readonly ConfirmationMonitor _monitor;
    private readonly Func<bool> _enabled;
    private readonly Func<ITrayIcon?> _tray;
    private readonly Action<ulong> _open;

    public ConfirmationNotifier(ConfirmationMonitor monitor, Func<bool> enabled, Func<ITrayIcon?> tray, Action<ulong> open)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _enabled = enabled;
        _tray = tray;
        _open = open;
        monitor.NewConfirmations += (_, e) => Show(e, NewText(e.Confirmations));
        monitor.AutoAccepted += (_, e) => Show(e, AutoText(e));
    }

    private void Show(ConfirmationEvent e, string text)
    {
        if (_enabled() && _monitor.AutomationFor(e.SteamId).Notify && _tray() is { } tray)
        {
            tray.Notify(e.AccountName, text, () => _open(e.SteamId));
        }
    }

    internal static string NewText(IReadOnlyList<MobileConfirmation> confirmations)
        => confirmations is [var only]
            ? string.Format(CultureInfo.CurrentCulture, NewOne, TypeLabel(only), Name(only))
            : string.Format(CultureInfo.CurrentCulture, NewMany, confirmations.Count);

    private static string AutoText(ConfirmationEvent e) => (e.Ok, e.Confirmations) switch
    {
        (true, [var only]) => string.Format(CultureInfo.CurrentCulture, AutoOne, Name(only)),
        (true, var all) => string.Format(CultureInfo.CurrentCulture, AutoMany, all.Count),
        (false, [var only]) => string.Format(CultureInfo.CurrentCulture, FailedOne, Name(only)),
        (false, var all) => string.Format(CultureInfo.CurrentCulture, FailedMany, all.Count),
    };

    private static string Name(MobileConfirmation c) => c.Headline ?? c.TypeName ?? Strings.Confirmation;

    private static string TypeLabel(MobileConfirmation c) => c.Kind switch
    {
        ConfirmationKind.Trade => Strings.Trade,
        ConfirmationKind.Market => c.IsMarketPurchase ? Strings.Purchase : Strings.Market,
        _ => Strings.Account,
    };
}
