namespace Compilarr.Core.Profiles;

/// <summary>
/// Thrown when a quality profile cannot be deleted because a song still uses it, or because it is
/// the last one: a song must always have a profile to be judged against.
/// </summary>
public sealed class ProfileInUseException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ProfileInUseException"/> class.</summary>
    /// <param name="message">Why the profile is still needed.</param>
    public ProfileInUseException(string message)
        : base(message)
    {
    }
}
