using SteamKit2;
using SteamKit2.Internal;

namespace BetterSteamAuth.Steam;

/// <summary>Measures Steam's clock through the account's own connection (ITwoFactorService/QueryTime).</summary>
public static class TimeSync
{
    public static async Task SyncAsync(AccountConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        using var api = connection.WebApi("ITwoFactorService");
        var request = new CTwoFactor_Time_Request { sender_time = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        var response = await api.CallProtobufAsync<CTwoFactor_Time_Response, CTwoFactor_Time_Request>(
            HttpMethod.Post, "QueryTime", request).ConfigureAwait(false);

        if (response.Result is not (EResult.OK or EResult.Invalid) || response.Body.server_time == 0)
        {
            throw new SteamException($"QueryTime failed: {response.Result}");
        }

        connection.Clock.Synchronize((long)response.Body.server_time);
    }

    /// <summary>
    /// Keeps the account's clock in sync: first after a random delay, then about every
    /// <paramref name="interval"/> with jitter, so accounts never sync in lockstep.
    /// Failures keep the last known offset.
    /// </summary>
    public static async Task RunAsync(AccountConnection connection, TimeSpan interval, Action<Exception>? onError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await Task.Delay(Jitter(TimeSpan.FromSeconds(10)), cancellationToken).ConfigureAwait(false);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or WebAPIRequestException or SteamException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                onError?.Invoke(ex);
            }

            await Task.Delay(interval + Jitter(interval / 10), cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Jitter(TimeSpan max)
        => TimeSpan.FromMilliseconds(System.Security.Cryptography.RandomNumberGenerator.GetInt32((int)max.TotalMilliseconds));
}

public sealed class SteamException : Exception
{
    public SteamException()
    {
    }

    public SteamException(string message) : base(message)
    {
    }

    public SteamException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
