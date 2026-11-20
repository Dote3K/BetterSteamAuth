using System.Globalization;
using System.Text;
using BetterSteamAuth.App.Resources;
using BetterSteamAuth.Core.Updates;
using BetterSteamAuth.Storage;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace BetterSteamAuth.App.Services;

public enum UpdateState
{
    Idle,

    /// <summary>Running from a build folder, not installed by Setup or the portable zip: nothing to update.</summary>
    NotInstalled,

    Checking,
    UpToDate,
    Downloading,

    /// <summary>Downloaded and verified; applies when the app closes, or now with Restart to update.</summary>
    Ready,

    /// <summary>The update didn't verify. Shown once in the main window, with details in Settings.</summary>
    Rejected,

    /// <summary>Couldn't reach GitHub or similar. Quiet: only Settings says so.</summary>
    CheckFailed,
}

/// <summary>
/// In-app updates from GitHub Releases with Velopack, accepted only when SHA256SUMS carries a valid release signature
/// (<see cref="ReleaseSignature"/>) and lists every file Velopack downloads with the same hash. Uses a direct connection
/// (or the system proxy), never an account's proxy. Never restarts by itself.
/// </summary>
public sealed class Updater(Func<Vault?> vault) : IDisposable
{
    public const string AutoKey = "update.auto";
    public const string BetaKey = "update.beta";
    private const string RepoUrl = "https://github.com/Dote3K/BetterSteamAuth";
    private const int MaxSumsBytes = 64 * 1024;
    private static readonly CompositeFormat NotSignedFormat = CompositeFormat.Parse(Strings.UpdateNotSignedFormat);
    private static readonly CompositeFormat NoSignatureFormat = CompositeFormat.Parse(Strings.UpdateNoSignatureFormat);
    private static readonly CompositeFormat BadSignatureFormat = CompositeFormat.Parse(Strings.UpdateBadSignatureFormat);
    private static readonly CompositeFormat DownloadMismatchFormat = CompositeFormat.Parse(Strings.UpdateDownloadMismatchFormat);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    // Just under the shell's hourly timer, so a failed check is retried on the next tick.
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(50);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxSumsBytes };
    private UpdateManager? _manager;
    private VelopackAsset? _ready;
    private int _running;

    /// <summary>Raised on a worker thread whenever <see cref="State"/> changes.</summary>
    public event EventHandler? StatusChanged;

    public UpdateState State { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    /// <summary>0–100 while <see cref="State"/> is <see cref="UpdateState.Downloading"/>.</summary>
    public int DownloadProgress { get; private set; }

    /// <summary>The version waiting to be applied, when <see cref="State"/> is <see cref="UpdateState.Ready"/>.</summary>
    public string? ReadyVersion => _ready?.Version.ToString();

    /// <summary>Why the last update was rejected or the last check failed. Never contains secrets.</summary>
    public string? FailureDetails { get; private set; }

    /// <summary>A rejected update not yet seen in Settings: the main window shows one quiet bar for it.</summary>
    public bool RejectedNoticePending { get; private set; }

    /// <summary>On by default.</summary>
    public bool AutoCheck
    {
        get => vault()?.GetSetting(AutoKey) != "off";
        set => vault()?.SetSetting(AutoKey, value ? "on" : "off");
    }

    /// <summary>Off by default, except while the app itself is a beta.</summary>
    public bool IncludeBeta
    {
        get => vault()?.GetSetting(BetaKey) is { } value ? value == "on" : IsPrerelease(ViewModels.ShellViewModel.AppVersion);
        set => vault()?.SetSetting(BetaKey, value ? "on" : "off");
    }

    public void AcknowledgeRejected() => RejectedNoticePending = false;

    /// <summary>
    /// Checks if automatic checks are on and the last check was 6 hours ago or more (within the hour after a failed
    /// check, which is usually a network hiccup). Safe to call often.
    /// </summary>
    public void CheckIfDue()
    {
        TimeSpan interval = State == UpdateState.CheckFailed ? RetryInterval : Interval;
        if (AutoCheck && (LastCheckedAt is not { } last || DateTimeOffset.UtcNow - last >= interval))
        {
            _ = CheckAsync();
        }
    }

    /// <summary>Check → verify the signed SHA256SUMS → download → verify the package → Ready. Any failure stops there.</summary>
    public async Task CheckAsync()
    {
        if (_ready is not null || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var manager = new UpdateManager(new GithubReleaseSource(RepoUrl, IncludeBeta));
            if (!manager.IsInstalled)
            {
                Set(UpdateState.NotInstalled);
                return;
            }

            Set(UpdateState.Checking);
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            LastCheckedAt = DateTimeOffset.UtcNow;
            if (info is null)
            {
                Set(UpdateState.UpToDate);
                return;
            }

            // Skipping versions means one delta per skipped release; each is checked against its own release's list.
            var target = info.TargetFullRelease;
            var files = info.DeltasToTarget.Append(target)
                .Select(asset => new ReleaseFile(asset.FileName, asset.Version.ToString(), asset.SHA256));
            if (await SignedRelease.FindUnsignedAsync(files, FetchVerifiedSumsAsync).ConfigureAwait(false) is { } unsigned)
            {
                throw new UpdateRejectedException(Format(NotSignedFormat, unsigned));
            }

            // With deltas, Velopack rebuilds the package from the installed one and re-zips it, so it never matches the
            // released full package byte for byte. Each delta is checked by Velopack against the feed's hash (which
            // matched the signed list above) before it's applied; only a full download can be re-hashed here.
            bool mayUseDeltas = info.BaseRelease is not null && info.DeltasToTarget.Length > 0;
            DownloadProgress = 0;
            Set(UpdateState.Downloading);
            await manager.DownloadUpdatesAsync(info, ReportProgress).ConfigureAwait(false);
            if (!mayUseDeltas)
            {
                await CheckDownloadedAsync(target).ConfigureAwait(false);
            }

            _manager = manager;
            _ready = target;
            Set(UpdateState.Ready);
        }
        catch (UpdateRejectedException ex)
        {
            FailureDetails = ex.Message;
            RejectedNoticePending = true;
            Set(UpdateState.Rejected);
        }
#pragma warning disable CA1031 // A failed check, whatever the cause, must never take the app down.
        catch (Exception ex) when (ex is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            LastCheckedAt = DateTimeOffset.UtcNow;
            FailureDetails = ex.Message;
            Set(UpdateState.CheckFailed);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Applies the downloaded update and starts the new version. Exits the process.</summary>
    public void ApplyAndRestart()
    {
        if (_manager is { } manager && _ready is { } ready)
        {
            manager.ApplyUpdatesAndRestart(ready);
        }
    }

    /// <summary>Called when the app closes: a ready update installs once this process has exited.</summary>
    public void ApplyOnExit()
    {
        if (_manager is { } manager && _ready is { } ready)
        {
            manager.WaitExitThenApplyUpdates(ready, silent: true, restart: false);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<IReadOnlyDictionary<string, string>> FetchVerifiedSumsAsync(string version)
    {
        if (ReleaseKeys.Trusted.Count == 0)
        {
            throw new UpdateRejectedException(Strings.UpdateNoReleaseKey);
        }

        string baseUrl = $"{RepoUrl}/releases/download/v{Uri.EscapeDataString(version)}/";
        byte[] sums = await _http.GetByteArrayAsync(new Uri(baseUrl + ReleaseSignature.SumsFileName)).ConfigureAwait(false);
        byte[] signature;
        try
        {
            signature = await _http.GetByteArrayAsync(new Uri(baseUrl + ReleaseSignature.SignatureFileName)).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new UpdateRejectedException(Format(NoSignatureFormat, version));
        }

        if (!ReleaseSignature.Verify(sums, signature, ReleaseKeys.Trusted))
        {
            throw new UpdateRejectedException(Format(BadSignatureFormat, version));
        }

        try
        {
            return ReleaseSignature.ParseSums(sums);
        }
        catch (FormatException ex)
        {
            throw new UpdateRejectedException(Strings.UpdateBadSumsList, ex);
        }
    }

    /// <summary>
    /// Re-hashes a fully downloaded package against the target's hash, which was checked against the signed list
    /// before downloading. Deletes the package if it doesn't match.
    /// </summary>
    private static async Task CheckDownloadedAsync(VelopackAsset target)
    {
        string folder = VelopackLocator.Current.PackagesDir
            ?? throw new UpdateRejectedException(Strings.UpdateDownloadMissing);
        string path = Path.Combine(folder, target.FileName);
        string actual;
        await using (var stream = File.OpenRead(path))
        {
            actual = await ReleaseSignature.HashAsync(stream).ConfigureAwait(false);
        }

        if (!string.Equals(actual, target.SHA256, StringComparison.OrdinalIgnoreCase))
        {
            // Velopack applies a downloaded package on the next start; never leave a bad one behind.
            File.Delete(path);
            throw new UpdateRejectedException(Format(DownloadMismatchFormat, target.FileName));
        }
    }

    private static string Format(CompositeFormat format, object? arg) => string.Format(CultureInfo.CurrentCulture, format, arg);

    private static bool IsPrerelease(string? version) => version?.Contains('-', StringComparison.Ordinal) == true;

    private void ReportProgress(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (percent != DownloadProgress)
        {
            DownloadProgress = percent;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Set(UpdateState state)
    {
        State = state;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>An update that failed verification. Its message is shown under Settings → Updates → Details.</summary>
public sealed class UpdateRejectedException(string message, Exception? inner = null) : Exception(message, inner);
