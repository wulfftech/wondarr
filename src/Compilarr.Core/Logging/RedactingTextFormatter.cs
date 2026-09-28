using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;

namespace Compilarr.Core.Logging;

/// <summary>
/// Renders an event with an inner formatter and then redacts the rendered text, so message text,
/// property values and exceptions are all covered — whatever the inner format happens to be.
/// </summary>
public sealed class RedactingTextFormatter(ITextFormatter inner, ISecretRegistry registry) : ITextFormatter
{
    private readonly ITextFormatter _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ISecretRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);

        output.Write(_registry.Redact(buffer.ToString()));
    }
}
