using System.Collections.Concurrent;

namespace Wondarr.Core.References;

/// <summary>
/// One gate per reference library, shared by every command that works on its rows: a scan (which also
/// identifies and adopts) and "Adopt now" would otherwise walk the same Identified rows together and
/// copy every file twice. The gate is taken before any per-song file lock and released after it.
/// </summary>
internal static class ReferenceLibraryGates
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Gates = new();

    /// <summary>The gate of one reference library; the same instance for every caller in the process.</summary>
    /// <param name="referenceLibraryId">The reference library.</param>
    /// <returns>The gate, with a count of one.</returns>
    internal static SemaphoreSlim For(long referenceLibraryId) =>
        Gates.GetOrAdd(referenceLibraryId, _ => new SemaphoreSlim(1, 1));
}
