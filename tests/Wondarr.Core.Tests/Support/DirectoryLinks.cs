using System.Diagnostics;

namespace Wondarr.Testing;

// Compiled into both Wondarr.Core.Tests and Wondarr.Sources.Tests (a linked file, like PlatformFactAttributes).

/// <summary>
/// Makes a directory link for a test: a symbolic link, or on Windows, where that needs administrator rights
/// or Developer Mode, a junction, which is a reparse point too and needs neither. Either one is what the
/// delete guards look for, so a test that needs "a link in the folder" runs everywhere.
/// </summary>
public static class DirectoryLinks
{
    /// <summary>
    /// Removes the link itself and nothing it leads to. A test calls it before its temp folder is deleted, because
    /// on Windows a recursive delete of a folder that holds a junction can be refused.
    /// </summary>
    /// <param name="link">The link to remove; a link that is already gone is fine.</param>
    public static void Remove(string link)
    {
        if (Directory.Exists(link))
        {
            Directory.Delete(link, recursive: false);
        }
    }

    /// <summary>Deletes a test's temp folder, links in it removed first and never followed.</summary>
    /// <param name="root">The folder to delete.</param>
    public static void DeleteTree(string root)
    {
        var pending = new Stack<string>();
        var links = new List<string>();
        var listing = new EnumerationOptions { AttributesToSkip = 0 };
        pending.Push(root);

        while (pending.Count > 0)
        {
            foreach (var directory in new DirectoryInfo(pending.Pop()).EnumerateDirectories("*", listing))
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    links.Add(directory.FullName);
                }
                else
                {
                    pending.Push(directory.FullName);
                }
            }
        }

        foreach (var link in links)
        {
            Directory.Delete(link, recursive: false);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Creates <paramref name="link"/> pointing at the directory <paramref name="target"/>.</summary>
    /// <param name="link">The path of the new link; its parent must exist.</param>
    /// <param name="target">The directory it leads to.</param>
    public static void Create(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);

            return;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows() && exception is IOException or UnauthorizedAccessException)
        {
            // Not allowed to make symbolic links here: a junction does the same job.
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new IOException("cmd.exe did not start");

        process.WaitForExit();

        if (process.ExitCode != 0 || !Directory.Exists(link))
        {
            throw new IOException($"Could not make a junction at {link}");
        }
    }
}
