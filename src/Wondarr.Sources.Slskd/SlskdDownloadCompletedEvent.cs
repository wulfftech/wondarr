using Wondarr.Core.Messaging;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// slskd finished writing a file it was asked for. Published only as a hint: polling the transfers
/// API stays the source of truth, so a lost event costs latency and never correctness.
/// </summary>
/// <param name="Username">The peer the file came from.</param>
/// <param name="TransferId">slskd's transfer id, which the grab is tracked by.</param>
/// <param name="RemoteFilename">The peer's own path for the file.</param>
/// <param name="LocalFilename">Where slskd wrote the file.</param>
public sealed record SlskdDownloadCompletedEvent(
    string Username,
    Guid TransferId,
    string RemoteFilename,
    string LocalFilename) : IEvent;