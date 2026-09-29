using Microsoft.Extensions.Options;

namespace Wondarr.Sources.Slskd;

/// <summary>Whether Wondarr runs its own slskd or talks to one the user already runs.</summary>
public enum SoulseekMode
{
    /// <summary>slskd ships inside the image and runs as a supervised child process.</summary>
    Bundled,

    /// <summary>The user runs slskd elsewhere; Wondarr only talks to its API.</summary>
    External,
}

/// <summary>
/// The <c>soulseek</c> section of <c>config.yml</c>. Everything the bundled slskd needs is rendered
/// from these values into <c>slskd.yml</c> by <see cref="SlskdConfigRenderer"/>.
/// </summary>
public sealed class SoulseekOptions
{
    /// <summary>Bundled slskd (the default) or an external instance.</summary>
    public SoulseekMode Mode { get; set; } = SoulseekMode.Bundled;

    /// <summary>Dedicated Soulseek account name. Absent means "not configured yet".</summary>
    public string? Username { get; set; }

    /// <summary>Dedicated Soulseek password. Never logged or echoed in a validation message.</summary>
    public string? Password { get; set; }

    /// <summary>Port slskd listens on for peer connections.</summary>
    public int ListenPort { get; set; } = 50300;

    /// <summary>"Share my library" toggle. Turning it off risks a leech ban.</summary>
    public bool ShareLibrary { get; set; } = true;

    /// <summary>
    /// Folders shared back to the network. The default (the seeded default library's root,
    /// <c>SeedData</c>) is applied by <see cref="SoulseekOptionsPostConfigure"/> rather than here:
    /// the binder <em>appends</em> bound items to whatever the property already holds, so a default
    /// in the property would turn <c>shared_folders: [/x]</c> into two folders and make an explicit
    /// empty list impossible.
    /// </summary>
    public List<string> SharedFolders { get; set; } = [];

    /// <summary>Concurrent upload slots.</summary>
    public int UploadSlots { get; set; } = 10;

    /// <summary>Upload speed limit in KiB/s; <c>0</c> means unlimited.</summary>
    public int UploadSpeedLimitKib { get; set; } = 2000;

    /// <summary>Whether slskd joins the distributed network.</summary>
    public bool DistributedNetwork { get; set; } = true;

    /// <summary>Directory slskd downloads completed transfers into.</summary>
    public string DownloadsDir { get; set; } = "/data/downloads/slskd";

    /// <summary>Directory slskd keeps partial transfers in.</summary>
    public string IncompleteDir { get; set; } = "/data/downloads/slskd/incomplete";

    /// <summary>Path of the bundled slskd executable.</summary>
    public string BinaryPath { get; set; } = "/opt/slskd/slskd";

    /// <summary>Loopback port slskd serves its own API and UI on.</summary>
    public int WebPort { get; set; } = 5030;

    /// <summary>How Wondarr searches the network: the parameters and the budget it searches under.</summary>
    public SoulseekSearchOptions Search { get; set; } = new();

    /// <summary>
    /// Whether a Soulseek account is configured. Without one slskd must start with
    /// <c>no_connect</c> so it does not retry a login it cannot make.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}

/// <summary>
/// The <c>soulseek.search</c> section: how one search is parameterised, and the budget every
/// submission passes. The budget defaults are the network's own limits and the validator holds the
/// user to them, so they cannot be configured above what Soulseek allows.
/// </summary>
public sealed class SoulseekSearchOptions
{
    /// <summary>Searches allowed inside <see cref="WindowSeconds"/>.</summary>
    public int MaxSearches { get; set; } = 30;

    /// <summary>Length of the rolling window the submission log covers, in seconds.</summary>
    public int WindowSeconds { get; set; } = 240;

    /// <summary>Searches allowed to be in flight at once.</summary>
    public int MaxOutstanding { get; set; } = 2;

    /// <summary>Minimum distance between two submissions, in seconds.</summary>
    public int MinSpacingSeconds { get; set; } = 5;

    /// <summary>How long peers may take to answer, in milliseconds.</summary>
    public int SearchTimeoutMs { get; set; } = 8000;

    /// <summary>Stop after this many peer responses.</summary>
    public int ResponseLimit { get; set; } = 100;

    /// <summary>Stop after this many files across all responses.</summary>
    public int FileLimit { get; set; } = 2000;

    /// <summary>Peers slower than this (bytes per second) are not counted.</summary>
    public int MinimumPeerUploadSpeed { get; set; } = 1;

    /// <summary>Wondarr's own limit on one search, in seconds; slskd's timeout is unrelated to it.</summary>
    public int WallClockSeconds { get; set; } = 30;

    /// <summary>How often a running search is polled for its state, in milliseconds.</summary>
    public int PollIntervalMs { get; set; } = 500;
}

/// <summary>
/// Validates <see cref="SoulseekOptions"/>. Every failure message starts with the YAML key so the
/// user can find the offending line in <c>config.yml</c>.
/// </summary>
public sealed class SoulseekOptionsValidator : IValidateOptions<SoulseekOptions>
{
    public ValidateOptionsResult Validate(string? name, SoulseekOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!Enum.IsDefined(options.Mode))
        {
            failures.Add($"soulseek.mode: must be bundled or external (was {(int)options.Mode})");
        }

        if (options.ListenPort is < 1024 or > 65535)
        {
            failures.Add($"soulseek.listen_port: must be between 1024 and 65535 (was {options.ListenPort})");
        }

        if (options.WebPort is < 1 or > 65535)
        {
            failures.Add($"soulseek.web_port: must be between 1 and 65535 (was {options.WebPort})");
        }

        if (options.UploadSlots is < 1 or > 100)
        {
            failures.Add($"soulseek.upload_slots: must be between 1 and 100 (was {options.UploadSlots})");
        }

        if (options.UploadSpeedLimitKib < 0)
        {
            failures.Add($"soulseek.upload_speed_limit_kib: must be 0 (unlimited) or greater (was {options.UploadSpeedLimitKib})");
        }

        // The password itself is never echoed: this message ends up in logs.
        if (!string.IsNullOrEmpty(options.Password) && string.IsNullOrWhiteSpace(options.Username))
        {
            failures.Add("soulseek.username: must be set when soulseek.password is set");
        }

        ValidateSearch(options.Search, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Checks the search budget against the limits Soulseek itself publishes. The maxima are the
    /// network's rules, not tuning knobs: exceeding them gets the account banned.
    /// </summary>
    private static void ValidateSearch(SoulseekSearchOptions search, List<string> failures)
    {
        if (search.MaxSearches is < 1 or > 30)
        {
            failures.Add($"soulseek.search.max_searches: must be between 1 and 30 (was {search.MaxSearches})");
        }

        if (search.WindowSeconds < 240)
        {
            failures.Add($"soulseek.search.window_seconds: must be at least 240 (was {search.WindowSeconds})");
        }

        if (search.MaxOutstanding is < 1 or > 2)
        {
            failures.Add($"soulseek.search.max_outstanding: must be between 1 and 2 (was {search.MaxOutstanding})");
        }

        if (search.MinSpacingSeconds < 5)
        {
            failures.Add($"soulseek.search.min_spacing_seconds: must be at least 5 (was {search.MinSpacingSeconds})");
        }

        if (search.SearchTimeoutMs is < 5000 or > 30000)
        {
            failures.Add($"soulseek.search.search_timeout_ms: must be between 5000 and 30000 (was {search.SearchTimeoutMs})");
        }

        if (search.ResponseLimit is < 1 or > 500)
        {
            failures.Add($"soulseek.search.response_limit: must be between 1 and 500 (was {search.ResponseLimit})");
        }

        if (search.FileLimit is < 1 or > 10000)
        {
            failures.Add($"soulseek.search.file_limit: must be between 1 and 10000 (was {search.FileLimit})");
        }

        if (search.WallClockSeconds is < 10 or > 120)
        {
            failures.Add($"soulseek.search.wall_clock_seconds: must be between 10 and 120 (was {search.WallClockSeconds})");
        }

        if (search.PollIntervalMs is < 100 or > 5000)
        {
            failures.Add($"soulseek.search.poll_interval_ms: must be between 100 and 5000 (was {search.PollIntervalMs})");
        }
    }
}
