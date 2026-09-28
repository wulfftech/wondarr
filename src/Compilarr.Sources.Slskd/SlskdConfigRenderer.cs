using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Compilarr.Sources.Slskd;

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
    /// <summary>Address slskd binds; loopback only, because Compilarr proxies everything.</summary>
    public const string LoopbackAddress = "127.0.0.1";

    /// <summary>Name of the API key Compilarr uses to call slskd.</summary>
    public const string ApiKeyName = "compilarr";

    /// <summary>Networks the generated API key is accepted from: the container's own loopback.</summary>
    public const string ApiKeyCidr = "127.0.0.1/32,::1/128";

    // Snake-case keys, and a null Soulseek username or password is left out entirely rather than
    // rendered as `~` (slskd treats an empty credential as "log in as nobody").
    private readonly ISerializer _serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>Renders the complete <c>slskd.yml</c> for <paramref name="options"/>.</summary>
    public string Render(SoulseekOptions options, SlskdRuntimeSecrets secrets)
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
        };

        return _serializer.Serialize(document);
    }

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
}
