namespace Wondarr.Api.Authentication;

/// <summary>The outcome of <see cref="LocalCallerRules.Evaluate"/>: local, or which kind of rule failed.</summary>
public enum LocalCallerVerdict
{
    /// <summary>All three rules hold: the caller is on the local network and used a name Wondarr trusts.</summary>
    Local,

    /// <summary>The caller is not on a local network: a proxy header is present, or the socket address is not local.</summary>
    NotOnLocalNetwork,

    /// <summary>The socket address is local but the <c>Host</c> header is not a trusted name (R-009).</summary>
    HostNotTrusted,
}
