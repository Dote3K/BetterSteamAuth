namespace BetterSteamAuth.Core.Security;

/// <summary>Argon2id parameters. Stored alongside the vault so they can be raised later.</summary>
public sealed record KdfParameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>64 MiB, 3 passes, 1 lane.</summary>
    public static KdfParameters Default { get; } = new(64 * 1024, 3, 1);
}
