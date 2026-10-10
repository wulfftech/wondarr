using System.Collections;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;

namespace Wondarr.Core.Updates;

/// <summary>The Updates card of Settings &gt; General.</summary>
/// <param name="CheckEnabled">Whether Wondarr asks GitHub for new releases.</param>
/// <param name="CheckEnabledLocked">Whether the environment sets it (<c>APP__UPDATE__CHECK_ENABLED</c>), so it cannot be changed here.</param>
public sealed record UpdateSettings(bool CheckEnabled, bool CheckEnabledLocked);

/// <summary>How an update of the setting ended.</summary>
/// <param name="Success">Whether the setting was written.</param>
/// <param name="Errors">Why it was refused.</param>
public sealed record UpdateSettingsResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>Reads and writes <c>update.check_enabled</c>.</summary>
public interface IUpdateSettingsService
{
    /// <summary>Reports the setting and whether the environment owns it.</summary>
    UpdateSettings Current();

    /// <summary>Writes the setting to <c>config.yml</c>; the options reload, so no restart is needed.</summary>
    /// <param name="checkEnabled">The new value, or <see langword="null"/> to leave it alone.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<UpdateSettingsResult> UpdateAsync(bool? checkEnabled, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IUpdateSettingsService"/>. A value the environment owns is refused rather than
/// silently overridden, the way the other settings pages do it.
/// </summary>
public sealed class UpdateSettingsService : IUpdateSettingsService
{
    private const string Section = "update";
    private const string Key = "check_enabled";
    private const string EnvironmentVariable = "APP__UPDATE__CHECK_ENABLED";

    private readonly IOptionsMonitor<UpdateOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly bool _locked;

    /// <summary>Initialises a new instance of the <see cref="UpdateSettingsService"/> class.</summary>
    /// <param name="options">The live options, which already include the environment.</param>
    /// <param name="writer">Writes <c>config.yml</c> and reloads the configuration.</param>
    /// <param name="environment">The process environment, used to find the settings the environment owns.</param>
    public UpdateSettingsService(
        IOptionsMonitor<UpdateOptions> options,
        IConfigFileWriter writer,
        IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options;
        _writer = writer;
        _locked = EnvironmentOverrides.SectionKeys(environment, "UPDATE").Contains("checkenabled");
    }

    /// <inheritdoc />
    public UpdateSettings Current() => new(_options.CurrentValue.CheckEnabled, _locked);

    /// <inheritdoc />
    public async Task<UpdateSettingsResult> UpdateAsync(bool? checkEnabled, CancellationToken cancellationToken)
    {
        if (checkEnabled is null)
        {
            return new UpdateSettingsResult(true, []);
        }

        if (_locked)
        {
            return new UpdateSettingsResult(
                false,
                [$"checkEnabled is set by the environment variable {EnvironmentVariable} and cannot be changed here"]);
        }

        await _writer
            .UpdateSectionAsync(
                Section,
                new Dictionary<string, object?> { [Key] = checkEnabled.Value },
                cancellationToken)
            .ConfigureAwait(false);

        return new UpdateSettingsResult(true, []);
    }
}
