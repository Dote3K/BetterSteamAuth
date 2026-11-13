using System.Text;
using BetterSteamAuth.Steam.Network;

namespace BetterSteamAuth.Tests.Steam;

public class ClientHelloSplittingTests
{
    [Fact]
    public void Split_cuts_inside_the_server_name_and_keeps_the_handshake_bytes()
    {
        byte[] name = Encoding.ASCII.GetBytes("api.steampowered.com");
        byte[] payload = [0x01, 0x00, 0x00, 0x40, .. new byte[40], .. name, .. new byte[30]];
        byte[] record = [0x16, 0x03, 0x01, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload, 0xAA];

        var parts = ClientHelloSplittingStream.Split(record, name);

        Assert.NotNull(parts);
        var (first, second, consumed) = parts.Value;
        Assert.Equal(record.Length - 1, consumed);
        Assert.Equal(record[..3], first[..3]);
        Assert.Equal(record[..3], second[..3]);
        Assert.Equal(payload, first[5..].Concat(second[5..]).ToArray());
        Assert.Equal(first.Length - 5, (first[3] << 8) | first[4]);

        // The full name never appears in a single record.
        Assert.Equal(-1, first.AsSpan().IndexOf(name));
        Assert.Equal(-1, second.AsSpan().IndexOf(name));
    }

    [Fact]
    public void Split_ignores_non_handshake_data()
        => Assert.Null(ClientHelloSplittingStream.Split("CONNECT host:443 HTTP/1.1\r\n"u8, "host"u8));
}
