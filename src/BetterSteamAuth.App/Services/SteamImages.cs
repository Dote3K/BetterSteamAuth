using Avalonia.Media.Imaging;
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.App.Services;

/// <summary>Steam images (avatars, items) loaded through an account's own connection and proxy.</summary>
public static class SteamImages
{
    /// <summary>The image as a bitmap, or null if it can't be downloaded or decoded.</summary>
    public static async Task<Bitmap?> LoadAsync(AccountConnection connection, string? url, CancellationToken token)
    {
        byte[]? bytes = await TradeOffers.GetImageAsync(connection, url, token).ConfigureAwait(true);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
