using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Wondarr.Core.Tests.References;

/// <summary>
/// Runs a write once, when armed, right after its context has read a song's <c>song_file</c> row: the
/// moment an import recording its file slips in between a release's read of the row and its delete.
/// </summary>
internal sealed class SongFileReadHook : DbCommandInterceptor
{
    private Func<Task>? _write;

    /// <summary>Gets a value indicating whether the write ran.</summary>
    public bool Fired { get; private set; }

    /// <summary>Runs <paramref name="write"/> after the next read of a song's file row.</summary>
    /// <param name="write">The racing write, made through another context.</param>
    public void Arm(Func<Task> write) => _write = write;

    /// <inheritdoc />
    public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
        DbCommand command,
        DataReaderClosingEventData eventData,
        InterceptionResult result)
    {
        // The read of the song's file itself, not a reference-row query that only looks into song_file.
        if (_write is { } write
            && command.CommandText.Contains("FROM \"song_file\"", StringComparison.Ordinal)
            && !command.CommandText.Contains("\"reference_file\"", StringComparison.Ordinal))
        {
            _write = null;
            await write().ConfigureAwait(false);
            Fired = true;
        }

        return result;
    }
}
