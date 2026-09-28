using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Wondarr.Core.Configuration;

/// <summary>
/// Configuration source for a YAML file such as <c>config.yml</c>. The file is optional; a
/// missing file simply contributes no keys.
/// </summary>
public sealed class YamlConfigurationSource : IConfigurationSource
{
    public YamlConfigurationSource(string filePath) => FilePath = filePath;

    /// <summary>Absolute path of the YAML file.</summary>
    public string FilePath { get; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new YamlConfigurationProvider(FilePath);
}

/// <summary>
/// Reads a YAML file through <see cref="YamlStream"/> (not the serializer, so unknown keys and
/// scalars stay untouched) and flattens it into configuration keys.
/// </summary>
public sealed class YamlConfigurationProvider : ConfigurationProvider
{
    private readonly string _filePath;

    public YamlConfigurationProvider(string filePath) => _filePath = filePath;

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(_filePath))
        {
            var stream = new YamlStream();
            try
            {
                using var reader = File.OpenText(_filePath);
                stream.Load(reader);
            }
            catch (YamlException exception)
            {
                throw new InvalidOperationException(
                    $"Invalid YAML in {_filePath} at line {exception.Start.Line}: {exception.Message}",
                    exception);
            }

            if (stream.Documents.Count > 0)
            {
                Flatten(stream.Documents[0].RootNode, [], data);
            }
        }

        Data = data;
    }

    private static void Flatten(YamlNode node, List<string> path, IDictionary<string, string?> sink)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    if (key is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value))
                    {
                        continue;
                    }

                    path.Add(NormalizeSegment(scalar.Value));
                    Flatten(value, path, sink);
                    path.RemoveAt(path.Count - 1);
                }

                break;

            case YamlSequenceNode sequence:
                for (var index = 0; index < sequence.Children.Count; index++)
                {
                    path.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Flatten(sequence.Children[index], path, sink);
                    path.RemoveAt(path.Count - 1);
                }

                break;

            default:
                if (path.Count > 0)
                {
                    var value = ((YamlScalarNode)node).Value;
                    sink[string.Join(':', path)] = value is null or "~" ? null : value;
                }

                break;
        }
    }

    /// <summary>
    /// Drops <c>_</c> from a key segment so the case-insensitive binder matches PascalCase
    /// properties (<c>url_base</c> → <c>urlbase</c> → <c>UrlBase</c>).
    /// </summary>
    private static string NormalizeSegment(string segment) => segment.Replace("_", string.Empty, StringComparison.Ordinal);
}
