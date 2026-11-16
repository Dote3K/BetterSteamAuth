using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>
/// A fingerprint of the data that matters, so automatic backups are only made when it changed.
/// </summary>
public sealed partial class Vault
{
    /// <summary>
    /// SHA-256 (hex) of the decrypted content that matters: each account's Steam data without its access token and
    /// session ID, the account list with names, proxies, groups, notes, and saved passwords, device profiles, the
    /// proxy pool's definitions, and settings other than <c>backup.*</c>. Proxy health and the confirmation history
    /// are left out. Needs the vault unlocked.
    /// </summary>
    public string ComputeFingerprint()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string? value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "\0null");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        foreach (var entry in ListAccounts().OrderBy(a => a.SteamId))
        {
            Add(entry.SteamId.ToString(CultureInfo.InvariantCulture));
            var maFile = Load(entry.SteamId);
            if (maFile.Session is { } session)
            {
                // Refreshed all the time; a new refresh token still counts.
                session.AccessToken = null;
                session.SessionId = null;
            }

            Add(maFile.ToJson());
            Add(entry.AccountName);
            Add(entry.ProxyId?.ToString(CultureInfo.InvariantCulture));
            Add(GetGroupName(entry.GroupId));
            Add(entry.Note);
            Add(GetSteamPassword(entry.SteamId));
            if (GetDeviceProfile(entry.SteamId) is { } profile)
            {
                Add(profile.Model);
                Add(profile.FriendlyName);
                Add(profile.AndroidVersion);
                Add(profile.BuildId);
                Add(Convert.ToBase64String(profile.MachineId));
            }
        }

        foreach (var proxy in ListProxies())
        {
            Add(proxy.Id.ToString(CultureInfo.InvariantCulture));
            var settings = GetProxySettings(proxy.Id);
            Add(settings?.ToString());
            Add(settings?.Username);
            Add(settings?.Password);
        }

        using (var connection = _db.Open())
        {
            foreach (var (key, value) in connection.Query<(string Key, string Value)>(
                         "SELECT key, value FROM settings WHERE key NOT LIKE 'backup.%' ORDER BY key"))
            {
                Add(key);
                Add(value);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
