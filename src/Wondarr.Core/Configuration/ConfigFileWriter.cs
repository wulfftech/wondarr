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
/// Unknown sections, unknown keys and key order survive a write, because the document is read and
/// written through YamlDotNet's representation model rather than the serializer. Comments do not:
/// the representation model does not retain them.
/// </para>
/// <para>
/// String values are written double-quoted, so a setting whose text looks like YAML (<c>~</c>,
/// <c>true</c>, a leading space, a <c>#</c>) reads back as the same string instead of turning into a
/// null, a bool or a comment.
/// </para>
/// <para>
/// Writers are serialised process-wide: a read-modify-write of a file that holds the API key and
/// the Soulseek password must never interleave with another one. The file is replaced atomically
/// (unique temporary file created owner-only in the same directory, then a move), so a reader —
/// including slskd's own watcher — never sees a half-written file.
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

    /// <summary>
    /// Sets one key, or removes it when the value is <see langword="null"/>. An empty list also
    /// writes the marker <c>&lt;key&gt;_set: true</c>, which is removed again as soon as the key has
    /// items.
    /// </summary>
    /// <remarks>
    /// An empty YAML sequence flattens to no configuration key at all, so a reader cannot tell
    /// <c>shared_folders: []</c> (the user deliberately shares nothing) from a file that never
    /// mentions the key (where a default applies). The marker is that difference, in the file.
    /// </remarks>
    private static void SetKey(YamlMappingNode section, string key, object? value)
    {
        // Replaced rather than assigned: a mapping's keys compare by content, and removing first
        // keeps the appended key in its new position rather than the old one.
        Remove(section, key);
        Remove(section, MarkerKey(key));

        if (value is not null)
        {
            section.Children[new YamlScalarNode(key)] = ToNode(value);

            if (value is IEnumerable<string> items && !items.Any())
            {
                section.Children[new YamlScalarNode(MarkerKey(key))] = new YamlScalarNode("true");
            }
        }
    }

    /// <summary>The key that records "this list was written empty" beside an empty sequence.</summary>
    private static string MarkerKey(string key) => $"{key}_set";

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
    /// the invariant culture; text is always double-quoted, so a value a user typed is read back as
    /// the same string and never as YAML of its own.
    /// </summary>
    private static YamlNode ToNode(object value) => value switch
    {
        bool flag => new YamlScalarNode(flag ? "true" : "false"),
        string text => Text(text),
        int number => new YamlScalarNode(number.ToString(CultureInfo.InvariantCulture)),
        long number => new YamlScalarNode(number.ToString(CultureInfo.InvariantCulture)),
        IEnumerable<string> items => new YamlSequenceNode([.. items.Select(item => (YamlNode)Text(item))]),
        _ => Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>A quoted scalar: what the user typed is never reinterpreted as YAML.</summary>
    private static YamlScalarNode Text(string value) =>
        new(value) { Style = ScalarStyle.DoubleQuoted };

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
            // The file holds the API key and the Soulseek password, so on Unix it is created
            // owner-only from the first byte rather than being chmod-ed after the write: a
            // world-readable moment is enough for another local process to read the secrets.
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(temporary, yaml, Utf8WithoutBom);
            }
            else
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                };

                using var stream = new FileStream(temporary, options);
                using var writer = new StreamWriter(stream, Utf8WithoutBom);

                writer.Write(yaml);
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