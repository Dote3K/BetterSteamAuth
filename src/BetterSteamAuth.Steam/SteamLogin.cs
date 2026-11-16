using System.Security.Cryptography;
using System.Text;
using BetterSteamAuth.Core.Devices;
using BetterSteamAuth.Core.Guard;
using SteamKit2;
using SteamKit2.Internal;

namespace BetterSteamAuth.Steam;

/// <summary>What Steam asks for besides the password.</summary>
public enum GuardChallenge
{
    /// <summary>A code from the email Steam just sent.</summary>
    EmailCode,
}

public sealed record LoginTokens(ulong SteamId, string AccountName, string AccessToken, string RefreshToken);

public sealed class LoginFailedException : Exception
{
    public LoginFailedException()
    {
    }

    public LoginFailedException(string message) : base(message)
    {
    }

    public LoginFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public LoginFailedException(EResult result, string message, Exception? innerException = null) : base(message, innerException)
    {
        Result = result;
    }

    public EResult Result { get; }
}

/// <summary>
/// Signs in the way the Steam mobile app does: IAuthenticationService over WebAPI, through the
/// account's own connection. Mirrors SteamKit2's SteamAuthentication, which needs a CM connection.
/// </summary>
public static class SteamLogin
{
    private const string AuthService = "IAuthenticationService";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);

    /// <param name="sharedSecret">The authenticator's shared secret, used to answer the Steam Guard code prompt.</param>
    /// <param name="askGuardCode">Called when Steam needs a code we can't produce (an email code). Return null to cancel.</param>
    /// <param name="device">The account's generated device profile; null uses the default profile.</param>
    public static async Task<LoginTokens> LoginAsync(
        AccountConnection connection,
        string accountName,
        string password,
        byte[]? sharedSecret,
        Func<GuardChallenge, Task<string?>> askGuardCode,
        DeviceProfile? device = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(accountName);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(askGuardCode);

        using var api = connection.WebApi(AuthService);

        // 1. Encrypt the password with the account's RSA key.
        var key = await Call<CAuthentication_GetPasswordRSAPublicKey_Response, CAuthentication_GetPasswordRSAPublicKey_Request>(
            api, HttpMethod.Get, "GetPasswordRSAPublicKey", new() { account_name = accountName }).ConfigureAwait(false);

        using var rsa = RSA.Create(new RSAParameters
        {
            Modulus = Convert.FromHexString(key.publickey_mod),
            Exponent = Convert.FromHexString(key.publickey_exp),
        });
        byte[] encrypted = rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.Pkcs1);

        // 2. Start a mobile session.
        var session = await Call<CAuthentication_BeginAuthSessionViaCredentials_Response, CAuthentication_BeginAuthSessionViaCredentials_Request>(
            api, HttpMethod.Post, "BeginAuthSessionViaCredentials", new()
            {
                account_name = accountName,
                encrypted_password = Convert.ToBase64String(encrypted),
                encryption_timestamp = key.timestamp,
                remember_login = true,
                platform_type = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                persistence = ESessionPersistence.k_ESessionPersistence_Persistent,
                website_id = MobileClient.WebsiteId,
                device_details = new CAuthentication_DeviceDetails
                {
                    device_friendly_name = device?.FriendlyName ?? MobileClient.DefaultDeviceName,
                    platform_type = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                    os_type = MobileClient.OsTypeFor(device),
                    gaming_device_type = MobileClient.PhoneDeviceType,
                },
            }).ConfigureAwait(false);

        // 3. Answer the Steam Guard challenge.
        var allowed = session.allowed_confirmations.Select(c => c.confirmation_type).ToHashSet();
        if (allowed.Contains(EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceCode) && sharedSecret is { Length: > 0 })
        {
            string code = SteamGuardCode.Generate(sharedSecret, connection.Clock.Now);
            await SubmitCode(api, session, code, EAuthSessionGuardType.k_EAuthSessionGuardType_DeviceCode).ConfigureAwait(false);
        }
        else if (allowed.Contains(EAuthSessionGuardType.k_EAuthSessionGuardType_EmailCode))
        {
            string code = await askGuardCode(GuardChallenge.EmailCode).ConfigureAwait(false)
                          ?? throw new OperationCanceledException();
            await SubmitCode(api, session, code.Trim(), EAuthSessionGuardType.k_EAuthSessionGuardType_EmailCode).ConfigureAwait(false);
        }
        else if (!allowed.Contains(EAuthSessionGuardType.k_EAuthSessionGuardType_None))
        {
            throw new LoginFailedException($"Steam asked for an unsupported confirmation: {string.Join(", ", allowed)}.");
        }

        // 4. Poll until Steam issues tokens.
        ulong clientId = session.client_id;
        var deadline = DateTimeOffset.UtcNow + PollTimeout;
        var interval = TimeSpan.FromSeconds(Math.Clamp(session.interval, 1, 10));
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await Call<CAuthentication_PollAuthSessionStatus_Response, CAuthentication_PollAuthSessionStatus_Request>(
                api, HttpMethod.Post, "PollAuthSessionStatus", new() { client_id = clientId, request_id = session.request_id }).ConfigureAwait(false);

            if (status.new_client_id != 0)
            {
                clientId = status.new_client_id;
            }

            if (!string.IsNullOrEmpty(status.refresh_token))
            {
                return new LoginTokens(session.steamid, status.account_name, status.access_token, status.refresh_token);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        throw new LoginFailedException(EResult.Timeout, "Steam didn't finish the sign-in in time.");
    }

    /// <summary>Gets a fresh access token from the refresh token. Returns a new refresh token if Steam rotated it.</summary>
    public static async Task<(string AccessToken, string? NewRefreshToken)> RefreshAccessTokenAsync(
        AccountConnection connection, string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        cancellationToken.ThrowIfCancellationRequested();

        using var api = connection.WebApi(AuthService);
        var response = await Call<CAuthentication_AccessToken_GenerateForApp_Response, CAuthentication_AccessToken_GenerateForApp_Request>(
            api, HttpMethod.Post, "GenerateAccessTokenForApp", new()
            {
                refresh_token = refreshToken,
                steamid = connection.SteamId,
                renewal_type = ETokenRenewalType.k_ETokenRenewalType_Allow,
            }).ConfigureAwait(false);

        return (response.access_token, string.IsNullOrEmpty(response.refresh_token) ? null : response.refresh_token);
    }

    private static Task<CAuthentication_UpdateAuthSessionWithSteamGuardCode_Response> SubmitCode(WebAPI.AsyncInterface api, CAuthentication_BeginAuthSessionViaCredentials_Response session, string code, EAuthSessionGuardType type)
        => Call<CAuthentication_UpdateAuthSessionWithSteamGuardCode_Response, CAuthentication_UpdateAuthSessionWithSteamGuardCode_Request>(
            api, HttpMethod.Post, "UpdateAuthSessionWithSteamGuardCode", new()
            {
                client_id = session.client_id,
                steamid = session.steamid,
                code = code,
                code_type = type,
            });

    private static async Task<TResponse> Call<TResponse, TRequest>(WebAPI.AsyncInterface api, HttpMethod method, string func, TRequest request)
        where TResponse : ProtoBuf.IExtensible, new()
        where TRequest : ProtoBuf.IExtensible, new()
    {
        WebAPI.WebAPIResponse<TResponse> response;
        try
        {
            response = await api.CallProtobufAsync<TResponse, TRequest>(method, func, request).ConfigureAwait(false);
        }
        catch (WebAPIRequestException ex)
        {
            throw new LoginFailedException(EResult.Fail, $"{func} failed: HTTP {(int)ex.StatusCode}", ex);
        }

        if (response.Result != EResult.OK)
        {
            throw new LoginFailedException(response.Result, $"{func} failed: {response.Result}");
        }

        return response.Body;
    }
}
