using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Devices;
using BetterSteamAuth.Core.Security;
using Dapper;

namespace BetterSteamAuth.Storage;

/// <summary>An account as indexed in <c>vault.db</c>.</summary>
public sealed record AccountEntry(
    ulong SteamId,
    string? AccountName,
    string FileName,
    long? ProxyId,
    long? GroupId,
    string? Note,
    int SortOrder);

/// <summary>
/// The hybrid store: one maFile per account (source of truth for secrets) plus
/// <c>vault.db</c> (index and metadata).
/// </summary>
public sealed partial class Vault : IDisposable
{
    private const string MetaState = "encryption_state";
    private const string MetaKdf = "kdf";
    private const string MetaSalt = "salt";
    private const string MetaWrappedKey = "wrapped_dek";
    private const string MetaWrappedNonce = "wrapped_dek_nonce";
    private const string MetaKeyCheck = "key_check";
    private const string MetaKeyCheckNonce = "key_check_nonce";

    private static readonly byte[] KeyCheckPlaintext = "BetterSteamAuth data key"u8.ToArray();

    private const string TrashFolder = ".trash";
    private const string BackupFolder = ".backup";

    private readonly VaultDatabase _db;
    private readonly ConcurrentDictionary<ulong, object> _fileLocks = new();
    private byte[]? _dataKey;

    private Vault(string folder, bool isEncrypted)
    {
        Folder = folder;
        IsEncrypted = isEncrypted;
        _db = new VaultDatabase(folder);
    }

    public string Folder { get; }

    public bool IsEncrypted { get; private set; }

    public bool IsUnlocked => !IsEncrypted || _dataKey is not null;

    public static bool Exists(string folder) => File.Exists(Path.Combine(folder, VaultDatabase.FileName));

    /// <summary>Creates a new vault. A null or empty password creates an unencrypted vault.</summary>
    public static Vault Create(string folder, string? password, KdfParameters? kdf = null)
    {
        if (Exists(folder))
        {
            throw new InvalidOperationException($"A vault already exists in {folder}.");
        }

        Directory.CreateDirectory(folder);
        bool encrypted = !string.IsNullOrEmpty(password);
        var vault = new Vault(folder, encrypted);
        vault._db.Migrate();

        using var connection = vault._db.Open();
        using var tx = connection.BeginTransaction();
        if (encrypted)
        {
            byte[] dataKey = VaultCrypto.NewDataKey();
            StoreWrappedKey(connection, tx, VaultCrypto.WrapDataKey(dataKey, password, kdf ?? KdfParameters.Default));
            StoreKeyCheck(connection, tx, dataKey);
            vault._dataKey = dataKey;
        }

        VaultDatabase.SetMeta(connection, MetaState, Encoding.UTF8.GetBytes(encrypted ? "encrypted" : "none"), tx);
        tx.Commit();
        return vault;
    }

    /// <summary>Opens an existing vault. Encrypted vaults start locked.</summary>
    public static Vault Open(string folder)
    {
        if (!Exists(folder))
        {
            throw new FileNotFoundException("No vault in this folder.", Path.Combine(folder, VaultDatabase.FileName));
        }

        var vault = new Vault(folder, isEncrypted: false);
        vault._db.Migrate();

        using var connection = vault._db.Open();
        byte[]? state = VaultDatabase.GetMeta(connection, MetaState);
        vault.IsEncrypted = state is not null && Encoding.UTF8.GetString(state) == "encrypted";
        return vault;
    }

    /// <summary>Unlocks an encrypted vault. Throws <see cref="WrongPasswordException"/>.</summary>
    public void Unlock(string password)
    {
        if (!IsEncrypted)
        {
            return;
        }

        using var connection = _db.Open();
        _dataKey = VaultCrypto.UnwrapDataKey(ReadWrappedKey(connection), password);
        if (VaultDatabase.GetMeta(connection, MetaKeyCheck) is null)
        {
            // Vaults created before the key check existed get one on their next unlock.
            using var tx = connection.BeginTransaction();
            StoreKeyCheck(connection, tx, _dataKey);
            tx.Commit();
        }
    }

    /// <summary>
    /// Unlocks with a data key saved earlier by <see cref="ExportDataKey"/> ("remember on this device").
    /// Returns false when the key doesn't belong to this vault.
    /// </summary>
    public bool TryUnlockWithKey(ReadOnlySpan<byte> dataKey)
    {
        if (!IsEncrypted)
        {
            return true;
        }

        using var connection = _db.Open();
        byte[]? check = VaultDatabase.GetMeta(connection, MetaKeyCheck);
        byte[]? nonce = VaultDatabase.GetMeta(connection, MetaKeyCheckNonce);
        if (check is null || nonce is null || dataKey.Length != VaultCrypto.KeySize)
        {
            return false;
        }

        try
        {
            VaultCrypto.Open(dataKey, nonce, check, KeyCheckPlaintext);
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException)
        {
            return false;
        }

        _dataKey = dataKey.ToArray();
        return true;
    }

    /// <summary>A copy of the data key, for the caller to protect (DPAPI) and clear after use.</summary>
    public byte[] ExportDataKey() => (byte[])RequireKey().Clone();

    /// <summary>Forgets the data key. Encrypted maFiles can't be read until <see cref="Unlock"/>.</summary>
    public void Lock()
    {
        if (_dataKey is not null)
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(_dataKey);
            _dataKey = null;
        }
    }

    public void ChangePassword(string currentPassword, string newPassword, KdfParameters? kdf = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPassword);
        if (!IsEncrypted)
        {
            throw new InvalidOperationException("This vault has no password.");
        }

        using var connection = _db.Open();
        var current = ReadWrappedKey(connection);
        byte[] dataKey = VaultCrypto.UnwrapDataKey(current, currentPassword);
        try
        {
            using var tx = connection.BeginTransaction();
            StoreWrappedKey(connection, tx, VaultCrypto.WrapDataKey(dataKey, newPassword, kdf ?? current.Kdf));
            tx.Commit();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <summary>Reads an app setting. Settings aren't encrypted, so this works while locked.</summary>
    public string? GetSetting(string key)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public void SetSetting(string key, string value)
    {
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO settings(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value });
    }

    /// <summary>Records an accepted or rejected confirmation in <c>confirmation_log</c>.</summary>
    public void LogConfirmation(ulong steamId, ulong confirmationId, int type, string? summary, bool accepted, bool automatic, bool ok)
    {
        using var connection = _db.Open();
        connection.Execute(
            "INSERT INTO confirmation_log(steam_id, conf_id, conf_type, summary, action, source, ok, at) " +
            "VALUES (@steamId, @confId, @type, @summary, @action, @source, @ok, @at)",
            new
            {
                steamId = (long)steamId,
                confId = confirmationId.ToString(CultureInfo.InvariantCulture),
                type,
                summary,
                action = accepted ? "accept" : "deny",
                source = automatic ? "auto" : "manual",
                ok = ok ? 1 : 0,
                at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
    }

    public IReadOnlyList<AccountEntry> ListAccounts()
    {
        using var connection = _db.Open();
        return connection.Query<AccountRow>(
                "SELECT steam_id AS SteamId, account_name AS AccountName, filename AS FileName, proxy_id AS ProxyId, " +
                "group_id AS GroupId, note AS Note, sort_order AS SortOrder FROM accounts ORDER BY sort_order, steam_id")
            .Select(r => r.ToEntry())
            .ToList();
    }

    public bool Contains(ulong steamId)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>("SELECT COUNT(*) FROM accounts WHERE steam_id = @id", new { id = (long)steamId }) > 0;
    }

    /// <summary>
    /// Adds a new account: writes its maFile first, then indexes it. A linked account also gets its
    /// proxy and generated device profile in the same database transaction.
    /// </summary>
    public AccountEntry Add(MaFile maFile, long? proxyId = null, DeviceProfile? deviceProfile = null)
    {
        ArgumentNullException.ThrowIfNull(maFile);
        ulong steamId = maFile.SteamId ?? throw new InvalidDataException("The maFile has no SteamID.");
        if (Contains(steamId))
        {
            throw new DuplicateAccountException(steamId);
        }

        string fileName = FileNameFor(steamId);
        WriteMaFile(steamId, maFile);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        connection.Execute(
            "INSERT INTO accounts(steam_id, account_name, filename, proxy_id, created_at, updated_at) VALUES (@id, @name, @file, @proxyId, @now, @now)",
            new { id = (long)steamId, name = IsEncrypted ? null : maFile.AccountName, file = fileName, proxyId, now },
            tx);
        if (deviceProfile is not null)
        {
            connection.Execute(
                "INSERT INTO device_profiles(steam_id, model, friendly_name, android_version, build_id, machine_id, created_at) " +
                "VALUES (@id, @Model, @FriendlyName, @AndroidVersion, @BuildId, @MachineId, @now)",
                new { id = (long)steamId, deviceProfile.Model, deviceProfile.FriendlyName, deviceProfile.AndroidVersion, deviceProfile.BuildId, deviceProfile.MachineId, now },
                tx);
        }

        tx.Commit();
        return new AccountEntry(steamId, IsEncrypted ? null : maFile.AccountName, fileName, proxyId, null, null, 0);
    }

    /// <summary>The account's generated device profile, or null for imported accounts (default profile).</summary>
    public DeviceProfile? GetDeviceProfile(ulong steamId)
    {
        using var connection = _db.Open();
        var row = connection.QuerySingleOrDefault<DeviceProfileRow>(
            "SELECT model AS Model, friendly_name AS FriendlyName, android_version AS AndroidVersion, build_id AS BuildId, " +
            "machine_id AS MachineId FROM device_profiles WHERE steam_id = @id", new { id = (long)steamId });
        return row is null ? null : new DeviceProfile(row.Model, row.FriendlyName, row.AndroidVersion, row.BuildId, row.MachineId);
    }

    public MaFile Load(ulong steamId)
    {
        string path = PathFor(steamId);
        lock (FileLock(steamId))
        {
            string json = File.ReadAllText(path);
            var envelope = MaFileEnvelope.TryParse(json);
            if (envelope is null)
            {
                return MaFile.Parse(json);
            }

            byte[] plaintext = envelope.Decrypt(RequireKey(), steamId);
            try
            {
                return MaFile.Parse(Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    /// <summary>Saves an existing account's maFile (for example after a token refresh).</summary>
    public void Save(MaFile maFile)
    {
        ArgumentNullException.ThrowIfNull(maFile);
        ulong steamId = maFile.SteamId ?? throw new InvalidDataException("The maFile has no SteamID.");
        if (!Contains(steamId))
        {
            throw new InvalidOperationException($"Account {steamId} isn't in this vault.");
        }

        WriteMaFile(steamId, maFile);
    }

    /// <summary>
    /// Moves the maFile to <c>.trash/</c>, records what's needed to restore it later (Settings → Data →
    /// Removed accounts), then removes the index rows. Returns what's needed to <see cref="Restore"/> it now (Undo).
    /// </summary>
    public RemovedAccount Remove(ulong steamId, RemovalReason reason = RemovalReason.Removed)
    {
        var entry = ListAccounts().FirstOrDefault(a => a.SteamId == steamId);
        var profile = GetDeviceProfile(steamId);
        string path = PathFor(steamId);
        string? trashPath = null;
        lock (FileLock(steamId))
        {
            if (File.Exists(path))
            {
                string trash = Path.Combine(Folder, TrashFolder);
                Directory.CreateDirectory(trash);
                string stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
                trashPath = Path.Combine(trash, $"{Path.GetFileName(path)}.{stamp}");
                File.Move(path, trashPath);
            }
        }

        using var connection = _db.Open();
        var password = connection.QuerySingleOrDefault<(byte[]? Secret, byte[]? Nonce)>(
            "SELECT password_enc, password_nonce FROM accounts WHERE steam_id = @id", new { id = (long)steamId });
        using (var tx = connection.BeginTransaction())
        {
            if (trashPath is not null)
            {
                RecordRemoved(connection, tx, steamId, entry, Path.GetFileName(trashPath), reason, profile, password.Secret, password.Nonce);
            }

            connection.Execute(
                "DELETE FROM device_profiles WHERE steam_id = @id; DELETE FROM accounts WHERE steam_id = @id",
                new { id = (long)steamId }, tx);
            tx.Commit();
        }

        return new RemovedAccount(entry ?? new AccountEntry(steamId, null, FileNameFor(steamId), null, null, null, 0), trashPath, profile)
        {
            PasswordSecret = password.Secret,
            PasswordNonce = password.Nonce,
        };
    }

    /// <summary>Undoes <see cref="Remove"/>: moves the maFile back and restores its rows.</summary>
    public void Restore(RemovedAccount removed)
    {
        ArgumentNullException.ThrowIfNull(removed);
        ulong steamId = removed.Entry.SteamId;
        if (Contains(steamId))
        {
            throw new DuplicateAccountException(steamId);
        }

        string path = PathFor(steamId);
        lock (FileLock(steamId))
        {
            if (removed.TrashPath is { } trashPath && File.Exists(trashPath) && !File.Exists(path))
            {
                File.Move(trashPath, path);
            }
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var e = removed.Entry;
        using var connection = _db.Open();
        using var tx = connection.BeginTransaction();
        if (removed.TrashPath is { } undone)
        {
            connection.Execute("DELETE FROM removed_accounts WHERE trash_file = @file", new { file = Path.GetFileName(undone) }, tx);
        }

        connection.Execute(
            "INSERT INTO accounts(steam_id, account_name, filename, proxy_id, group_id, note, password_enc, password_nonce, sort_order, created_at, updated_at) " +
            "VALUES (@id, @name, @file, (SELECT id FROM proxies WHERE id = @proxyId), (SELECT id FROM groups WHERE id = @groupId), @note, @secret, @nonce, @sort, @now, @now)",
            new
            {
                id = (long)steamId, name = e.AccountName, file = FileNameFor(steamId), proxyId = e.ProxyId, groupId = e.GroupId, note = e.Note,
                secret = removed.PasswordSecret, nonce = removed.PasswordNonce, sort = e.SortOrder, now,
            },
            tx);
        if (removed.DeviceProfile is { } p)
        {
            connection.Execute(
                "INSERT INTO device_profiles(steam_id, model, friendly_name, android_version, build_id, machine_id, created_at) " +
                "VALUES (@id, @Model, @FriendlyName, @AndroidVersion, @BuildId, @MachineId, @now)",
                new { id = (long)steamId, p.Model, p.FriendlyName, p.AndroidVersion, p.BuildId, p.MachineId, now },
                tx);
        }

        tx.Commit();
    }

    public void Dispose()
    {
        Lock();
        _db.Checkpoint();
    }

    private void WriteMaFile(ulong steamId, MaFile maFile)
    {
        string json = maFile.ToJson();
        if (IsEncrypted)
        {
            byte[] plaintext = Encoding.UTF8.GetBytes(json);
            json = MaFileEnvelope.Encrypt(RequireKey(), steamId, plaintext).ToJson();
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }

        string path = PathFor(steamId);
        lock (FileLock(steamId))
        {
            AtomicFile.WriteAllText(path, json, Path.Combine(Folder, BackupFolder, Path.GetFileName(path) + ".bak"));
        }
    }

    private byte[] RequireKey() => _dataKey ?? throw new VaultLockedException();

    private object FileLock(ulong steamId) => _fileLocks.GetOrAdd(steamId, _ => new object());

    private string PathFor(ulong steamId) => Path.Combine(Folder, FileNameFor(steamId));

    private static string FileNameFor(ulong steamId) => steamId.ToString(CultureInfo.InvariantCulture) + ".maFile";

    private static void StoreWrappedKey(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, WrappedKey key)
    {
        VaultDatabase.SetMeta(connection, MetaKdf, JsonSerializer.SerializeToUtf8Bytes(key.Kdf), tx);
        VaultDatabase.SetMeta(connection, MetaSalt, key.Salt, tx);
        VaultDatabase.SetMeta(connection, MetaWrappedNonce, key.Nonce, tx);
        VaultDatabase.SetMeta(connection, MetaWrappedKey, key.Ciphertext, tx);
    }

    /// <summary>An empty message sealed with the data key, so a remembered key can be verified without the password.</summary>
    private static void StoreKeyCheck(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction tx, byte[] dataKey)
    {
        var (nonce, ciphertext) = VaultCrypto.Seal(dataKey, [], KeyCheckPlaintext);
        VaultDatabase.SetMeta(connection, MetaKeyCheckNonce, nonce, tx);
        VaultDatabase.SetMeta(connection, MetaKeyCheck, ciphertext, tx);
    }

    private static WrappedKey ReadWrappedKey(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        byte[] Need(string key) => VaultDatabase.GetMeta(connection, key)
                                   ?? throw new InvalidDataException($"vault.db is missing '{key}'.");

        var kdf = JsonSerializer.Deserialize<KdfParameters>(Need(MetaKdf))
                  ?? throw new InvalidDataException("vault.db has invalid KDF parameters.");
        return new WrappedKey(Need(MetaSalt), Need(MetaWrappedNonce), Need(MetaWrappedKey), kdf);
    }

    private sealed record DeviceProfileRow(string Model, string FriendlyName, string AndroidVersion, string BuildId, byte[] MachineId);

    private sealed record AccountRow(long SteamId, string? AccountName, string FileName, long? ProxyId, long? GroupId, string? Note, long SortOrder)
    {
        public AccountEntry ToEntry() => new((ulong)SteamId, AccountName, FileName, ProxyId, GroupId, Note, (int)SortOrder);
    }
}

/// <summary>What <see cref="Vault.Remove"/> took away, so it can be restored.</summary>
public sealed record RemovedAccount(AccountEntry Entry, string? TrashPath, DeviceProfile? DeviceProfile)
{
    /// <summary>The saved Steam password, still encrypted.</summary>
    public byte[]? PasswordSecret { get; init; }

    public byte[]? PasswordNonce { get; init; }
}

public sealed class VaultLockedException : InvalidOperationException
{
    public VaultLockedException() : base("The vault is locked.")
    {
    }

    public VaultLockedException(string message) : base(message)
    {
    }

    public VaultLockedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>The data was saved by a newer version of the app (a database migration this version doesn't know). Nothing was changed.</summary>
public sealed class VaultTooNewException : IOException
{
    public VaultTooNewException(long dataVersion, int supportedVersion)
        : base($"The data has schema version {dataVersion}; this version of the app supports up to {supportedVersion}.")
    {
        DataVersion = dataVersion;
        SupportedVersion = supportedVersion;
    }

    public long DataVersion { get; }

    public int SupportedVersion { get; }
}

public sealed class DuplicateAccountException : InvalidOperationException
{
    public DuplicateAccountException(ulong steamId) : base($"Account {steamId} is already added.")
    {
        SteamId = steamId;
    }

    public DuplicateAccountException() : base("The account is already added.")
    {
    }

    public DuplicateAccountException(string message) : base(message)
    {
    }

    public DuplicateAccountException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public ulong SteamId { get; }
}
