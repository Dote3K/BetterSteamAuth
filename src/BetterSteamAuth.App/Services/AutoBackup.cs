using System.Globalization;
using BetterSteamAuth.Storage;

namespace BetterSteamAuth.App.Services;

/// <summary>How often an automatic backup may be made, at most.</summary>
public enum AutoBackupFrequency
{
    /// <summary>At the next check after the data changed.</summary>
    AsSoonAsPossible,

    Daily,

    Weekly,
}

/// <summary>
/// Optional automatic backups, made only when the data changed since the last one (by fingerprint), only for
/// apps with a password, into a folder outside the data folder.
/// </summary>
public sealed class AutoBackup(Func<Vault?> vault)
{
    public const string EnabledKey = "backup.auto";
    public const string FrequencyKey = "backup.auto_frequency";
    public const string KeepKey = "backup.auto_keep";
    public const string FolderKey = "backup.folder";
    public const string LastAtKey = "backup.last_auto_at";
    public const string FingerprintKey = "backup.last_auto_fingerprint";
    public const int KeepAll = 0;
    private const string FilePrefix = "BetterSteamAuth-auto-";

    private int _running;

    /// <summary>Raised (on a worker thread) after a check, with whether the last attempt failed.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>The last automatic backup failed; shown once in the main window until the next success.</summary>
    public bool LastFailed { get; private set; }

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BetterSteamAuth", "Backups");

    public bool IsAvailable => vault() is { IsEncrypted: true };

    public bool Enabled
    {
        get => IsAvailable && vault()?.GetSetting(EnabledKey) == "on";
        set => vault()?.SetSetting(EnabledKey, value ? "on" : "off");
    }

    public AutoBackupFrequency Frequency
    {
        get => vault()?.GetSetting(FrequencyKey) switch
        {
            "asap" => AutoBackupFrequency.AsSoonAsPossible,
            "weekly" => AutoBackupFrequency.Weekly,
            _ => AutoBackupFrequency.Daily,
        };
        set => vault()?.SetSetting(FrequencyKey, value switch
        {
            AutoBackupFrequency.AsSoonAsPossible => "asap",
            AutoBackupFrequency.Weekly => "weekly",
            _ => "daily",
        });
    }

    /// <summary>How many automatic backups to keep; <see cref="KeepAll"/> keeps every one.</summary>
    public int Keep
    {
        get => vault()?.GetSetting(KeepKey) is { } text
            ? text == "all" ? KeepAll : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 10
            : 10;
        set => vault()?.SetSetting(KeepKey, value == KeepAll ? "all" : value.ToString(CultureInfo.InvariantCulture));
    }

    public string Folder
    {
        get => vault()?.GetSetting(FolderKey) is { Length: > 0 } folder ? folder : DefaultFolder;
        set => vault()?.SetSetting(FolderKey, value);
    }

    public DateTimeOffset? LastAt
        => long.TryParse(vault()?.GetSetting(LastAtKey), NumberStyles.None, CultureInfo.InvariantCulture, out long at)
            ? DateTimeOffset.FromUnixTimeSeconds(at)
            : null;

    /// <summary>
    /// Makes an automatic backup if it's on, the interval since the last one has passed, and the data differs
    /// from it. Safe to call often; only one runs at a time. Returns true when a backup was written.
    /// </summary>
    public async Task<bool> RunIfDueAsync(string? appVersion)
    {
        if (vault() is not { IsEncrypted: true, IsUnlocked: true } current || !Enabled || !IntervalPassed()
            || Interlocked.Exchange(ref _running, 1) == 1)
        {
            return false;
        }

        try
        {
            bool written = await Task.Run(() => BackUpIfChanged(current, appVersion)).ConfigureAwait(false);
            LastFailed = false;
            return written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultLockedException
                                       or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            LastFailed = true;
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IntervalPassed()
    {
        if (LastAt is not { } last)
        {
            return true;
        }

        var interval = Frequency switch
        {
            AutoBackupFrequency.Weekly => TimeSpan.FromDays(7),
            AutoBackupFrequency.Daily => TimeSpan.FromDays(1),
            _ => TimeSpan.Zero,
        };
        return DateTimeOffset.UtcNow - last >= interval;
    }

    private bool BackUpIfChanged(Vault current, string? appVersion)
    {
        string fingerprint = current.ComputeFingerprint();
        if (fingerprint == current.GetSetting(FingerprintKey))
        {
            return false;
        }

        string folder = Folder;
        Directory.CreateDirectory(folder);
        string name = FilePrefix + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + VaultBackup.Extension;
        VaultBackup.Create(current, Path.Combine(folder, name), newPassword: null, appVersion);
        current.SetSetting(LastAtKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        current.SetSetting(FingerprintKey, fingerprint);
        Prune(folder);
        return true;
    }

    /// <summary>Deletes the oldest automatic backups beyond <see cref="Keep"/>. Manual backups are never touched.</summary>
    private void Prune(string folder)
    {
        int keep = Keep;
        if (keep == KeepAll)
        {
            return;
        }

        // The timestamp in the name sorts in creation order.
        foreach (string old in Directory.GetFiles(folder, FilePrefix + "*" + VaultBackup.Extension)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                     .Skip(keep))
        {
            File.Delete(old);
        }
    }
}
