namespace Tysl.Inspection.Desktop.Domain.Models;

public enum GroupSyncFailureKind
{
    None = 0,
    GetGroupListFailed = 1,
    GetGroupDeviceListFailed = 2,
    DatabaseWriteFailed = 3
}

public sealed record GroupSyncFailure(
    GroupSyncFailureKind FailureKind,
    string? GroupId,
    string? GroupName,
    string Message);

public sealed record GroupSyncSummary(
    int GroupCount,
    int DeviceCount,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<GroupSyncFailure> Failures)
{
    public bool IsSuccess => FailureCount == 0;
}
