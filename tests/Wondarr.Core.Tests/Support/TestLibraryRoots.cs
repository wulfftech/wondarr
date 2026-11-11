using Wondarr.Core.Organizer;

namespace Wondarr.Core.Tests.Support;

/// <summary>Library roots for tests: the real check over a real or fake disk, and one helper that marks a temporary root.</summary>
internal static class TestLibraryRoots
{
    /// <summary>The real <see cref="LibraryRoots"/> over the real disk.</summary>
    internal static ILibraryRoots Real { get; } = new LibraryRoots(new DiskOperations());

    /// <summary>The real <see cref="LibraryRoots"/> over <paramref name="disk"/>.</summary>
    internal static ILibraryRoots Over(IDiskOperations disk) => new LibraryRoots(disk);

    /// <summary>Creates <paramref name="root"/> when needed and writes the marker into it; returns the root.</summary>
    internal static string Mark(string root)
    {
        Real.Mark(root);

        return root;
    }
}
