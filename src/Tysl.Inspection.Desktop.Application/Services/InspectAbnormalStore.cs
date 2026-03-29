using Microsoft.Extensions.Logging;
using Tysl.Inspection.Desktop.Application.Abstractions;

namespace Tysl.Inspection.Desktop.Application.Services;

public sealed class InspectAbnormalStore(
    IInspectAbnormalPoolStore abnormalPoolStore,
    ILogger<InspectAbnormalStore> logger) : IInspectAbnormalStore
{
    private readonly List<InspectAbnormalItem> items = [];
    private bool hasLoaded;

    public IReadOnlyList<InspectAbnormalItem> GetItems()
    {
        EnsureLoaded();
        return items.ToArray();
    }

    public InspectAbnormalItem? Add(InspectResult result)
    {
        if (!CanAdd(result))
        {
            return null;
        }

        EnsureLoaded();

        var index = FindIndex(result.DeviceCode, result.AbnormalClass, result.Conclusion);
        if (index >= 0)
        {
            var current = items[index];
            var updated = current with
            {
                InspectAt = result.InspectAt,
                DeviceName = result.DeviceName,
                AbnormalClass = result.AbnormalClass,
                AbnormalClassText = result.AbnormalClassText,
                Conclusion = result.Conclusion,
                FailureCategory = result.FailureCategory,
                SummaryText = result.BuildDispositionSummary(),
                UpdatedAt = result.InspectAt
            };

            TryPersist(updated);
            items.RemoveAt(index);
            items.Insert(0, updated);

            logger.LogInformation(
                "Inspect abnormal hit dedup and updated abnormal pool for {DeviceCode}. AbnormalClass={AbnormalClass}, Conclusion={Conclusion}.",
                updated.DeviceCode,
                updated.AbnormalClassText,
                updated.Conclusion);

            return updated;
        }

        var item = new InspectAbnormalItem(
            Guid.NewGuid(),
            result.InspectAt,
            result.DeviceName,
            result.DeviceCode,
            result.AbnormalClass,
            result.AbnormalClassText,
            result.Conclusion,
            result.FailureCategory,
            result.BuildDispositionSummary(),
            false,
            result.InspectAt);

        TryPersist(item);
        items.Insert(0, item);

        logger.LogInformation(
            "Inspect abnormal written to abnormal pool for {DeviceCode}. AbnormalClass={AbnormalClass}.",
            item.DeviceCode,
            item.AbnormalClassText);

        return item;
    }

    public InspectAbnormalItem? ToggleReviewed(Guid id)
    {
        EnsureLoaded();

        var index = items.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return null;
        }

        var updated = items[index] with
        {
            IsReviewed = !items[index].IsReviewed,
            UpdatedAt = DateTimeOffset.Now
        };

        TryPersist(updated);
        items[index] = updated;

        logger.LogInformation(
            "Inspect abnormal reviewed state persisted for {DeviceCode}. IsReviewed={IsReviewed}.",
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

    private void EnsureLoaded()
    {
        if (hasLoaded)
        {
            return;
        }

        hasLoaded = true;

        try
        {
            items.Clear();
            items.AddRange(abnormalPoolStore.LoadItems());

            logger.LogInformation(
                "Inspect abnormal pool restored from SQLite. Count={Count}.",
                items.Count);
        }
        catch (Exception exception)
        {
            items.Clear();
            logger.LogError(exception, "Failed to restore inspect abnormal pool from SQLite.");
        }
    }

    private void TryPersist(InspectAbnormalItem item)
    {
        try
        {
            abnormalPoolStore.Upsert(item);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to persist inspect abnormal pool item for {DeviceCode}.",
                item.DeviceCode);
        }
    }

    private int FindIndex(string deviceCode, InspectAbnormalClass abnormalClass, string conclusion)
    {
        return items.FindIndex(item =>
            string.Equals(item.DeviceCode, deviceCode, StringComparison.OrdinalIgnoreCase)
            && item.AbnormalClass == abnormalClass
            && string.Equals(item.Conclusion, conclusion, StringComparison.Ordinal));
    }
}
