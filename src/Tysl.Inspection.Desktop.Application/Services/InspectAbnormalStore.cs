using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class InspectAbnormalStore(
    ILogger<InspectAbnormalStore> logger) : IInspectAbnormalStore
{
    private readonly List<InspectAbnormalItem> items = [];

    public IReadOnlyList<InspectAbnormalItem> GetItems()
    {
        return items.ToArray();
    }

    public InspectAbnormalItem? Add(InspectResult result)
    {
        if (!CanAdd(result))
        {
            return null;
        }

        var item = new InspectAbnormalItem(
            Guid.NewGuid(),
            result.InspectAt,
            result.DeviceName,
            result.DeviceCode,
            result.Conclusion,
            result.AbnormalClassText,
            result.BuildDispositionSummary(),
            false);

        items.Insert(0, item);

        logger.LogInformation(
            "Inspect abnormal added to session list for {DeviceCode}. AbnormalClass={AbnormalClass}.",
            item.DeviceCode,
            item.AbnormalClassText);

        return item;
    }

    public InspectAbnormalItem? ToggleReviewed(Guid id)
    {
        var index = items.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return null;
        }

        var updated = items[index] with { IsReviewed = !items[index].IsReviewed };
        items[index] = updated;

        logger.LogInformation(
            "Inspect abnormal reviewed state toggled for {DeviceCode}. IsReviewed={IsReviewed}.",
            updated.DeviceCode,
            updated.IsReviewed);

        return updated;
    }

    private static bool CanAdd(InspectResult result)
    {
        return result.AbnormalClass is InspectAbnormalClass.Offline
            or InspectAbnormalClass.RtspNotReady
            or InspectAbnormalClass.PlayFailed;
    }
}
