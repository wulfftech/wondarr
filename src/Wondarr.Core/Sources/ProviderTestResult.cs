namespace Wondarr.Core.Sources;

/// <summary>
/// The outcome of a connection test against an indexer or a download client. <paramref name="Error"/>
/// never names a secret: an API key or password is masked before the message is built.
/// </summary>
/// <param name="Success">Whether the far end answered as expected.</param>
/// <param name="Error">Why it did not, or <see langword="null"/>.</param>
public sealed record ProviderTestResult(bool Success, string? Error);
