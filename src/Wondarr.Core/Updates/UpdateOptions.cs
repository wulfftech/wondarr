namespace Wondarr.Core.Updates;

/// <summary>
/// The <c>update</c> section of <c>config.yml</c> (environment: <c>APP__UPDATE__CHECK_ENABLED</c>).
/// </summary>
public sealed class UpdateOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether Wondarr asks GitHub for the latest release. When off, no
    /// request is ever made and the scheduled task does nothing.
    /// </summary>
    public bool CheckEnabled { get; set; } = true;
}
