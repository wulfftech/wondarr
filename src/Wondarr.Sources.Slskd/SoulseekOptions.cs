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
    /// Folders shared back to the network. The default is the seeded default library's root
    /// (<c>SeedData</c>), so "share my library" shares the library out of the box.
    /// </summary>
    public List<string> SharedFolders { get; set; } = ["/data/music"];

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

    /// <summary>
    /// Whether a Soulseek account is configured. Without one slskd must start with
    /// <c>no_connect</c> so it does not retry a login it cannot make.
    /// </summary>
    public bool HasCredentials =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
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

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
