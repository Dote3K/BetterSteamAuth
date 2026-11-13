using System.Collections.Concurrent;
using System.Net.Sockets;

namespace BetterSteamAuth.Steam.Network;

public enum SniBypass
{
    Off,
    On,
    Auto,
}

public sealed record NetworkOptions(SniBypass SniBypass = SniBypass.Auto, EncryptedDns EncryptedDns = EncryptedDns.Off)
{
    /// <summary>Keys in the <c>settings</c> table of vault.db.</summary>
    public const string SniBypassKey = "network.sni_bypass";
    public const string EncryptedDnsKey = "network.encrypted_dns";

    /// <summary>Reads stored values; missing or unknown values fall back to the defaults.</summary>
    public static NetworkOptions FromSettings(string? sniBypass, string? encryptedDns) => new(
        Enum.TryParse(sniBypass, ignoreCase: true, out SniBypass sni) ? sni : SniBypass.Auto,
        Enum.TryParse(encryptedDns, ignoreCase: true, out EncryptedDns dns) ? dns : EncryptedDns.Off);
}

/// <summary>
/// App-wide network settings applied by every account's own connection: how sockets are opened
/// (encrypted DNS) and whether the TLS ClientHello is split.
/// </summary>
public sealed class NetworkPolicy : IDisposable
{
    private readonly DohResolver? _resolver;
    private readonly ConcurrentDictionary<string, byte> _splitHosts = new(StringComparer.OrdinalIgnoreCase);

    public NetworkPolicy(NetworkOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = options.EncryptedDns == EncryptedDns.Off ? null : new DohResolver(options.EncryptedDns);
    }

    public NetworkOptions Options { get; }

    public bool ShouldSplit(string host) => Options.SniBypass switch
    {
        SniBypass.On => true,
        SniBypass.Auto => _splitHosts.ContainsKey(host),
        _ => false,
    };

    /// <summary>In Auto mode, marks a host as needing a split ClientHello. True if newly marked.</summary>
    public bool MarkForSplit(string host) => Options.SniBypass == SniBypass.Auto && _splitHosts.TryAdd(host, 0);

    /// <summary><see cref="SocketsHttpHandler.ConnectCallback"/> implementation.</summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.DnsEndPoint;
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (_resolver is not null)
            {
                var addresses = await _resolver.ResolveAsync(endpoint.Host, cancellationToken).ConfigureAwait(false);
                await socket.ConnectAsync(addresses, endpoint.Port, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var stream = new NetworkStream(socket, ownsSocket: true);

        // Through a proxy the endpoint is the proxy; the ClientHello still names the target.
        string target = context.InitialRequestMessage.RequestUri?.Host ?? endpoint.Host;
        return ShouldSplit(target) ? new ClientHelloSplittingStream(stream, target) : stream;
    }

    public void Dispose() => _resolver?.Dispose();
}

/// <summary>
/// Auto mode: when a connection is reset while it's being set up, mark the host and retry once
/// with a split ClientHello.
/// </summary>
internal sealed class SniRetryHandler(NetworkPolicy policy) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsSetupFailure(ex)
                                              && request.RequestUri is { } uri
                                              && policy.MarkForSplit(uri.Host))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsSetupFailure(HttpRequestException ex)
        => ex.HttpRequestError is HttpRequestError.SecureConnectionError or HttpRequestError.ConnectionError
           || ex.InnerException is IOException or SocketException;
}
