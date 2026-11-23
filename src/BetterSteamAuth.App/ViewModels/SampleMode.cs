namespace BetterSteamAuth.App.ViewModels;

/// <summary>
/// Debug-only sample mode (<c>BSA_SAMPLE_CONFIRMATIONS=1</c>): Steam calls are replaced with fake answers
/// so flows can be tried without real accounts. Always off in release builds.
/// </summary>
internal static class SampleMode
{
#if DEBUG
    public static bool IsOn => Environment.GetEnvironmentVariable("BSA_SAMPLE_CONFIRMATIONS") == "1";
#else
    public static bool IsOn => false;
#endif

    /// <summary>Runs <paramref name="real"/>, or after a short delay returns <paramref name="sample"/> in sample mode.</summary>
    public static async Task<T> Or<T>(Func<Task<T>> real, Func<T> sample)
    {
        if (IsOn)
        {
            await Task.Delay(600).ConfigureAwait(true);
            return sample();
        }

        return await real().ConfigureAwait(true);
    }

    /// <summary>Like the other overload, for a sample that has to await something.</summary>
    public static async Task<T> Or<T>(Func<Task<T>> real, Func<Task<T>> sample)
    {
        if (IsOn)
        {
            await Task.Delay(600).ConfigureAwait(true);
            return await sample().ConfigureAwait(true);
        }

        return await real().ConfigureAwait(true);
    }
}
