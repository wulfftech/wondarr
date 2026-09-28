namespace Compilarr.Core.Profiles;

/// <summary>
/// Thrown when a quality profile or a library fails validation. Every problem is carried as a
/// (property, message) pair whose property names are the API resource field names
/// (<c>name</c>, <c>items</c>, <c>cutoff</c>, <c>rootPath</c>, …), so a controller turns the list
/// straight into an RFC 7807 validation problem without translating anything.
/// </summary>
public sealed class ProfileValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="ProfileValidationException"/> class.</summary>
    /// <param name="errors">The (property, message) pairs; at least one.</param>
    public ProfileValidationException(IReadOnlyList<(string Property, string Message)> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors;
    }

    /// <summary>Gets the validation problems, in the order they were found.</summary>
    public IReadOnlyList<(string Property, string Message)> Errors { get; }

    private static string BuildMessage(IReadOnlyList<(string Property, string Message)> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return string.Join(
            " ",
            errors.Select(error => $"{error.Property}: {error.Message}"));
    }
}