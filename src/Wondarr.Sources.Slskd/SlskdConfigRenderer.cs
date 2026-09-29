using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Wondarr.Sources.Slskd;

/// <summary>
/// Renders the app's Soulseek settings into slskd's <c>slskd.yml</c>.
/// </summary>
/// <remarks>
/// The document is built as an object graph and serialised with YamlDotNet, never by string
/// concatenation, so a value containing a colon, a quote or a newline cannot break the file.
/// The generated API key and web password only ever appear here — the file is written with mode
/// 0600 by the caller.
/// </remarks>
public sealed class SlskdConfigRenderer
{
    /// <summary>Address slskd binds; loopback only, because Wondarr proxies everything.</summary>
    public const string LoopbackAddress = "127.0.0.1";

    /// <summary>Name of the API key Wondarr uses to call slskd.</summary>
    public const string ApiKeyName = "wondarr";

    /// <summary>Networks the generated API key is accepted from: the container's own loopback.</summary>
    public const string ApiKeyCidr = "127.0.0.1/32,::1/128";

    // Snake-case keys, and a null Soulseek username or password is left out entirely rather than
    // rendered as `~` (slskd treats an empty credential as "log in as nobody").
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        // slskd reads this on Linux; LF on every platform keeps the output (and the golden files) identical
        .WithNewLine("\n")
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>Name slskd's webhook integration is registered under.</summary>
    public const string WebhookName = "wondarr";

    /// <summary>Header slskd sends Wondarr's generated token in.</summary>
    public const string WebhookHeaderName = "X-Wondarr-Webhook";

    /// <summary>The only event Wondarr wants: one file finished.</summary>
    public const string WebhookEvent = "DownloadFileComplete";

    /// <summary>How long slskd waits for Wondarr before giving up, in milliseconds.</summary>
    public const int WebhookTimeoutMs = 5000;

    /// <summary>How many times slskd re-sends a webhook Wondarr did not accept.</summary>
    public const int WebhookAttempts = 3;

    /// <summary>
    /// Renders the complete <c>slskd.yml</c> for <paramref name="options"/>.
    /// </summary>
    /// <param name="options">The Soulseek settings to render.</param>
    /// <param name="secrets">The generated credentials slskd authenticates with and to Wondarr.</param>
    /// <param name="webhookUrl">
    /// Where slskd should call Wondarr when a file finishes, or <c>null</c> to render no webhook
    /// integration at all (completion is still detected by polling).
    /// </param>
    public string Render(SoulseekOptions options, SlskdRuntimeSecrets secrets, string? webhookUrl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);

        var document = new SlskdDocument
        {
            Headless = true,
            RemoteConfiguration = false,
            Flags = new SlskdFlags
            {
                NoLogo = true,
                NoVersionCheck = true,
                NoConnect = !options.HasCredentials,
            },
            Web = new SlskdWeb
            {
                Port = options.WebPort,
                IpAddress = LoopbackAddress,
                Https = new SlskdHttps { Disabled = true },
                Authentication = new SlskdWebAuthentication
                {
                    Disabled = false,
                    Username = secrets.WebUsername,
                    Password = secrets.WebPassword,
                    ApiKeys = new Dictionary<string, SlskdApiKey>(StringComparer.Ordinal)
                    {
                        [ApiKeyName] = new SlskdApiKey
                        {
                            Key = secrets.ApiKey,
                            Role = "administrator",
                            Cidr = ApiKeyCidr,
                        },
                    },
                },
            },
            Soulseek = new SlskdSoulseek
            {
                Username = string.IsNullOrWhiteSpace(options.Username) ? null : options.Username,
                Password = string.IsNullOrWhiteSpace(options.Password) ? null : options.Password,
                ListenPort = options.ListenPort,
                DistributedNetwork = new SlskdDistributedNetwork { Disabled = !options.DistributedNetwork },
            },
            Directories = new SlskdDirectories
            {
                Downloads = options.DownloadsDir,
                Incomplete = options.IncompleteDir,
            },
            Shares = new SlskdShares
            {
                Directories = options.ShareLibrary ? [.. options.SharedFolders] : [],
            },
            Transfers = new SlskdTransfers
            {
                Upload = new SlskdUpload
                {
                    Slots = options.UploadSlots,
                    SpeedLimit = options.UploadSpeedLimitKib,
                },
            },
            Integrations = webhookUrl is null ? null : Webhook(webhookUrl, secrets.WebhookToken),
        };

        return _serializer.Serialize(document);
    }

    private static SlskdIntegrations Webhook(string url, string? token) => new()
    {
        Webhooks = new Dictionary<string, SlskdWebhook>(StringComparer.Ordinal)
        {
            [WebhookName] = new SlskdWebhook
            {
                On = [WebhookEvent],
                Call = new SlskdWebhookCall
                {
                    Url = url,
                    Headers =
                    [
                        new SlskdWebhookHeader { Name = WebhookHeaderName, Value = token },
                    ],
                },
                Timeout = WebhookTimeoutMs,
                Retry = new SlskdWebhookRetry { Attempts = WebhookAttempts },
            },
        },
    };

    private sealed class SlskdDocument
    {
        public bool Headless { get; set; }

        public bool RemoteConfiguration { get; set; }

        public SlskdFlags Flags { get; set; } = new();

        public SlskdWeb Web { get; set; } = new();

        public SlskdSoulseek Soulseek { get; set; } = new();

        public SlskdDirectories Directories { get; set; } = new();

        public SlskdShares Shares { get; set; } = new();

        public SlskdTransfers Transfers { get; set; } = new();

        public SlskdIntegrations? Integrations { get; set; }
    }

    private sealed class SlskdFlags
    {
        public bool NoLogo { get; set; }

        public bool NoVersionCheck { get; set; }

        public bool NoConnect { get; set; }
    }

    private sealed class SlskdWeb
    {
        public int Port { get; set; }

        public string IpAddress { get; set; } = LoopbackAddress;

        public SlskdHttps Https { get; set; } = new();

        public SlskdWebAuthentication Authentication { get; set; } = new();
    }

    private sealed class SlskdHttps
    {
        public bool Disabled { get; set; }
    }

    private sealed class SlskdWebAuthentication
    {
        public bool Disabled { get; set; }

        public string? Username { get; set; }

        public string? Password { get; set; }

        public Dictionary<string, SlskdApiKey>? ApiKeys { get; set; }
    }

    private sealed class SlskdApiKey
    {
        public string? Key { get; set; }

        public string? Role { get; set; }

        public string? Cidr { get; set; }
    }

    private sealed class SlskdSoulseek
    {
        public string? Username { get; set; }

        public string? Password { get; set; }

        public int ListenPort { get; set; }

        public SlskdDistributedNetwork DistributedNetwork { get; set; } = new();
    }

    private sealed class SlskdDistributedNetwork
    {
        public bool Disabled { get; set; }
    }

    private sealed class SlskdDirectories
    {
        public string? Downloads { get; set; }

        public string? Incomplete { get; set; }
    }

    private sealed class SlskdShares
    {
        public List<string> Directories { get; set; } = [];
    }

    private sealed class SlskdTransfers
    {
        public SlskdUpload Upload { get; set; } = new();
    }

    private sealed class SlskdUpload
    {
        public int Slots { get; set; }

        public int SpeedLimit { get; set; }
    }

    private sealed class SlskdIntegrations
    {
        public Dictionary<string, SlskdWebhook> Webhooks { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class SlskdWebhook
    {
        public List<string> On { get; set; } = [];

        public SlskdWebhookCall Call { get; set; } = new();

        public int Timeout { get; set; }

        public SlskdWebhookRetry Retry { get; set; } = new();
    }

    private sealed class SlskdWebhookCall
    {
        public string? Url { get; set; }

        public List<SlskdWebhookHeader> Headers { get; set; } = [];
    }

    private sealed class SlskdWebhookHeader
    {
        public string? Name { get; set; }

        public string? Value { get; set; }
    }

    private sealed class SlskdWebhookRetry
    {
        public int Attempts { get; set; }
    }
}
