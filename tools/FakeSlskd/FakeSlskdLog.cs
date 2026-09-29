using System.Globalization;

namespace FakeSlskd;

/// <summary>
/// Writes slskd-style one-line log entries to stdout. The app's <c>SlskdHost</c> forwards every line
/// the child prints into its own log, and the smoke test greps for the login line, so the format
/// (<c>[HH:mm:ss LVL] …</c>) is part of the contract and not just cosmetics.
/// </summary>
public static class FakeSlskdLog
{
    private static readonly Lock Gate = new();

    /// <summary>Writes an information line.</summary>
    public static void Info(string message) => Write("INF", message);

    /// <summary>Writes an error line.</summary>
    public static void Error(string message) => Write("ERR", message);

    private static void Write(string level, string message)
    {
        var stamp = DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var line = string.Concat("[", stamp, " ", level, "] ", message);

        lock (Gate)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }
}