using System.Globalization;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace BetterSteamAuth.Storage;

/// <summary>Opens <c>vault.db</c> and applies embedded migrations tracked by <c>PRAGMA user_version</c>.</summary>
internal sealed class VaultDatabase
{
    public const string FileName = "vault.db";

    private readonly string _connectionString;

    public VaultDatabase(string folder)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, FileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        return connection;
    }

    public void Migrate()
    {
        using var connection = Open();
        long version = connection.ExecuteScalar<long>("PRAGMA user_version;");

        foreach (var (number, sql) in LoadMigrations().Where(m => m.Number > version))
        {
            using var tx = connection.BeginTransaction();
            connection.Execute(sql, transaction: tx);
            connection.Execute(string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {number};"), transaction: tx);
            tx.Commit();
        }
    }

    /// <summary>Checkpoints the WAL so a closed vault folder holds only <c>vault.db</c>.</summary>
    public void Checkpoint()
    {
        using var connection = Open();
        connection.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public static byte[]? GetMeta(SqliteConnection connection, string key)
        => connection.ExecuteScalar<byte[]?>("SELECT value FROM vault_meta WHERE key = @key", new { key });

    public static void SetMeta(SqliteConnection connection, string key, byte[] value, SqliteTransaction? tx = null)
        => connection.Execute(
            "INSERT INTO vault_meta(key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
            new { key, value },
            tx);

    private static IEnumerable<(int Number, string Sql)> LoadMigrations()
    {
        var assembly = typeof(VaultDatabase).Assembly;
        const string prefix = "BetterSteamAuth.Storage.Migrations.";

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Number: int.Parse(n.AsSpan(prefix.Length, 3), CultureInfo.InvariantCulture), Name: n))
            .OrderBy(m => m.Number)
            .Select(m => (m.Number, Read(assembly, m.Name)));
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Missing migration resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
