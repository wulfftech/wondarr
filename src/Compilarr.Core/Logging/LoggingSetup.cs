using Compilarr.Core.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Templates;

namespace Compilarr.Core.Logging;

/// <summary>
/// Builds the Serilog pipeline: one JSON object per line, to the console and to rolling files,
/// with every sink behind <see cref="RedactingTextFormatter"/>.
/// </summary>
public static class LoggingSetup
{
    /// <summary>Property names are part of the API: <c>timestamp</c>, <c>level</c>, <c>message</c>, <c>exception</c>, <c>props</c>.</summary>
    public const string JsonTemplate = "{ {timestamp: @t, level: @l, message: @m, exception: @x, props: rest()} }\n";

    /// <summary>Console rendering for humans: <c>[12:34:56 INF] Context: message</c>.</summary>
    public const string TextTemplate = "[{@t:HH:mm:ss} {@l:u3}] {SourceContext}: {@m}\n{@x}";

    /// <summary>The rolling file names, for example <c>compilarr-20260928.json</c>.</summary>
    public const string FileNameTemplate = "compilarr-.json";

    /// <summary>
    /// Applies the Compilarr pipeline to <paramref name="loggerConfiguration"/>.
    /// </summary>
    public static LoggerConfiguration Configure(
        LoggerConfiguration loggerConfiguration,
        LogOptions options,
        CompilarrPaths paths,
        ISecretRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(registry);

        var jsonFormatter = new RedactingTextFormatter(new ExpressionTemplate(JsonTemplate), registry);
        var consoleFormatter = options.ConsoleFormat == LogConsoleFormat.Json
            ? jsonFormatter
            : new RedactingTextFormatter(new ExpressionTemplate(TextTemplate), registry);

        return loggerConfiguration
            .MinimumLevel.Is(ParseLevel(options.Level))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(consoleFormatter)
            .WriteTo.File(
                formatter: jsonFormatter,
                path: System.IO.Path.Combine(paths.LogsDir, FileNameTemplate),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: options.FileSizeLimitMb * 1024L * 1024L,
                retainedFileCountLimit: options.RetainedFiles,
                shared: true);
    }

    /// <summary>
    /// Registers the API key immediately and again whenever <see cref="ServerOptions"/> changes, so
    /// a key edited through the API never reaches a sink.
    /// </summary>
    public static void RegisterServerApiKey(ISecretRegistry registry, IOptionsMonitor<ServerOptions> monitor)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(monitor);

        registry.Register(monitor.CurrentValue.ApiKey);
        monitor.OnChange(options => registry.Register(options.ApiKey));
    }

    private static LogEventLevel ParseLevel(string level) =>
        Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogEventLevel.Information;
}
