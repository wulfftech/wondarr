namespace Wondarr.Core.Decisions;

/// <summary>
/// What the decision engine knows about a source provider (a Soulseek peer) from what it delivered
/// before. <paramref name="FailuresLast24Hours"/> is the reason the engine skips a peer for a day
/// after two recent failures (MATCHING_ENGINE §6.3).
/// </summary>
/// <param name="Successes">How many times the provider delivered a file that verified.</param>
/// <param name="Failures">How many times the provider failed us, all time.</param>
/// <param name="FailuresLast24Hours">How many of those failures are within the last 24 hours.</param>
public sealed record UserReputation(int Successes, int Failures, int FailuresLast24Hours);