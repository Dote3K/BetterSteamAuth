using BetterSteamAuth.Core.Guard;

namespace BetterSteamAuth.Tests.Core;

// Expected values were computed with an independent Python implementation
// (hmac + struct). The secrets are fake.
public class SteamGuardCodeTests
{
    private const string SharedSecret = "bTvsR2DJ3XG8ZXOoMZ4LXsbbOAI=";

    [Theory]
    [InlineData(0, "879PG")]
    [InlineData(29, "879PG")]
    [InlineData(30, "B3BWP")]
    [InlineData(1700000000, "HMVHQ")]
    [InlineData(1763030400, "9BHYY")]
    [InlineData(2000000000, "55D3B")]
    public void Generate_matches_reference(long time, string expected)
        => Assert.Equal(expected, SteamGuardCode.Generate(SharedSecret, time));

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 29)]
    [InlineData(29, 1)]
    [InlineData(30, 30)]
    public void SecondsRemaining_counts_down_within_period(long time, int expected)
        => Assert.Equal(expected, SteamGuardCode.SecondsRemaining(time));

    [Fact]
    public void Generate_rejects_negative_time()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SteamGuardCode.Generate(SharedSecret, -1));
}

public class ConfirmationKeyTests
{
    private const string IdentitySecret = "V2hhdEFuSWRlbnRpdHlTZWNyZXQ9";

    [Theory]
    [InlineData(1700000000, ConfirmationTag.Confirm, "u5vXC2tDIMx/vDkf8OQt3Yk/j2Y=")]
    [InlineData(1763030400, ConfirmationTag.List, "Vnxx0QXSuiM9aRuAmRR+vpMjZ6Y=")]
    [InlineData(1763030400, ConfirmationTag.Allow, "BRBGXjA2amzV5uXvwU86Nuh8FIY=")]
    [InlineData(1763030400, ConfirmationTag.Cancel, "Ll+c8hFKMr2gVsnFlrIi9eRcjOg=")]
    public void Generate_matches_reference(long time, string tag, string expected)
        => Assert.Equal(expected, ConfirmationKey.Generate(IdentitySecret, time, tag));
}

public class SteamClockTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Value { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Value;
    }

    [Fact]
    public void Now_applies_offset_from_last_sync()
    {
        var time = new FixedTime(DateTimeOffset.FromUnixTimeSeconds(1_000));
        var clock = new SteamClock(time);

        clock.Synchronize(serverTime: 1_042);
        time.Value = DateTimeOffset.FromUnixTimeSeconds(2_000);

        Assert.Equal(TimeSpan.FromSeconds(42), clock.Offset);
        Assert.Equal(2_042, clock.Now);
    }
}
