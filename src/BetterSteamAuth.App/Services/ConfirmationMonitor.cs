using System.Globalization;
using System.Security.Cryptography;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.App.ViewModels;
using BetterSteamAuth.Steam;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.App.Services;

/// <summary>The latest check of one account. <see cref="Problem"/> is set when the check failed.</summary>
public sealed record AccountCheck(
    ulong SteamId, IReadOnlyList<MobileConfirmation> Confirmations, string? Problem, bool ProblemNeedsLogin, DateTime CheckedUtc);

/// <summary>Confirmations of one account that just appeared, or that automation just sent.</summary>
public sealed record ConfirmationEvent(ulong SteamId, string AccountName, IReadOnlyList<MobileConfirmation> Confirmations, bool Ok);

/// <summary>
/// Checks every account for confirmations while the app is unlocked, whichever page is open: one loop per account on
/// its own jittered schedule and connection. Accepts what an account's automation allows and logs it, and tells
/// pages and notifications what changed. Runs on the UI thread's context, so events arrive on the UI thread.
/// </summary>
public sealed class ConfirmationMonitor(ShellViewModel shell) : IDisposable
{
    /// <summary>A confirmation automation couldn't send is tried this many times, then left for a person.</summary>
    private const int MaxAutoAttempts = 2;

    private readonly Dictionary<ulong, AccountCheck> _latest = [];
    private readonly Dictionary<ulong, HashSet<ulong>> _known = [];
    private readonly Dictionary<ulong, int> _autoAttempts = [];
    private readonly Dictionary<ulong, Task> _inFlight = [];
    private readonly Dictionary<ulong, CancellationTokenSource> _loops = [];
    private IReadOnlyDictionary<ulong, AccountAutomation> _automation = new Dictionary<ulong, AccountAutomation>();
    private TaskCompletionSource _changed = NewSignal();
    private CancellationTokenSource _run = new();
    private bool _onScreen;
#if DEBUG
    private readonly HashSet<(ulong SteamId, ulong Id)> _sampleGone = [];
#endif

    /// <summary>After every check of an account, successful or not.</summary>
    public event EventHandler<AccountCheck>? Checked;

    /// <summary>Confirmations not seen before. Those already there at the first check after unlock aren't reported.</summary>
    public event EventHandler<ConfirmationEvent>? NewConfirmations;

    /// <summary>Confirmations automation sent, with whether Steam took them.</summary>
    public event EventHandler<ConfirmationEvent>? AutoAccepted;

    /// <summary>True while confirmations are on screen (the main window, not minimized).</summary>
    public bool OnScreen
    {
        get => _onScreen;
        set
        {
            if (_onScreen != value)
            {
                _onScreen = value;
                Wake();
            }
        }
    }

    public AccountCheck? Latest(ulong steamId) => _latest.GetValueOrDefault(steamId);

    public AccountAutomation AutomationFor(ulong steamId) => _automation.GetValueOrDefault(steamId) ?? AccountAutomation.Default;

    /// <summary>Whether the account is checked even when its confirmations aren't on screen, which auto-accept needs.</summary>
    public bool ChecksInBackground(AccountAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(automation);
        return automation.Checks == ConfirmationChecks.Always
               || (automation.Checks == ConfirmationChecks.LikeSettings && shell.CheckInBackground);
    }

    /// <summary>Starts loops for accounts that have none, stops those of removed accounts, and rereads automation.</summary>
    public void Sync()
    {
        if (shell.Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

        _automation = vault.ListAutomation();
        foreach (ulong steamId in _automation.Keys.Where(id => !_loops.ContainsKey(id)))
        {
            var loop = CancellationTokenSource.CreateLinkedTokenSource(_run.Token);
            _loops[steamId] = loop;
            _ = RunAsync(steamId, loop.Token);
        }

        foreach (ulong steamId in _loops.Keys.Where(id => !_automation.ContainsKey(id)).ToList())
        {
            _loops[steamId].Cancel();
            _loops[steamId].Dispose();
            _loops.Remove(steamId);
            _latest.Remove(steamId);
            _known.Remove(steamId);
        }

        Wake();
    }

    /// <summary>Stops every loop and forgets every result (the app is locking).</summary>
    public void Stop()
    {
        _run.Cancel();
        _run.Dispose();
        _run = new CancellationTokenSource();
        foreach (var loop in _loops.Values)
        {
            loop.Dispose();
        }

        _loops.Clear();
        _latest.Clear();
        _known.Clear();
        _autoAttempts.Clear();
        _inFlight.Clear();
        Wake();
    }

    /// <summary>Something that decides whether accounts are checked has changed: loops look again.</summary>
    public void Wake()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    /// <summary>Checks one account now, whatever its schedule (Refresh). Joins a check already running.</summary>
    public Task CheckNowAsync(ulong steamId) => _loops.ContainsKey(steamId) ? CheckAsync(steamId, _run.Token) : Task.CompletedTask;

    public Task CheckAllNowAsync() => Task.WhenAll(_loops.Keys.ToList().Select(CheckNowAsync));

    /// <summary>Confirmations answered by hand leave the latest list, so a page opened before the next check doesn't show them.</summary>
    public void Forget(ulong steamId, IReadOnlyCollection<ulong> confirmationIds)
    {
        ArgumentNullException.ThrowIfNull(confirmationIds);
#if DEBUG
        if (SampleMode.IsOn)
        {
            _sampleGone.UnionWith(confirmationIds.Select(id => (steamId, id)));
        }
#endif
        if (_latest.TryGetValue(steamId, out var check))
        {
            _latest[steamId] = check with { Confirmations = [.. check.Confirmations.Where(c => !confirmationIds.Contains(c.Id))] };
        }
    }

    public void Dispose()
    {
        _run.Cancel();
        _run.Dispose();
        foreach (var loop in _loops.Values)
        {
            loop.Dispose();
        }
    }

    private bool ShouldCheck(ulong steamId)
    {
        var automation = AutomationFor(steamId);
        return automation.Checks != ConfirmationChecks.Never && (OnScreen || ChecksInBackground(automation));
    }

    private async Task RunAsync(ulong steamId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Jitter(TimeSpan.FromSeconds(5)), cancellationToken).ConfigureAwait(true);
            while (true)
            {
                if (!ShouldCheck(steamId))
                {
                    await _changed.Task.WaitAsync(cancellationToken).ConfigureAwait(true);

                    // Coming back: don't let every account fire at once.
                    await Task.Delay(Jitter(TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(true);
                    continue;
                }

                // Read each round, so a new interval from Settings applies from the next check.
                var interval = TimeSpan.FromSeconds(shell.ConfirmationPolicy.RefreshSeconds);
                var last = _latest.GetValueOrDefault(steamId)?.CheckedUtc ?? DateTime.MinValue;
                var wait = last + interval - DateTime.UtcNow;
                if (wait <= TimeSpan.Zero)
                {
                    await CheckAsync(steamId, cancellationToken).ConfigureAwait(true);
                    wait = interval;
                }

                var changed = _changed.Task;
                await Task.WhenAny(Task.Delay(wait + Jitter(interval / 2), cancellationToken), changed).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Locked, closed, or the account was removed.
        }
    }

    private Task CheckAsync(ulong steamId, CancellationToken cancellationToken)
    {
        if (_inFlight.TryGetValue(steamId, out var running))
        {
            return running;
        }

        var task = CheckAndForgetAsync(steamId, cancellationToken);
        if (!task.IsCompleted)
        {
            _inFlight[steamId] = task;
        }

        return task;
    }

    private async Task CheckAndForgetAsync(ulong steamId, CancellationToken cancellationToken)
    {
        try
        {
            await CheckCoreAsync(steamId, mayAutoSignIn: true, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Locked or closed mid-check.
        }
        finally
        {
            _inFlight.Remove(steamId);
        }
    }

    private async Task CheckCoreAsync(ulong steamId, bool mayAutoSignIn, CancellationToken cancellationToken)
    {
        if (shell.Vault is not { IsUnlocked: true } vault)
        {
            return;
        }

        string name = steamId.ToString(CultureInfo.InvariantCulture);
        try
        {
            var maFile = vault.Load(steamId);
            name = maFile.AccountName ?? name;
            if (!maFile.FullyEnrolled && vault.GetDeviceProfile(steamId) is not null)
            {
                // Linked here but not active on Steam yet: nothing to load until the link is finished.
                Publish(Failed(steamId, Strings.LinkingNotFinished, needsLogin: false));
                return;
            }

            IReadOnlyList<MobileConfirmation> list;
            AccountConnection? connection = null;
#if DEBUG
            if (SampleMode.IsOn)
            {
                // UI review without a real account: sample rows, and sending is simulated.
                await Task.Delay(300, cancellationToken).ConfigureAwait(true);
                list = [.. SampleConfirmations.Create().Where(c => !_sampleGone.Contains((steamId, c.Id)))];
            }
            else
#endif
            {
                if (string.IsNullOrEmpty(maFile.IdentitySecret) || string.IsNullOrEmpty(maFile.DeviceId))
                {
                    Publish(Failed(steamId, Strings.NoConfirmationSecret, needsLogin: false));
                    return;
                }

                connection = shell.ConnectionFor(steamId);
                if (await WebSession.EnsureAsync(connection, maFile.Session, cancellationToken).ConfigureAwait(true))
                {
                    vault.Save(maFile);
                }

                list = await Confirmations.GetAsync(connection, maFile.IdentitySecret, maFile.DeviceId, cancellationToken)
                    .ConfigureAwait(true);
            }

            list = await AutoAcceptAsync(steamId, name, list, connection, maFile.IdentitySecret, maFile.DeviceId, cancellationToken)
                .ConfigureAwait(true);
            Announce(steamId, name, list);
            Publish(new AccountCheck(steamId, list, null, false, DateTime.UtcNow));
        }
        catch (SessionExpiredException)
        {
            // A saved password renews the session by itself.
            if (mayAutoSignIn && await shell.TryAutoSignInAsync(steamId, cancellationToken).ConfigureAwait(true))
            {
                await CheckCoreAsync(steamId, mayAutoSignIn: false, cancellationToken).ConfigureAwait(true);
                return;
            }

            Publish(Failed(steamId, Strings.SignInToLoadConfirmations, needsLogin: true));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Fail-closed: a dead proxy stops the account; say so instead of a generic network error.
            bool proxyDead = vault.GetAccountProxyId(steamId) is { } proxyId
                             && vault.ListProxies().FirstOrDefault(p => p.Id == proxyId)?.Status == ProxyStatus.Dead;
            Publish(Failed(steamId, proxyDead ? Strings.ProxyNotResponding : Strings.CantReachSteam, needsLogin: false));
        }
    }

    /// <summary>Sends what the account's automation allows in one request, logs it, and returns what's left.</summary>
    private async Task<IReadOnlyList<MobileConfirmation>> AutoAcceptAsync(
        ulong steamId, string name, IReadOnlyList<MobileConfirmation> list, AccountConnection? connection,
        string? identitySecret, string? deviceId, CancellationToken cancellationToken)
    {
        var rules = AutomationFor(steamId);
        if (!rules.AcceptsAny || !ChecksInBackground(rules) || shell.Vault is not { IsUnlocked: true } vault)
        {
            return list;
        }

        var picked = list
            .Where(c => AutoAccept.Selects(c, rules.Trades, rules.Market, rules.Purchases)
                        && _autoAttempts.GetValueOrDefault(c.Id) < MaxAutoAttempts)
            .ToList();
        if (picked.Count == 0)
        {
            return list;
        }

        bool ok = false;
#if DEBUG
        if (SampleMode.IsOn)
        {
            ok = true;
            _sampleGone.UnionWith(picked.Select(c => (steamId, c.Id)));
        }
        else
#endif
        {
            try
            {
                ok = connection is not null && identitySecret is not null && deviceId is not null
                     && await Confirmations.RespondAsync(connection, identitySecret, deviceId, picked, accept: true, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Logged as failed below; tried once more on the next check.
            }
        }

        foreach (var confirmation in picked)
        {
            vault.LogConfirmation(steamId, confirmation.Id, confirmation.Type, confirmation.Headline ?? confirmation.TypeName,
                accepted: true, automatic: true, ok, string.Join('\n', confirmation.Summary));
            if (!ok)
            {
                _autoAttempts[confirmation.Id] = _autoAttempts.GetValueOrDefault(confirmation.Id) + 1;
            }
        }

        AutoAccepted?.Invoke(this, new ConfirmationEvent(steamId, name, picked, ok));
        return ok ? [.. list.Except(picked)] : list;
    }

    private void Announce(ulong steamId, string name, IReadOnlyList<MobileConfirmation> list)
    {
        var ids = list.Select(c => c.Id).ToHashSet();
        if (!_known.TryGetValue(steamId, out var known))
        {
            _known[steamId] = ids;
            return;
        }

        var fresh = list.Where(c => !known.Contains(c.Id)).ToList();
        known.UnionWith(ids);
        if (fresh.Count > 0)
        {
            NewConfirmations?.Invoke(this, new ConfirmationEvent(steamId, name, fresh, Ok: true));
        }
    }

    private AccountCheck Failed(ulong steamId, string problem, bool needsLogin)
        => new(steamId, _latest.GetValueOrDefault(steamId)?.Confirmations ?? [], problem, needsLogin, DateTime.UtcNow);

    private void Publish(AccountCheck check)
    {
        _latest[check.SteamId] = check;
        Checked?.Invoke(this, check);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TimeSpan Jitter(TimeSpan max)
        => TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(Math.Max(1, (int)max.TotalMilliseconds)));
}
