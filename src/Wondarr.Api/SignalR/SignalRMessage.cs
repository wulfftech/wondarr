// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Http/SignalR/SignalRMessage.cs and
// ResourceChangeMessage.cs, GPL-3.0.
// Adapted for Wondarr: one non-generic message type carrying a body, so the hub client method
// keeps a single signature for every resource kind.

namespace Wondarr.Api.SignalR;

/// <summary>A message pushed to every connected client, in the shape the *arr UIs expect.</summary>
/// <param name="Name">Which resource family changed, one of <see cref="SignalRMessageNames"/>.</param>
/// <param name="Body">What happened and to which resource.</param>
public sealed record SignalRMessage(string Name, SignalRMessageBody Body);

/// <summary>The change behind a <see cref="SignalRMessage"/>.</summary>
/// <param name="Action">
/// The kind of change: <c>updated</c>, <c>sync</c> (here is the whole collection) or <c>deleted</c>.
/// </param>
/// <param name="Resource">The resource the action applies to, or the collection for <c>sync</c>.</param>
public sealed record SignalRMessageBody(string Action, object? Resource);
