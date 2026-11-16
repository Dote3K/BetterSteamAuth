using System.Globalization;
using BetterSteamAuth.Core.Accounts;
using BetterSteamAuth.Core.Guard;
using SteamKit2;
using SteamKit2.Internal;

namespace BetterSteamAuth.Steam;

public enum AddStatus
{
    /// <summary>Secrets received; the authenticator isn't active until it's finalized.</summary>
    Added,

    /// <summary>The account already has a mobile authenticator somewhere else.</summary>
    AlreadyLinked,

    /// <summary>Steam wants a phone number on this account first.</summary>
    PhoneRequired,
}

public enum FinalizeStatus
{
    Done,

    /// <summary>The activation code from the email or SMS is wrong.</summary>
    BadActivationCode,

    /// <summary>Steam didn't accept the codes this authenticator generated (usually clock trouble).</summary>
    CodesRejected,
}

/// <param name="MaFile">The new maFile, not yet fully enrolled. Null unless <see cref="AddStatus.Added"/>.</param>
/// <param name="SentToEmail">True when Steam sent the activation code by email, false for SMS.</param>
/// <param name="PhoneHint">The last digits of the phone the SMS went to, when Steam says.</param>
public sealed record AddResult(AddStatus Status, MaFile? MaFile, bool SentToEmail, string? PhoneHint);

/// <param name="Active">Steam reports an active mobile authenticator.</param>
/// <param name="DeviceId">The device ID it was linked with.</param>
public sealed record AuthenticatorStatus(bool Active, string? DeviceId);

public sealed class LinkFailedException : Exception
{
    public LinkFailedException()
    {
    }

    public LinkFailedException(string message) : base(message)
    {
    }

    public LinkFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public LinkFailedException(int status, string message, Exception? innerException = null) : base(message, innerException)
    {
        Status = status;
    }

    /// <summary>Steam's status (an EResult value), when it sent one.</summary>
    public int Status { get; }
}

/// <summary>
/// Adds Steam Guard Mobile Authenticator to an account (ITwoFactorService), through the account's own
/// connection. Status values follow SteamAuth/SDA; to be checked with a real account.
/// </summary>
public static class AuthenticatorLinker
{
    private const string Service = "ITwoFactorService";
    private const uint MobileAuthenticator = 1;
    private const uint AuthenticatorVersion = 2;
    private const int StatusPhoneRequired = 2;
    private const int StatusDuplicateRequest = 29;
    private const int StatusCodeMismatch = 88;
    private const int StatusActivationCodeMismatch = 89;
    private const int ConfirmTypeEmail = 3;
    private const int MaxFinalizeAttempts = 30;

    /// <summary>
    /// Asks Steam for new authenticator secrets. On <see cref="AddStatus.Added"/>, save the returned
    /// maFile before doing anything else: it's the only copy of the secrets.
    /// </summary>
    public static async Task<AddResult> AddAsync(
        AccountConnection connection, string accessToken, ulong steamId, string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(deviceId);

        var (r, result) = await Call<CTwoFactor_AddAuthenticator_Response, CTwoFactor_AddAuthenticator_Request>(
            connection, accessToken, "AddAuthenticator", new()
            {
                steamid = steamId,
                authenticator_type = MobileAuthenticator,
                device_identifier = deviceId,
                version = AuthenticatorVersion,
            }, allowErrorResult: true, cancellationToken).ConfigureAwait(false);

        switch (StatusOf(r.status, result))
        {
            case StatusDuplicateRequest:
                return new AddResult(AddStatus.AlreadyLinked, null, false, null);
            case StatusPhoneRequired:
                return new AddResult(AddStatus.PhoneRequired, null, false, null);
            case (int)EResult.OK when r.shared_secret is { Length: > 0 } && r.identity_secret is { Length: > 0 }:
                break;
            default:
                throw new LinkFailedException(StatusOf(r.status, result), $"AddAuthenticator failed: {(EResult)StatusOf(r.status, result)}");
        }

        var maFile = new MaFile
        {
            SharedSecret = Convert.ToBase64String(r.shared_secret),
            IdentitySecret = Convert.ToBase64String(r.identity_secret),
            Secret1 = r.secret_1 is { Length: > 0 } ? Convert.ToBase64String(r.secret_1) : null,
            SerialNumber = r.serial_number.ToString(CultureInfo.InvariantCulture),
            RevocationCode = r.revocation_code,
            Uri = r.uri,
            ServerTime = (long)r.server_time,
            AccountName = r.account_name,
            TokenGid = r.token_gid,
            Status = r.status,
            DeviceId = deviceId,
            FullyEnrolled = false,
        };
        return new AddResult(
            AddStatus.Added,
            maFile,
            SentToEmail: r.confirm_type == ConfirmTypeEmail || string.IsNullOrEmpty(r.phone_number_hint),
            PhoneHint: string.IsNullOrEmpty(r.phone_number_hint) ? null : r.phone_number_hint);
    }

    /// <summary>
    /// Activates the authenticator with the code Steam sent. Steam may ask for codes from more than one
    /// 30-second window; those are generated and sent here.
    /// </summary>
    public static async Task<FinalizeStatus> FinalizeAsync(
        AccountConnection connection, string accessToken, ulong steamId, byte[] sharedSecret, string activationCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(sharedSecret);
        ArgumentException.ThrowIfNullOrEmpty(activationCode);

        long time = connection.Clock.Now;
        for (int attempt = 0; attempt < MaxFinalizeAttempts; attempt++)
        {
            var (r, result) = await Call<CTwoFactor_FinalizeAddAuthenticator_Response, CTwoFactor_FinalizeAddAuthenticator_Request>(
                connection, accessToken, "FinalizeAddAuthenticator", new()
                {
                    steamid = steamId,
                    authenticator_code = SteamGuardCode.Generate(sharedSecret, time),
                    authenticator_time = (ulong)time,
                    activation_code = activationCode.Trim(),
                    validate_sms_code = true,
                }, allowErrorResult: true, cancellationToken).ConfigureAwait(false);

            int status = StatusOf(r.status, result);
            if (status == StatusActivationCodeMismatch)
            {
                return FinalizeStatus.BadActivationCode;
            }

            if (r.success)
            {
                return FinalizeStatus.Done;
            }

            if (status != StatusCodeMismatch)
            {
                throw new LinkFailedException(status, $"FinalizeAddAuthenticator failed: {(EResult)status}");
            }

            // Steam wants the code of the next window.
            time = (r.server_time > 0 ? (long)r.server_time : time) + SteamGuardCode.PeriodSeconds;
        }

        return FinalizeStatus.CodesRejected;
    }

    /// <summary>Whether the account has an active mobile authenticator, and with which device ID.</summary>
    public static async Task<AuthenticatorStatus> QueryStatusAsync(
        AccountConnection connection, string accessToken, ulong steamId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var (r, _) = await Call<CTwoFactor_Status_Response, CTwoFactor_Status_Request>(
            connection, accessToken, "QueryStatus", new() { steamid = steamId }, allowErrorResult: false, cancellationToken).ConfigureAwait(false);
        return new AuthenticatorStatus(r.state != 0 && r.authenticator_type == MobileAuthenticator, r.device_identifier);
    }

    /// <summary>The body's status, or the response's EResult when the body has none.</summary>
    private static int StatusOf(int bodyStatus, EResult result) => bodyStatus != 0 ? bodyStatus : (int)result;

    private static async Task<(TResponse Body, EResult Result)> Call<TResponse, TRequest>(
        AccountConnection connection, string accessToken, string func, TRequest request, bool allowErrorResult, CancellationToken cancellationToken)
        where TResponse : ProtoBuf.IExtensible, new()
        where TRequest : ProtoBuf.IExtensible, new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var api = connection.WebApi(Service);
        WebAPI.WebAPIResponse<TResponse> response;
        try
        {
            response = await api.CallProtobufAsync<TResponse, TRequest>(
                HttpMethod.Post, func, request, extraArgs: new Dictionary<string, object?> { ["access_token"] = accessToken }).ConfigureAwait(false);
        }
        catch (WebAPIRequestException ex)
        {
            throw new HttpRequestException($"{func} failed: HTTP {(int)ex.StatusCode}", ex, ex.StatusCode);
        }

        // These calls report most outcomes in the body's status; only fail on a transport-level error result.
        if (response.Result != EResult.OK && !(allowErrorResult && response.Body is not null))
        {
            throw new LinkFailedException((int)response.Result, $"{func} failed: {response.Result}");
        }

        return (response.Body, response.Result);
    }
}
