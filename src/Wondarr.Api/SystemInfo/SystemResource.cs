// Ported from Lidarr (https://github.com/Lidarr/Lidarr), src/Lidarr.Api.V1/System/SystemResource.cs, GPL-3.0.
// Adapted for Wondarr: a record, enums flattened to the strings *arr clients expect, and no
// update-mechanism message.

using Wondarr.Core.Configuration;

namespace Wondarr.Api.SystemInfo;

/// <summary>
/// The app-info payload dashboards and *arr clients read to identify the instance. Property names
/// are serialised camelCase, as the *arrs do.
/// </summary>
/// <param name="AppName">Always <c>Wondarr</c>.</param>
/// <param name="InstanceName">The user's name for this instance; <c>Wondarr</c> for now.</param>
/// <param name="Version">The assembly's informational version.</param>
/// <param name="BuildTime">When the running assembly was written to disk (UTC).</param>
/// <param name="IsDebug">Whether this is a debug build.</param>
/// <param name="IsProduction">Whether this is a release build.</param>
/// <param name="IsAdmin">Whether the process runs with administrative rights. Always false for now.</param>
/// <param name="IsUserInteractive">Whether the process can talk to the user.</param>
/// <param name="StartupPath">The directory the app was started from.</param>
/// <param name="AppData">The configuration directory.</param>
/// <param name="OsName">Windows, Linux or macOS.</param>
/// <param name="OsVersion">The OS version string.</param>
/// <param name="IsNetCore">Always true; there is no .NET Framework build.</param>
/// <param name="IsLinux">Whether the app runs on Linux.</param>
/// <param name="IsOsx">Whether the app runs on macOS.</param>
/// <param name="IsWindows">Whether the app runs on Windows.</param>
/// <param name="IsDocker">Whether the app runs in a container.</param>
/// <param name="IsContainerized">Whether the app runs in a container.</param>
/// <param name="Mode">The runtime mode; <c>console</c>, as there is no service wrapper.</param>
/// <param name="Branch">The release branch; <c>develop</c> until there is a stable release.</param>
/// <param name="Authentication">The configured authentication method, serialised camelCase.</param>
/// <param name="DatabaseType">Always <c>sqLite</c>.</param>
/// <param name="DatabaseVersion">The SQLite library version.</param>
/// <param name="MigrationVersion">How many EF Core migrations have been applied.</param>
/// <param name="UrlBase">The configured URL base, empty for the site root.</param>
/// <param name="RuntimeVersion">The .NET runtime version.</param>
/// <param name="RuntimeName">Always <c>netcore</c>.</param>
/// <param name="StartTime">When the process started (UTC).</param>
/// <param name="PackageVersion">The version of the deployed package.</param>
/// <param name="PackageAuthor">The package author.</param>
/// <param name="PackageUpdateMechanism"><c>docker</c> in a container, otherwise <c>builtIn</c>.</param>
public sealed record SystemResource(
    string AppName,
    string InstanceName,
    string Version,
    DateTime BuildTime,
    bool IsDebug,
    bool IsProduction,
    bool IsAdmin,
    bool IsUserInteractive,
    string StartupPath,
    string AppData,
    string OsName,
    string OsVersion,
    bool IsNetCore,
    bool IsLinux,
    bool IsOsx,
    bool IsWindows,
    bool IsDocker,
    bool IsContainerized,
    string Mode,
    string Branch,
    AuthenticationMethod Authentication,
    string DatabaseType,
    string DatabaseVersion,
    int MigrationVersion,
    string UrlBase,
    string RuntimeVersion,
    string RuntimeName,
    DateTime StartTime,
    string PackageVersion,
    string PackageAuthor,
    string PackageUpdateMechanism);
