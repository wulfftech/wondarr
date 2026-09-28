// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/System/SystemController.cs, GPL-3.0.
// Adapted for Compilarr: the status action only, with Compilarr's own build, host and database facts.

using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Compilarr.Core.Configuration;
using Compilarr.Core.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compilarr.Api.SystemInfo;

/// <summary>The endpoint dashboards and *arr clients probe for the app's identity and version.</summary>
[ApiController]
[Route("api/v1/system")]
public sealed class SystemController : ControllerBase
{
    private readonly CompilarrPaths _paths;
    private readonly CompilarrDbContext _context;
    private readonly ServerOptions _server;

    /// <summary>Initialises a new instance of the <see cref="SystemController"/> class.</summary>
    public SystemController(
        CompilarrPaths paths,
        CompilarrDbContext context,
        IOptions<ServerOptions> server)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(server);

        _paths = paths;
        _context = context;
        _server = server.Value;
    }

    /// <summary>Reports the running app, its host and its database.</summary>
    [HttpGet("status")]
    [Produces("application/json")]
    public async Task<ActionResult<SystemResource>> GetStatus(CancellationToken cancellationToken)
    {
        var assembly = typeof(SystemController).Assembly;
        var version = InformationalVersion(assembly);
        var containerized = IsContainerized();

        return Ok(new SystemResource(
            AppName: "Compilarr",
            InstanceName: "Compilarr",
            Version: version,
            BuildTime: BuildTime(assembly),
            IsDebug: IsDebugBuild,
            IsProduction: !IsDebugBuild,
            IsAdmin: false,
            IsUserInteractive: Environment.UserInteractive,
            StartupPath: AppContext.BaseDirectory,
            AppData: _paths.ConfigDir,
            OsName: OsName(),
            OsVersion: Environment.OSVersion.VersionString,
            IsNetCore: true,
            IsLinux: OperatingSystem.IsLinux(),
            IsOsx: OperatingSystem.IsMacOS(),
            IsWindows: OperatingSystem.IsWindows(),
            IsDocker: containerized,
            IsContainerized: containerized,
            Mode: "console",
            Branch: "develop",
            Authentication: _server.Auth,
            DatabaseType: "sqLite",
            DatabaseVersion: await GetDatabaseVersionAsync(cancellationToken).ConfigureAwait(false),
            MigrationVersion: (await _context.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count(),
            UrlBase: _server.UrlBase,
            RuntimeVersion: Environment.Version.ToString(),
            RuntimeName: "netcore",
            StartTime: ProcessStartTime(),
            PackageVersion: version,
            PackageAuthor: "Compilarr contributors",
            PackageUpdateMechanism: containerized ? "docker" : "builtIn"));
    }

    private const bool IsDebugBuild =
#if DEBUG
        true;
#else
        false;
#endif

    private static string InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Deterministic builds keep the assembly's write time, which is the closest thing to a build stamp.</summary>
    private static DateTime BuildTime(Assembly assembly)
    {
        var location = assembly.Location;

        return string.IsNullOrEmpty(location) || !global::System.IO.File.Exists(location)
            ? DateTime.UnixEpoch
            : global::System.IO.File.GetLastWriteTimeUtc(location);
    }

    private static string OsName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }

        return OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription;
    }

    private static bool IsContainerized() =>
        global::System.IO.File.Exists("/.dockerenv")
        || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    private static DateTime ProcessStartTime()
    {
        using var process = Process.GetCurrentProcess();

        return process.StartTime.ToUniversalTime();
    }

    private async Task<string> GetDatabaseVersionAsync(CancellationToken cancellationToken)
    {
        var connection = _context.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;

        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sqlite_version();";

            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string ?? "unknown";
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}
