namespace Wondarr.Core.Notifications;

/// <summary>
/// A notification could not be stored: the values the user sent break one of the rules the API
/// reports as a <c>400</c>.
/// </summary>
public sealed class NotificationValidationException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="NotificationValidationException"/> class.</summary>
    /// <param name="field">The resource field the message belongs to, for example <c>name</c>.</param>
    /// <param name="detail">The sentence to show the user.</param>
    public NotificationValidationException(string field, string detail)
        : base(detail) => Field = field;

    /// <summary>Gets the field the message belongs to.</summary>
    public string Field { get; }

    /// <summary>Gets the sentence to show the user.</summary>
    public string Detail => Message;
}

/// <summary>
/// A provider could not send. The message never names the endpoint, its headers or its credentials:
/// a webhook URL is a secret and lands in log files and API responses.
/// </summary>
public sealed class NotificationSendException : Exception
{
    /// <summary>Initialises a new instance of the <see cref="NotificationSendException"/> class.</summary>
    /// <param name="message">A sentence that names no endpoint secret.</param>
    public NotificationSendException(string message)
        : base(message)
    {
    }

    /// <summary>Initialises a new instance of the <see cref="NotificationSendException"/> class.</summary>
    /// <param name="message">A sentence that names no endpoint secret.</param>
    /// <param name="innerException">What the transport threw; its message is not surfaced.</param>
    public NotificationSendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
