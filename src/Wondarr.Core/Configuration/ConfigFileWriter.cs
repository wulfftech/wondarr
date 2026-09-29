using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Writes settings back into <c>config.yml</c>, the file the user edits by hand. Only the named
/// top-level section is touched; everything else in the file — including keys Wondarr does not know
/// — is preserved exactly as it was written.
/// </summary>
public interface IConfigFileWriter
{
    /// <summary>
    /// Merges <paramref name="values"/> into the top-level mapping <paramref name="section"/>,
    /// creating the section when it is absent. Keys are snake_case; a <see langword="null"/> value
    /// removes the key. The file is then reloaded so bound options see the change.
    /// </summary>
    /// <param name="section">Top-level section name, for example <c>soulseek</c>.</param>
    /// <param name="values">The keys to write; only these are touched.</param>
    /// <param name="cancellationToken">Cancels the write. A cancelled write leaves the file untouched.</param>
    Task UpdateSectionAsync(
        string section,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// The document is read and written through YamlDotNet's representation model rather than the
/// serializer, so comments-free but unknown sections, key order and scalar styles survive a write.
/// </para>
/// <para>
/// Writers are serialised process-wide: a read-modify-write of a file that holds the API key and
/// the Soulseek password must never interleave with another one. The file is replaced atomically
/// (unique temporary file in the same directory, then a move), so a reader — including slskd's own
/// watcher — never sees a half-written file, and the owner-only mode is set before the move.
/// </para>
/// </remarks>
public sealed partial class ConfigFileWriter : IConfigFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serialises every writer in the process; the file is shared state.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly WondarrPaths _paths;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConfigFileWriter> _logger;

    /// <summary>Initialises a new instance of the <see cref="ConfigFileWriter"/> class.</summary>
    /// <param name="paths">The configuration directory holding <c>config.yml</c>.</param>
    /// <param name="configuration">The configuration root, reloaded after each write.</param>
    /// <param name="logger">Receives Debug lines naming the section and keys, never their values.</param>
    public ConfigFileWriter(WondarrPaths paths, IConfiguration configuration, ILogger<ConfigFileWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        _paths = paths;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task UpdateSectionAsync(
        string section,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ArgumentNullException.ThrowIfNull(values);

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var path = _paths.ConfigFile;
            var document = ReadDocument(path);

            var root = document.Documents[0].RootNode as YamlMappingNode
                ?? throw new InvalidOperationException(
                    $"{path} does not hold a YAML mapping at its top level, so {section} cannot be written");

            var sectionNode = Section(root, section);
            foreach (var (key, value) in values)
            {
                SetKey(sectionNode, key, value);
            }

            Write(path, document);

            // The values are deliberately absent from the log: the section holds the Soulseek
            // password and the generated API key.
            var keys = string.Join(", ", values.Keys);
            LogUpdated(_logger, section, keys);

            if (_configuration is IConfigurationRoot configurationRoot)
            {
                configurationRoot.Reload();
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Loads the file, or starts an empty document when there is none yet.</summary>
    private static YamlStream ReadDocument(string path)
    {
        var stream = new YamlStream();

        if (!File.Exists(path))
        {
            stream.Documents.Add(new YamlDocument(new YamlMappingNode()));

            return stream;
        }

        try
        {
            using var reader = File.OpenText(path);
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            throw new InvalidOperationException(
                $"Invalid YAML in {path} at line {exception.Start.Line}: {exception.Message}",
                exception);
        }

        if (stream.Documents.Count == 0)
        {
            stream.Documents.Add(new YamlDocument(new YamlMappingNode()));
        }

        return stream;
    }

    /// <summary>The existing mapping for <paramref name="section"/>, created (and appended) if absent.</summary>
    private static YamlMappingNode Section(YamlMappingNode root, string section)
    {
        foreach (var (key, value) in root.Children)
        {
            if (key is YamlScalarNode scalar && string.Equals(scalar.Value, section, StringComparison.Ordinal))
            {
                return value as YamlMappingNode
                    ?? throw new InvalidOperationException($"The {section} key in config.yml is not a mapping");
            }
        }

        var created = new YamlMappingNode();
        root.Children[new YamlScalarNode(section)] = created;

        return created;
    }

    /// <summary>Sets one key, or removes it when the value is <see langword="null"/>.</summary>
    private static void SetKey(YamlMappingNode section, string key, object? value)
    {
        // Replaced rather than assigned: a mapping's keys compare by content, and removing first
        // keeps the appended key in its new position rather than the old one.
        Remove(section, key);

        if (value is not null)
        {
            section.Children[new YamlScalarNode(key)] = ToNode(value);
        }
    }

    private static void Remove(YamlMappingNode section, string key)
    {
        YamlNode? existing = null;

        foreach (var (candidate, _) in section.Children)
        {
            if (candidate is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                existing = candidate;
                break;
            }
        }

        if (existing is not null)
        {
            section.Children.Remove(existing);
        }
    }

    /// <summary>
    /// Converts a value to a YAML node. Bools are written <c>true</c>/<c>false</c> and numbers with
    /// the invariant culture; strings are left unstyled so YamlDotNet quotes whatever needs quoting.
    /// </summary>
    private static YamlNode ToNode(object value) => value switch
    {
        bool flag => new YamlScalarNode(flag ? "true" : "false"),
        string text => new YamlScalarNode(text),
        int number => new YamlScalarNode(number.ToString(CultureInfo.InvariantCulture)),
        long number => new YamlScalarNode(number.ToString(CultureInfo.InvariantCulture)),
        IEnumerable<string> items => new YamlSequenceNode(
            [.. items.Select(item => (YamlNode)new YamlScalarNode(item))]),
        _ => new YamlScalarNode(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>
    /// Writes the document over the target atomically: a unique temporary file beside it, then a
    /// move. The newline is forced to LF so the file is identical on every platform.
    /// </summary>
    private static void Write(string path, YamlStream document)
    {
        var builder = new StringBuilder();
        using (var writer = new StringWriter(builder, CultureInfo.InvariantCulture))
        {
            document.Save(writer, assignAnchors: false);
        }

        var yaml = builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporary, yaml, Utf8WithoutBom);

            // The file holds the API key and the Soulseek password: readable by the app user only.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file in the config directory is not worth failing the write over.
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote the {Section} section of config.yml ({Keys})")]
    private static partial void LogUpdated(ILogger logger, string section, string keys);
}