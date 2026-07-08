namespace Amarin.Tools;

internal sealed class ServiceSnapshotEntry
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StartType { get; set; } = string.Empty;
}

internal sealed class ScheduledTaskSnapshotEntry
{
    public string TaskName { get; set; } = string.Empty;
    public string TaskPath { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

internal sealed class StartupProgramSnapshotEntry
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? Location { get; set; }
}