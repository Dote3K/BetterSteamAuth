#if DEBUG
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BetterSteamAuth.Steam;

namespace BetterSteamAuth.App.ViewModels;

/// <summary>Fake confirmations for reviewing the UI in debug builds. Never compiled into release builds.</summary>
internal static class SampleConfirmations
{
    public static IReadOnlyList<MobileConfirmation> Create()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return
        [
            new() { Id = 1, Type = 2, TypeName = "Trade Offer", Headline = "buyer_88", CreationTime = now - 120, Summary = ["You will give 3 items", "You will receive nothing"],
                    Extra = new() { ["warn"] = System.Text.Json.JsonDocument.Parse("\"An item in this trade was recently in another trade that was cancelled.\"").RootElement } },
            new() { Id = 4, Type = 2, TypeName = "Trade Offer", Headline = "skinswap_bot", CreationTime = now - 1800, Summary = ["You will give 1 item", "You will receive 2 items"] },
            new() { Id = 2, Type = 3, TypeName = "Market Listing", Headline = "AK-47 | Redline (Field-Tested)", CreationTime = now - 540, Summary = ["List for $12.40", "You receive $10.78"] },
            new() { Id = 3, Type = 9, TypeName = "Web API Key", Headline = "Create Web API key", CreationTime = now - 20, Summary = ["Domain: steam-trade.example"] },
        ];
    }

    /// <summary>
    /// A stand-in picture (a two-tone square) for some sample rows, so the layout can be reviewed offline; buyer_88
    /// keeps the type icon to show the fallback.
    /// </summary>
    public static Bitmap? Picture(MobileConfirmation confirmation)
    {
        (uint top, uint bottom) = confirmation.Headline switch
        {
            "skinswap_bot" => (0xFF4F7CACu, 0xFF2B4A6Fu),
            "AK-47 | Redline (Field-Tested)" => (0xFFB33A3Au, 0xFF5C1E1Eu),
            _ => (0u, 0u),
        };
        if (top == 0)
        {
            return null;
        }

        const int size = 64;
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var pixels = new int[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = unchecked((int)(i < pixels.Length / 2 ? top : bottom));
        }

        Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        return bitmap;
    }

    /// <summary>A sample partner level: a fresh account has none to speak of.</summary>
    public static int? PartnerLevel(MobileConfirmation confirmation) => confirmation.Headline == "buyer_88" ? 0 : 42;

    /// <summary>A fake trade offer and partner for the detail screen.</summary>
    public static (TradeOfferDetails, PartnerProfile) TradeDetails(MobileConfirmation confirmation)
    {
        var created = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(confirmation.Headline == "buyer_88" ? -4 : -2900));
        var give = confirmation.Headline == "buyer_88"
            ? new List<TradeItem>
            {
                new("AK-47 | Redline (Field-Tested)", "Classified Rifle", null, 1),
                new("AWP | Asiimov (Field-Tested)", "Covert Sniper Rifle", null, 1),
                new("Prisma 2 Case", "Base Grade Container", null, 3),
            }
            : [new TradeItem("M4A4 | Desolate Space (Minimal Wear)", "Classified Rifle", null, 1)];
        var receive = confirmation.Headline == "buyer_88"
            ? new List<TradeItem>()
            : [new TradeItem("Glock-18 | Water Elemental (Field-Tested)", "Restricted Pistol", null, 1), new TradeItem("Recoil Case", "Base Grade Container", null, 1)];
        return (new TradeOfferDetails(76561198000000999, give, receive),
            new PartnerProfile(confirmation.Headline ?? "partner", null, created, false, false, false, confirmation.Headline == "buyer_88"));
    }

    /// <summary>A fake AddAuthenticator answer with generated (not real) secrets.</summary>
    public static AddResult LinkResult(string deviceId)
    {
        static string Secret() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(20));
        return new AddResult(AddStatus.Added, new Core.Accounts.MaFile
        {
            SharedSecret = Secret(),
            IdentitySecret = Secret(),
            RevocationCode = "R" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10000, 99999),
            SerialNumber = "1234567890",
            DeviceId = deviceId,
            FullyEnrolled = false,
        }, SentToEmail: true, PhoneHint: null);
    }
}
#endif
