namespace Compilarr.Api.SystemInfo;

/// <summary>
/// A scheduled task as the *arr clients expect it. The field names match Lidarr's
/// <c>TaskResource</c> (<c>src/Lidarr.Api.V1/System/Tasks/TaskResource.cs</c>).
/// </summary>
/// <param name="Id">The <c>job</c> row id.</param>
/// <param name="Name">The display name, for example <c>Check Health</c>.</param>
/// <param name="TaskName">The command name, for example <c>CheckHealth</c>.</param>
/// <param name="Interval">Minutes between runs.</param>
/// <param name="LastExecution">When the task last finished, or <see langword="null"/> if it never has.</param>
/// <param name="LastStartTime">When it last started; the same instant as <paramref name="LastExecution"/> for now.</param>
/// <param name="NextExecution">When it is next due, or <see langword="null"/>.</param>
/// <param name="LastDuration">How long the last run took; <c>00:00:00</c> until run times are recorded.</param>
/// <param name="LastResult">A short description of the last outcome, or <see langword="null"/>.</param>
public sealed record TaskResource(
    long Id,
    string Name,
    string TaskName,
    int Interval,
    DateTime? LastExecution,
    DateTime? LastStartTime,
    DateTime? NextExecution,
    string LastDuration,
    string? LastResult);
