using System.Text;
using Microsoft.Extensions.Configuration;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Compilarr.Core.Configuration;

/// <summary>
/// Creates <c>config.yml</c> on first run and makes sure it carries an API key — the same
/// bootstrap the *arr applications do with <c>config.xml</c>.
/// </summary>
public static class ConfigFileInitializer
{
    private static readonly YamlScalarNode ServerKey = new("server");
    private static readonly YamlScalarNode ApiKeyKey = new("api_key");

    /// <summary>
    /// Ensures <paramref name="paths"/>.ConfigDir and <c>config.yml</c> exist. Runs before the
    /// host is built, so it is deliberately synchronous.
    /// </summary>
    /// <returns><c>true</c> when the file was written or rewritten.</returns>
    public static bool EnsureInitialized(CompilarrPaths paths, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(configuration);

        Directory.CreateDirectory(paths.ConfigDir);

        if (!File.Exists(paths.ConfigFile))
        {
            Save(BuildDefault(GenerateApiKey()), paths.ConfigFile);
            return true;
        }

        // The API key is supplied outside the file, so leave the file exactly as it is.
        if (!string.IsNullOrWhiteSpace(configuration["Server:ApiKey"]))
        {
            return false;
        }

        var stream = Load(paths.ConfigFile);

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException($"{paths.ConfigFile}: expected a YAML mapping at the top level");
        }

        if (!root.Children.TryGetValue(ServerKey, out var serverNode))
        {
            serverNode = new YamlMappingNode();
            root.Children[ServerKey] = serverNode;
        }
        else if (serverNode is not YamlMappingNode)
        {
            throw new InvalidOperationException($"{paths.ConfigFile}: 'server' must be a YAML mapping");
        }

        var server = (YamlMappingNode)serverNode;
        if (server.Children.TryGetValue(ApiKeyKey, out var existing)
            && existing is YamlScalarNode { Value: { Length: > 0 } })
        {
            // Never overwrite a key that is already there.
            return false;
        }

        // Rewriting the document drops any comments the user added; acceptable for the one-off
        // "generate the key" path, and the key is only written when it is missing.
        server.Children[ApiKeyKey] = new YamlScalarNode(GenerateApiKey());
        Save(stream, paths.ConfigFile);

        return true;
    }

    private static string GenerateApiKey() => Guid.NewGuid().ToString("N");

    private static YamlStream BuildDefault(string apiKey)
    {
        var server = new YamlMappingNode
        {
            { "port", new YamlScalarNode(new ServerOptions().Port.ToString(System.Globalization.CultureInfo.InvariantCulture)) },
            { "bind_address", new YamlScalarNode(new ServerOptions().BindAddress) },
            { "url_base", new YamlScalarNode(string.Empty) },
            { "auth", new YamlScalarNode(CamelCase(AuthenticationMethod.Forms)) },
            { "auth_required", new YamlScalarNode(CamelCase(AuthenticationRequired.DisabledForLocalAddresses)) },
            { "api_key", new YamlScalarNode(apiKey) },
        };

        return new YamlStream(new YamlDocument(new YamlMappingNode { { ServerKey, server } }));
    }

    // Enum binding is case-insensitive, so camelCase reads back and stays legible (disabledForLocalAddresses).
    private static string CamelCase(Enum value) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static YamlStream Load(string path)
    {
        var stream = new YamlStream();

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

        return stream;
    }

    private static void Save(YamlStream stream, string path)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        stream.Save(writer, assignAnchors: false);
    }
}
