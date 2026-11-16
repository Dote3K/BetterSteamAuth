using BetterSteamAuth.Core.Accounts;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>An account in another vault (a backup) and whether this vault already has it.</summary>
public sealed record MergeCandidate(ulong SteamId, string? AccountName, bool AlreadyHere);

/// <summary>
/// Copying accounts between vaults: restoring a backup into this app, and writing a password-protected
/// backup of an app that has no password.
/// </summary>
public sealed partial class Vault
{
    /// <summary>What <see cref="MergeFrom"/> would do. Both vaults must be unlocked.</summary>
    public IReadOnlyList<MergeCandidate> PreviewMerge(Vault source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.ListAccounts()
            .Select(a => new MergeCandidate(a.SteamId, a.AccountName ?? source.TryLoad(a.SteamId)?.AccountName, Contains(a.SteamId)))
            .ToList();
    }

    /// <summary>
    /// Adds the accounts this vault doesn't have, with their proxy (added to the pool if missing), group,
    /// note, device profile, and saved password. Accounts already here are left as they are. Returns how many were added.
    /// </summary>
    public int MergeFrom(Vault source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int added = 0;
        foreach (var entry in source.ListAccounts().Where(a => !Contains(a.SteamId)))
        {
            var maFile = source.Load(entry.SteamId);
            maFile.Session ??= new MaFileSession();
            if (maFile.Session.SteamId == 0)
            {
                maFile.Session.SteamId = entry.SteamId;
            }

            long? proxyId = entry.ProxyId is { } sourceProxy && source.GetProxySettings(sourceProxy) is { } proxy
                ? FindOrAddProxy(proxy)
                : null;
            Add(maFile, proxyId, source.GetDeviceProfile(entry.SteamId));
            SetAccountGroup(entry.SteamId, source.GetGroupName(entry.GroupId));
            SetAccountNote(entry.SteamId, entry.Note);
            if (CanSaveSteamPasswords && source.GetSteamPassword(entry.SteamId) is { } password)
            {
                SetSteamPassword(entry.SteamId, password);
            }

            added++;
        }

        return added;
    }

    /// <summary>Copies every app setting from another vault (used for backups, never when restoring into an app).</summary>
    internal void CopySettingsFrom(Vault source)
    {
        using var from = source._db.Open();
        var settings = from.Query<(string Key, string Value)>("SELECT key, value FROM settings").ToList();
        using var to = _db.Open();
        using var tx = to.BeginTransaction();
        foreach (var (key, value) in settings)
        {
            to.Execute(
                "INSERT INTO settings(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                new { key, value }, tx);
        }

        tx.Commit();
    }

    private MaFile? TryLoad(ulong steamId) => TryReadMaFile(PathFor(steamId), steamId);

    /// <summary>The pool's proxy with the same scheme, host, port, and username, added first if there's none.</summary>
    private long FindOrAddProxy(ProxySettings proxy)
    {
        AddProxies([proxy]);
        return ListProxies().First(p => p.Scheme == proxy.Scheme && p.Host == proxy.Host && p.Port == proxy.Port
                                        && string.Equals(p.Username, proxy.Username, StringComparison.Ordinal)).Id;
    }
}
