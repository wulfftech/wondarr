namespace Compilarr.Core.Tests.Jobs;

/// <summary>
/// Polling helper for the tests that have to wait for the background executor. Production code
/// waits on the queue's channel; tests have no such signal, so they poll — with a hard cap.
/// </summary>
internal static class TestWait
{
    /// <summary>The longest a test waits for a condition before it fails.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Polls <paramref name="condition"/> until it holds or <see cref="Timeout"/> passes.</summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return await condition();
    }
}
