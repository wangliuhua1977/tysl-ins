namespace Tysl.Inspection.Desktop.Domain.Models;

public enum GroupSyncFailureKind
{
    None = 0,
    GetRegionListFailed = 1,
    GetRegionDeviceListFailed = 2,
    GetDeviceCountReconciliationFailed = 3,
    DatabaseWriteFailed = 4
}

public sealed record GroupSyncFailure(
    GroupSyncFailureKind FailureKind,
    string? GroupId,
    string? GroupName,
    string Message);

public sealed record GroupSyncSnapshotMetadata(
    int PlatformGroupCount,
    int PlatformDeviceCount,
    bool ReconciliationCompleted,
    bool ReconciliationMatched,
    int ReconciledRegionCount,
    int ReconciledDeviceCount,
    int ReconciledOnlineCount,
    string ReconciliationScopeText)
{
    public static GroupSyncSnapshotMetadata Empty { get; } =
        new(0, 0, false, false, 0, 0, 0, string.Empty);
}

public sealed record GroupSyncSummary(
    int GroupCount,
    int DeviceCount,
    int SuccessCount,
    int FailureCount,
    DateTimeOffset? LastSyncedAt,
    IReadOnlyList<GroupSyncFailure> Failures,
    bool SnapshotReplaced,
    GroupSyncSnapshotMetadata Metadata)
{
    public bool IsSuccess => SnapshotReplaced && FailureCount == 0;
}
