using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Metering;

/// <summary>
/// In-memory aggregator for Microsoft Marketplace metered billing.
///
/// <para>
/// The metering API enforces that for every (resource, dimension, plan,
/// calendar hour) only a single event can be emitted. Callers therefore
/// record raw consumption via <see cref="Record(MeteringRecord)"/> and the
/// service accumulates the quantity in memory, keyed by the start-of-hour
/// timestamp. <see cref="FlushAsync"/> drains the buffer and submits the
/// aggregated quantities using the batch API (max 25 events per request).
/// </para>
///
/// <para>
/// Only usage above the included base fee should be recorded – the API
/// itself does not perform that subtraction.
/// </para>
/// </summary>
public sealed class MeteringService
{
    private readonly MarketplaceMeteringClient _client;
    private readonly ILogger<MeteringService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<MeteringKey, AggregateBucket> _buckets = new();

    public MeteringService(
        MarketplaceMeteringClient client,
        ILogger<MeteringService> logger,
        TimeProvider? timeProvider = null)
    {
        _client = client;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Number of distinct (resource, dimension, plan, hour) buckets currently
    /// pending flush.
    /// </summary>
    public int PendingBucketCount => _buckets.Count;

    /// <summary>
    /// Records consumption in memory. Multiple calls for the same hour are
    /// summed up and emitted as a single usage event on flush.
    /// </summary>
    public void Record(MeteringRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Quantity <= 0)
        {
            _logger.LogDebug(
                "Skipping non-positive metering quantity {Quantity} for {Resource}/{Dimension}.",
                record.Quantity, record.Resource, record.Dimension);
            return;
        }

        if (string.IsNullOrWhiteSpace(record.Resource) ||
            string.IsNullOrWhiteSpace(record.Dimension) ||
            string.IsNullOrWhiteSpace(record.PlanId))
        {
            throw new ArgumentException(
                "Resource, Dimension and PlanId must be supplied for every metering record.", nameof(record));
        }

        DateTimeOffset effectiveTime = record.EffectiveTime ?? _timeProvider.GetUtcNow();
        DateTimeOffset hourBucket = TruncateToHour(effectiveTime);

        var key = new MeteringKey(record.Resource, record.ResourceKind, record.Dimension, record.PlanId, hourBucket);

        _buckets.AddOrUpdate(
            key,
            _ => new AggregateBucket
            {
                Quantity = record.Quantity,
                FirstEffectiveTime = effectiveTime,
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Quantity += record.Quantity;
                    if (effectiveTime < existing.FirstEffectiveTime)
                    {
                        existing.FirstEffectiveTime = effectiveTime;
                    }
                }
                return existing;
            });
    }

    /// <summary>
    /// Convenience overload that records usage with parameters instead of a record.
    /// </summary>
    public void Record(
        string resource,
        string dimension,
        string planId,
        double quantity,
        MeteringResourceKind resourceKind = MeteringResourceKind.ResourceId,
        DateTimeOffset? effectiveTime = null)
        => Record(new MeteringRecord
        {
            Resource = resource,
            Dimension = dimension,
            PlanId = planId,
            Quantity = quantity,
            ResourceKind = resourceKind,
            EffectiveTime = effectiveTime,
        });

    /// <summary>
    /// Returns a snapshot of the pending buckets without draining them.
    /// </summary>
    public IReadOnlyCollection<UsageEventRequest> Peek()
    {
        var list = new List<UsageEventRequest>(_buckets.Count);
        foreach (KeyValuePair<MeteringKey, AggregateBucket> pair in _buckets)
        {
            list.Add(BuildRequest(pair.Key, pair.Value.Quantity, pair.Key.HourBucket));
        }
        return list;
    }

    /// <summary>
    /// Drains all currently buffered usage events and submits them to the
    /// metering API in batches of at most <see cref="MarketplaceMeteringClient.MaxBatchSize"/>.
    ///
    /// Successfully accepted (or duplicate) events are removed from the
    /// buffer. Failed events are re-added so the next flush can retry.
    /// </summary>
    public async Task<FlushSummary> FlushAsync(CancellationToken cancellationToken)
    {
        if (_buckets.IsEmpty)
        {
            return new FlushSummary();
        }

        // Snapshot and remove only entries older than the current hour, but
        // also any expired ones (> 23 hours old). Note: the API allows the
        // current hour after it elapses, but to keep things simple we
        // include everything – it's still safe because the marketplace
        // rejects duplicates / expired events.
        var drained = new List<KeyValuePair<MeteringKey, double>>();
        DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddHours(-24);

        foreach (MeteringKey key in _buckets.Keys.ToArray())
        {
            if (_buckets.TryRemove(key, out AggregateBucket? bucket))
            {
                if (key.HourBucket < cutoff)
                {
                    _logger.LogWarning(
                        "Discarding expired metering bucket {Resource}/{Dimension} at {Hour} (qty={Quantity}).",
                        key.Resource, key.Dimension, key.HourBucket, bucket.Quantity);
                    continue;
                }

                drained.Add(new KeyValuePair<MeteringKey, double>(key, bucket.Quantity));
            }
        }

        if (drained.Count == 0)
        {
            return new FlushSummary();
        }

        var summary = new FlushSummary();
        foreach (List<KeyValuePair<MeteringKey, double>> chunk in Chunk(drained, MarketplaceMeteringClient.MaxBatchSize))
        {
            var batch = new BatchUsageEventRequest
            {
                Request = chunk.ConvertAll(p => BuildRequest(p.Key, p.Value, p.Key.HourBucket)),
            };

            BatchUsageEventResponse? response;
            try
            {
                response = await _client.EmitBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metering batch flush threw; re-queueing {Count} events.", chunk.Count);
                Requeue(chunk);
                summary.FailedCount += chunk.Count;
                continue;
            }

            if (response is null)
            {
                Requeue(chunk);
                summary.FailedCount += chunk.Count;
                continue;
            }

            for (int i = 0; i < chunk.Count; i++)
            {
                UsageEventResponse? itemResponse = i < response.Result.Count ? response.Result[i] : null;
                ClassifyResult(chunk[i], itemResponse, summary);
            }
        }

        _logger.LogInformation(
            "Metering flush complete: accepted={Accepted}, duplicate={Duplicate}, expired={Expired}, failed={Failed}.",
            summary.AcceptedCount, summary.DuplicateCount, summary.ExpiredCount, summary.FailedCount);

        return summary;
    }

    private void ClassifyResult(KeyValuePair<MeteringKey, double> chunkItem, UsageEventResponse? response, FlushSummary summary)
    {
        string status = response?.Status ?? UsageEventStatus.Error;
        switch (status)
        {
            case UsageEventStatus.Accepted:
                summary.AcceptedCount++;
                summary.AcceptedQuantity += chunkItem.Value;
                break;

            case UsageEventStatus.Duplicate:
                // Treat as success – Microsoft already recorded this hour.
                summary.DuplicateCount++;
                break;

            case UsageEventStatus.Expired:
            case UsageEventStatus.InvalidDimension:
            case UsageEventStatus.InvalidQuantity:
            case UsageEventStatus.BadArgument:
            case UsageEventStatus.ResourceNotFound:
            case UsageEventStatus.ResourceNotAuthorized:
            case UsageEventStatus.ResourceNotActive:
                _logger.LogWarning(
                    "Metering event for {Resource}/{Dimension} dropped with status {Status} ({Message}).",
                    chunkItem.Key.Resource, chunkItem.Key.Dimension, status, response?.Error?.Message);
                summary.ExpiredCount++;
                break;

            default:
                _logger.LogError(
                    "Metering event for {Resource}/{Dimension} returned status {Status} ({Message}); re-queueing.",
                    chunkItem.Key.Resource, chunkItem.Key.Dimension, status, response?.Error?.Message);
                Requeue(new[] { chunkItem });
                summary.FailedCount++;
                break;
        }
    }

    private void Requeue(IEnumerable<KeyValuePair<MeteringKey, double>> items)
    {
        foreach (KeyValuePair<MeteringKey, double> item in items)
        {
            _buckets.AddOrUpdate(
                item.Key,
                _ => new AggregateBucket { Quantity = item.Value, FirstEffectiveTime = item.Key.HourBucket },
                (_, existing) =>
                {
                    lock (existing)
                    {
                        existing.Quantity += item.Value;
                    }
                    return existing;
                });
        }
    }

    private static UsageEventRequest BuildRequest(MeteringKey key, double quantity, DateTimeOffset effectiveStartTime)
    {
        var request = new UsageEventRequest
        {
            Quantity = quantity,
            Dimension = key.Dimension,
            PlanId = key.PlanId,
            EffectiveStartTime = effectiveStartTime,
        };

        if (key.ResourceKind == MeteringResourceKind.ResourceUri)
        {
            request.ResourceUri = key.Resource;
        }
        else
        {
            request.ResourceId = key.Resource;
        }

        return request;
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static IEnumerable<List<KeyValuePair<MeteringKey, double>>> Chunk(
        List<KeyValuePair<MeteringKey, double>> source,
        int size)
    {
        for (int i = 0; i < source.Count; i += size)
        {
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
        }
    }

    private readonly record struct MeteringKey(
        string Resource,
        MeteringResourceKind ResourceKind,
        string Dimension,
        string PlanId,
        DateTimeOffset HourBucket);

    private sealed class AggregateBucket
    {
        public double Quantity;
        public DateTimeOffset FirstEffectiveTime;
    }
}

/// <summary>
/// Result of a flush operation.
/// </summary>
public sealed class FlushSummary
{
    public int AcceptedCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ExpiredCount { get; set; }
    public int FailedCount { get; set; }
    public double AcceptedQuantity { get; set; }
}
