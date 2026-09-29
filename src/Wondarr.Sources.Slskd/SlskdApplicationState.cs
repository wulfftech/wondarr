namespace Wondarr.Sources.Slskd;

/// <summary>
/// The subset of slskd's <c>GET /api/v0/application</c> response the supervisor needs. slskd
/// serialises camelCase, which <see cref="SlskdClient"/> matches with the web JSON defaults.
/// </summary>
public sealed record SlskdApplicationState
{
    /// <summary>Version information about the running slskd.</summary>
    public SlskdVersion Version { get; init; } = new();

    /// <summary>Connection state of the Soulseek server.</summary>
    public SlskdServer Server { get; init; } = new();

    /// <summary>Whether slskd is waiting to be restarted.</summary>
    public bool PendingRestart { get; init; }

    /// <summary>Whether slskd is waiting to reconnect to the Soulseek server.</summary>
    public bool PendingReconnect { get; init; }

    /// <summary>The logged-in Soulseek user.</summary>
    public SlskdUser User { get; init; } = new();

    /// <summary>What slskd has scanned and is sharing.</summary>
    public SlskdShares Shares { get; init; } = new();
}

/// <summary>The <c>version</c> object of slskd's application state.</summary>
public sealed record SlskdVersion
{
    /// <summary>Version string of the running slskd, for example <c>0.26.0.0</c>.</summary>
    public string Current { get; init; } = string.Empty;
}

/// <summary>The <c>server</c> object of slskd's application state.</summary>
public sealed record SlskdServer
{
    /// <summary>Whether the Soulseek account is logged in.</summary>
    public bool IsLoggedIn { get; init; }

    /// <summary>Whether the Soulseek server connection is up.</summary>
    public bool IsConnected { get; init; }

    /// <summary>Comma-separated state flags, for example <c>Connected, LoggedIn</c>.</summary>
    public string State { get; init; } = string.Empty;
}

/// <summary>The <c>user</c> object of slskd's application state.</summary>
public sealed record SlskdUser
{
    /// <summary>The Soulseek username slskd is logged in as.</summary>
    public string Username { get; init; } = string.Empty;
}

/// <summary>The <c>shares</c> object of slskd's application state.</summary>
public sealed record SlskdShares
{
    /// <summary>How many directories slskd shares.</summary>
    public int Directories { get; init; }

    /// <summary>How many files slskd shares.</summary>
    public int Files { get; init; }

    /// <summary>Whether the share scan has finished at least once.</summary>
    public bool Ready { get; init; }

    /// <summary>Whether a share scan is running right now.</summary>
    public bool Scanning { get; init; }
}
