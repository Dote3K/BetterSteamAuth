using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterSteamAuth.Core.Security;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BetterSteamAuth.Storage;

/// <summary>The <c>manifest.json</c> at the root of a <c>.bsabackup</c> file.</summary>
public sealed record BackupManifest(
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("created")] DateTimeOffset Created,
    [property: JsonPropertyName("app")] string? App,
    [property: JsonPropertyName("accounts")] int Accounts,
    [property: JsonPropertyName("encrypted")] bool Encrypted);

/// <summary>A backup unpacked into a temporary folder and opened as a vault. Disposing deletes the folder.</summary>
public sealed class OpenedBackup : IDisposable
{
    internal OpenedBackup(string folder, BackupManifest manifest, Vault vault)
    {
        Folder = folder;
        Manifest = manifest;
        Vault = vault;
    }

    /// <summary>The unpacked data folder (vault.db and maFiles).</summary>
    public string Folder { get; }

    public BackupManifest Manifest { get; }

    /// <summary>The backup's data. Encrypted backups start locked: unlock with the password the backup was made with.</summary>
    public Vault Vault { get; }

    public void Dispose()
    {
        Vault.Dispose();
        SqliteConnection.ClearAllPools();
        VaultBackup.DeleteFolder(Folder);
    }

    /// <summary>Copies the unpacked data (vault.db and maFiles) into <paramref name="destination"/>.</summary>
    internal void CopyTo(string destination)
    {
        Vault.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(Folder))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }
}

/// <summary>
/// Data-only backups: a zip of a data folder (a vault.db snapshot plus every maFile) with a manifest.
/// The program itself is never included.
/// </summary>
public static class VaultBackup
{
    public const string Extension = ".bsabackup";
    public const string FormatName = "bsa-backup";
    public const int FormatVersion = 1;
    private const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes a backup of <paramref name="vault"/> to <paramref name="destination"/>. An app with a password is
    /// backed up as it is stored (nothing is decrypted). An app without one is written as an encrypted data folder
    /// when <paramref name="newPassword"/> is given, or plain when it's null.
    /// </summary>
    public static BackupManifest Create(Vault vault, string destination, string? newPassword, string? appVersion, KdfParameters? kdf = null)
    {
        ArgumentNullException.ThrowIfNull(vault);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        string work = NewTempFolder();
        try
        {
            string data = Path.Combine(work, "data");
            bool encrypted;
            int accounts;
            if (vault.IsEncrypted || string.IsNullOrEmpty(newPassword))
            {
                vault.SnapshotTo(data);
                encrypted = vault.IsEncrypted;
                accounts = vault.ListAccounts().Count;
            }
            else
            {
                using (var copy = Vault.Create(data, newPassword, kdf))
                {
                    copy.MergeFrom(vault);
                    copy.CopySettingsFrom(vault);
                    accounts = copy.ListAccounts().Count;
                }

                SqliteConnection.ClearAllPools();
                encrypted = true;
            }

            foreach (string leftover in Directory.GetFiles(data, VaultDatabase.FileName + "-*"))
            {
                File.Delete(leftover);
            }

            var manifest = new BackupManifest(FormatName, FormatVersion, DateTimeOffset.UtcNow, appVersion, accounts, encrypted);
            File.WriteAllText(Path.Combine(data, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions));

            string temp = destination + ".tmp";
            File.Delete(temp);
            ZipFile.CreateFromDirectory(data, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(temp, destination, overwrite: true);
            return manifest;
        }
        finally
        {
            DeleteFolder(work);
        }
    }

    /// <summary>Reads only the manifest. Throws <see cref="InvalidDataException"/> if the file isn't a backup this version understands.</summary>
    public static BackupManifest ReadManifest(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("No manifest.");
            using var stream = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(stream);
            if (manifest is null || manifest.Format != FormatName || manifest.Version > FormatVersion
                || zip.GetEntry(VaultDatabase.FileName) is null)
            {
                throw new InvalidDataException("Not a supported BetterSteamAuth backup.");
            }

            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException("Not a BetterSteamAuth backup.", ex);
        }
    }

    /// <summary>Unpacks a backup into a temporary folder and opens it. Dispose the result to delete the folder.</summary>
    public static OpenedBackup Open(string path)
    {
        var manifest = ReadManifest(path);
        string folder = NewTempFolder();
        try
        {
            // ExtractToDirectory refuses entries that would land outside the folder.
            ZipFile.ExtractToDirectory(path, folder);
            File.Delete(Path.Combine(folder, ManifestName));
            return new OpenedBackup(folder, manifest, Vault.Open(folder));
        }
        catch
        {
            DeleteFolder(folder);
            throw;
        }
    }

    /// <summary>The name the current data folder gets when a backup replaces it, e.g. <c>BetterSteamAuth.old-2026-11-16-1530</c>.</summary>
    public static string KeptFolderFor(string dataFolder, DateTime now)
        => Path.TrimEndingDirectorySeparator(dataFolder) + ".old-" + now.ToString("yyyy-MM-dd-HHmm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Replaces a data folder with an opened backup. The current folder is never deleted: it's renamed to
    /// <paramref name="keptFolder"/> when it has anything in it. Close the app's vault before calling this.
    /// Returns the kept folder, or null when there was nothing to keep.
    /// </summary>
    public static string? ReplaceDataFolder(OpenedBackup backup, string dataFolder, string keptFolder)
    {
        ArgumentNullException.ThrowIfNull(backup);
        SqliteConnection.ClearAllPools();
        string? kept = null;
        if (Directory.Exists(dataFolder) && Directory.EnumerateFileSystemEntries(dataFolder).Any())
        {
            Directory.Move(dataFolder, keptFolder);
            kept = keptFolder;
        }

        backup.CopyTo(dataFolder);
        return kept;
    }

    internal static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder isn't worth failing the backup over.
        }
    }

    private static string NewTempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "BetterSteamAuth", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}

public sealed partial class Vault
{
    /// <summary>
    /// Writes a consistent copy of this data folder for a backup: a vault.db snapshot (VACUUM INTO, without the
    /// removed accounts) and every account's maFile exactly as stored.
    /// </summary>
    internal void SnapshotTo(string folder)
    {
        Directory.CreateDirectory(folder);
        string db = Path.Combine(folder, VaultDatabase.FileName);
        using (var connection = _db.Open())
        {
            connection.Execute("VACUUM INTO @db", new { db });
        }

        string snapshot = new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString();
        using (var connection = new SqliteConnection(snapshot))
        {
            connection.Open();
            connection.Execute("DELETE FROM removed_accounts; PRAGMA journal_mode = DELETE;");
        }

        foreach (var entry in ListAccounts())
        {
            lock (FileLock(entry.SteamId))
            {
                string source = PathFor(entry.SteamId);
                if (File.Exists(source))
                {
                    File.Copy(source, Path.Combine(folder, entry.FileName), overwrite: true);
                }
            }
        }
    }
}
