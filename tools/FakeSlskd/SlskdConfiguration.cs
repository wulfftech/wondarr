using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace FakeSlskd;

/// <summary>One configured webhook target (<c>integrations.webhooks.&lt;name&gt;</c>).</summary>
/// <param name="Name">The webhook's key in the configuration, for logs.</param>
/// <param name="Url">Where the event is posted.</param>
/// <param name="On">The event names this webhook subscribes to.</param>
/// <param name="Headers">Headers sent with the request.</param>
public sealed record WebhookTarget(
    string Name,
    string Url,
    IReadOnlyList<string> On,
    IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Whether this webhook wants <paramref name="eventName"/>.</summary>
    public bool Listens(string eventName) =>
        On.Any(name => string.Equals(name, eventName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Any", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The parts of the app's rendered <c>slskd.yml</c> the fake has to honour: where to listen, which API
/// keys authenticate callers, the Soulseek username it claims to be logged in as, the download and
/// incomplete directories it writes into, the directories it claims to share, and the webhooks it
/// calls back.
/// </summary>
public sealed record SlskdConfiguration
{
    /// <summary>Default HTTP port, matching slskd's own.</summary>
    public const int DefaultPort = 5030;

    /// <summary>Port <c>web.port</c> asks the fake to listen on.</summary>
    public int WebPort { get; init; } = DefaultPort;

    /// <summary>Address <c>web.ip_address</c> asks the fake to listen on.</summary>
    public string WebIpAddress { get; init; } = "127.0.0.1";

    /// <summary><c>web.authentication.disabled</c>: when true no API key is required.</summary>
    public bool AuthenticationDisabled { get; init; }

    /// <summary>Every key in <c>web.authentication.api_keys.*.key</c>.</summary>
    public IReadOnlyList<string> ApiKeys { get; init; } = [];

    /// <summary><c>soulseek.username</c>; the fake reports itself logged out when it is empty.</summary>
    public string? SoulseekUsername { get; init; }

    /// <summary><c>directories.downloads</c>: finished files are moved here.</summary>
    public string DownloadsDirectory { get; init; } = "downloads";

    /// <summary><c>directories.incomplete</c>: files are generated here before they are moved.</summary>
    public string IncompleteDirectory { get; init; } = "incomplete";

    /// <summary><c>shares.directories</c>: what <c>/api/v0/shares</c> reports.</summary>
    public IReadOnlyList<string> ShareDirectories { get; init; } = [];

    /// <summary><c>integrations.webhooks</c>.</summary>
    public IReadOnlyList<WebhookTarget> Webhooks { get; init; } = [];

    /// <summary>Reads a <c>slskd.yml</c> from disk.</summary>
    /// <param name="path">Path of the configuration file.</param>
    public static SlskdConfiguration Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return FromYaml(File.ReadAllText(path));
    }

    /// <summary>Reads a <c>slskd.yml</c> document.</summary>
    /// <param name="yaml">The configuration document.</param>
    public static SlskdConfiguration FromYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var document = deserializer.Deserialize<SlskdYaml>(yaml) ?? new SlskdYaml();

        return new SlskdConfiguration
        {
            WebPort = document.Web.Port,
            WebIpAddress = string.IsNullOrWhiteSpace(document.Web.IpAddress) ? "127.0.0.1" : document.Web.IpAddress,
            AuthenticationDisabled = document.Web.Authentication.Disabled,
            ApiKeys = document.Web.Authentication.ApiKeys.Values
                .Select(entry => entry.Key)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToArray(),
            SoulseekUsername = string.IsNullOrWhiteSpace(document.Soulseek.Username) ? null : document.Soulseek.Username,
            DownloadsDirectory = document.Directories.Downloads,
            IncompleteDirectory = document.Directories.Incomplete,
            ShareDirectories = document.Shares.Directories,
            Webhooks = ReadWebhooks(document.Integrations.Webhooks),
        };
    }

    private static WebhookTarget[] ReadWebhooks(Dictionary<string, WebhookYaml> webhooks) =>
        webhooks
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value.Call.Url))
            .Select(entry => new WebhookTarget(
                entry.Key,
                entry.Value.Call.Url,
                entry.Value.On,
                ReadHeaders(entry.Value.Call.Headers)))
            .ToArray();

    /// <summary>
    /// Reads <c>call.headers</c>, which slskd documents as a mapping but which some samples write as a
    /// list of <c>{name, value}</c> pairs. Both are accepted.
    /// </summary>
    private static Dictionary<string, string> ReadHeaders(object? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        switch (headers)
        {
            case IDictionary<object, object> mapping:
                foreach (var (key, value) in mapping)
                {
                    if (key is not null && value is not null)
                    {
                        result[Convert.ToString(key, CultureInfo.InvariantCulture) ?? string.Empty] =
                            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }

                break;

            case IEnumerable<object> sequence:
                foreach (var item in sequence)
                {
                    if (item is IDictionary<object, object> pair
                        && pair.TryGetValue("name", out var name)
                        && pair.TryGetValue("value", out var value)
                        && name is not null)
                    {
                        result[Convert.ToString(name, CultureInfo.InvariantCulture) ?? string.Empty] =
                            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                    }
                }

                break;

            default:
                break;
        }

        return result;
    }

    private sealed class SlskdYaml
    {
        public WebYaml Web { get; set; } = new();

        public SoulseekYaml Soulseek { get; set; } = new();

        public DirectoriesYaml Directories { get; set; } = new();

        public SharesYaml Shares { get; set; } = new();

        public IntegrationsYaml Integrations { get; set; } = new();
    }

    private sealed class WebYaml
    {
        public int Port { get; set; } = DefaultPort;

        public string IpAddress { get; set; } = "127.0.0.1";

        public AuthenticationYaml Authentication { get; set; } = new();
    }

    private sealed class AuthenticationYaml
    {
        public bool Disabled { get; set; }

        public Dictionary<string, ApiKeyYaml> ApiKeys { get; set; } = [];
    }

    private sealed class ApiKeyYaml
    {
        public string Key { get; set; } = string.Empty;
    }

    private sealed class SoulseekYaml
    {
        public string Username { get; set; } = string.Empty;
    }

    private sealed class DirectoriesYaml
    {
        public string Downloads { get; set; } = "downloads";

        public string Incomplete { get; set; } = "incomplete";
    }

    private sealed class SharesYaml
    {
        public List<string> Directories { get; set; } = [];
    }

    private sealed class IntegrationsYaml
    {
        public Dictionary<string, WebhookYaml> Webhooks { get; set; } = [];
    }

    private sealed class WebhookYaml
    {
        public List<string> On { get; set; } = [];

        public WebhookCallYaml Call { get; set; } = new();
    }

    private sealed class WebhookCallYaml
    {
        public string Url { get; set; } = string.Empty;

        public object? Headers { get; set; }
    }
}