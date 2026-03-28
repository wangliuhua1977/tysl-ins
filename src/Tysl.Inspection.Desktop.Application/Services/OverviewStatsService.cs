using Tysl.Inspection.Desktop.Application.Abstractions;
using Tysl.Inspection.Desktop.Domain.Models;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class OverviewStatsService(IGroupSyncStore groupSyncStore) : IOverviewStatsService
{
    public Task<OverviewStats> GetAsync(CancellationToken cancellationToken)
    {
        return groupSyncStore.GetOverviewStatsAsync(cancellationToken);
    }
}
