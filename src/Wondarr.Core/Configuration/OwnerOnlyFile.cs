using System.Text;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Files that hold secrets (the API key, the Soulseek password, indexer keys, the whole database)
/// are readable by the app user only, whatever <c>UMASK</c> the container runs under. On Unix the
/// mode is part of the <c>open</c> call that creates the file, so there is never a moment in which
/// it is world-readable; it is not set by a chmod after the write.
/// </summary>
public static class OwnerOnlyFile
{
    /// <summary>Read and write for the owner, nothing for anyone else (<c>0600</c>).</summary>
    public const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Opens a file for writing, creating it owner-only. The mode applies when the call creates the
    /// file; an existing file keeps its own (see <see cref="Tighten"/>). On Windows the mode is ignored.
    /// </summary>
    /// <param name="path">The file to create.</param>
    /// <param name="mode"><see cref="FileMode.Create"/> to replace, <see cref="FileMode.CreateNew"/> to refuse an existing file.</param>
    public static FileStream Create(string path, FileMode mode = FileMode.Create)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerReadWrite;
        }

        return new FileStream(path, options);
    }

    /// <summary>Writes a text file, creating it owner-only.</summary>
    /// <param name="path">The file to create.</param>
    /// <param name="text">What to write.</param>
    /// <param name="encoding">The encoding to write it in.</param>
    /// <param name="mode">How to open the file; see <see cref="Create"/>.</param>
    public static void WriteAllText(string path, string text, Encoding encoding, FileMode mode = FileMode.Create)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);

        using var stream = Create(path, mode);
        using var writer = new StreamWriter(stream, encoding);

        writer.Write(text);
    }

    /// <summary>
    /// Makes sure an empty owner-only file exists, for a file another library will create and then
    /// open (SQLite gives its <c>-wal</c> and <c>-shm</c> files the mode of the main file). A file
    /// that is already there is left alone.
    /// </summary>
    /// <param name="path">The file to create.</param>
    public static void EnsureExists(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            return;
        }

        try
        {
            Create(path, FileMode.CreateNew).Dispose();
        }
        catch (IOException) when (File.Exists(path))
        {
            // Created by someone else between the check and the call; theirs stands.
        }
    }

    /// <summary>
    /// Sets an existing file to <c>0600</c> when it has any other mode. Does nothing on Windows or
    /// when the file is missing.
    /// </summary>
    /// <param name="path">The file to tighten.</param>
    /// <returns><see langword="true"/> when the mode was changed.</returns>
    public static bool Tighten(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            if (File.GetUnixFileMode(path) == OwnerReadWrite)
            {
                return false;
            }

            File.SetUnixFileMode(path, OwnerReadWrite);

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file the app user does not own cannot be changed; the next start tries again.
            return false;
        }
    }
}
