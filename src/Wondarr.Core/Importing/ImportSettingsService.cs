using System.Collections;
using Microsoft.Extensions.Options;
using Wondarr.Core.Configuration;
using Wondarr.Core.Organizer;

namespace Wondarr.Core.Importing;

/// <summary>The import settings the UI edits (Settings &gt; Download clients).</summary>
/// <param name="CleanLeftoverFolders">Whether an import cleans up what the download left behind.</param>
/// <param name="CleanLeftoverFoldersLocked">Whether the environment sets it (<c>APP__IMPORT__CLEAN_LEFTOVER_FOLDERS</c>), so it cannot be changed here.</param>
public sealed record ImportSettings(bool CleanLeftoverFolders, bool CleanLeftoverFoldersLocked);

/// <summary>How an update of the import settings ended.</summary>
/// <param name="Success">Whether the setting was written.</param>
/// <param name="Errors">Why it was refused.</param>
public sealed record ImportSettingsResult(bool Success, IReadOnlyList<string> Errors);

/// <summary>Reads and writes <c>import.clean_leftover_folders</c>.</summary>
public interface IImportSettingsService
{
    /// <summary>Reports the setting and whether the environment owns it.</summary>
    ImportSettings Current();

    /// <summary>Writes the setting to <c>config.yml</c>; the options reload, so no restart is needed.</summary>
    /// <param name="cleanLeftoverFolders">The new value, or <see langword="null"/> to leave it alone.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<ImportSettingsResult> UpdateAsync(bool? cleanLeftoverFolders, CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="IImportSettingsService"/>. A value the environment owns is refused rather than
/// silently overridden, the way the other settings pages do it.
/// </summary>
public sealed class ImportSettingsService : IImportSettingsService
{
    private const string Section = "import";
    private const string Key = "clean_leftover_folders";
    private const string EnvironmentVariable = "APP__IMPORT__CLEAN_LEFTOVER_FOLDERS";

    private readonly IOptionsMonitor<ImportOptions> _options;
    private readonly IConfigFileWriter _writer;
    private readonly bool _locked;

    /// <summary>Initialises a new instance of the <see cref="ImportSettingsService"/> class.</summary>
    /// <param name="options">The live options, which already include the environment.</param>
    /// <param name="writer">Writes <c>config.yml</c> and reloads the configuration.</param>
    /// <param name="environment">The process environment, used to find the settings the environment owns.</param>
    public ImportSettingsService(
        IOptionsMonitor<ImportOptions> options,
        IConfigFileWriter writer,
        IDictionary environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(environment);

        _options = options;
        _writer = writer;
        _locked = EnvironmentOverrides.SectionKeys(environment, "IMPORT").Contains("cleanleftoverfolders");
    }

    /// <inheritdoc />
    public ImportSettings Current() => new(_options.CurrentValue.CleanLeftoverFolders, _locked);

    /// <inheritdoc />
    public async Task<ImportSettingsResult> UpdateAsync(bool? cleanLeftoverFolders, CancellationToken cancellationToken)
    {
        if (cleanLeftoverFolders is null)
        {
            return new ImportSettingsResult(true, []);
        }

        if (_locked)
        {
            return new ImportSettingsResult(
                false,
                [$"cleanLeftoverFolders is set by the environment variable {EnvironmentVariable} and cannot be changed here"]);
        }

        await _writer
            .UpdateSectionAsync(
                Section,
                new Dictionary<string, object?> { [Key] = cleanLeftoverFolders.Value },
                cancellationToken)
            .ConfigureAwait(false);

        return new ImportSettingsResult(true, []);
    }
}
