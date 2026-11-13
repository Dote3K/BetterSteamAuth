namespace BetterSteamAuth.Core.Guard;

/// <summary>
/// Steam time as seen by one account: local time plus the offset measured by
/// that account's last time sync. Each account owns its own clock.
/// </summary>
public sealed class SteamClock(TimeProvider timeProvider)
{
    private long _offsetSeconds;

    public SteamClock() : this(TimeProvider.System)
    {
    }

    /// <summary>Steam time minus local time, from the last successful sync.</summary>
    public TimeSpan Offset => TimeSpan.FromSeconds(Interlocked.Read(ref _offsetSeconds));

    /// <summary>Current Steam time in Unix seconds.</summary>
    public long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds() + Interlocked.Read(ref _offsetSeconds);

    /// <summary>Records a sync result: the server's Unix time at the moment of the response.</summary>
    public void Synchronize(long serverTime)
        => Interlocked.Exchange(ref _offsetSeconds, serverTime - timeProvider.GetUtcNow().ToUnixTimeSeconds());
}
