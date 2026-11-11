namespace Bifrost.Tests.Systematic;

internal static class InvariantRun
{
    public static int SeedCount(int defaultCount)
    {
        var setting = Environment.GetEnvironmentVariable("BIFROST_INVARIANT_SEED_MULTIPLIER");
        if (string.IsNullOrEmpty(setting))
        {
            return defaultCount;
        }

        if (!int.TryParse(setting, out var multiplier) || multiplier < 1 || multiplier > 16)
        {
            throw new InvalidOperationException("BIFROST_INVARIANT_SEED_MULTIPLIER must be an integer from 1 through 16.");
        }

        return checked(defaultCount * multiplier);
    }
}
