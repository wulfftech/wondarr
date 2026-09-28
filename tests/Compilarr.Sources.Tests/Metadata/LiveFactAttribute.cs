using Xunit;

namespace Compilarr.Sources.Tests.Metadata;

/// <summary>
/// A <see cref="FactAttribute"/> for tests that talk to a live provider. They are skipped unless
/// <c>COMPILARR_LIVE_TESTS=1</c>, because CI must not depend on someone else's uptime — or spend
/// someone else's rate limit.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>Initialises a new instance of the <see cref="LiveFactAttribute"/> class.</summary>
    public LiveFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("COMPILARR_LIVE_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Live provider tests run only with COMPILARR_LIVE_TESTS=1.";
        }
    }
}