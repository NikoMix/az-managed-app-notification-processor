using System.Text.Json;
using Azure.Provisioning.Engine.Metering;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine;

/// <summary>
/// HTTP and timer triggers that surface the in-memory <see cref="MeteringService"/>:
/// <list type="bullet">
///   <item><c>POST /api/metering/record</c> – buffer a usage record.</item>
///   <item><c>POST /api/metering/flush</c>  – flush buffered events on demand.</item>
///   <item><c>GET  /api/metering/pending</c> – inspect pending buffer contents.</item>
///   <item>Timer  <c>0 *&#47;5 * * * *</c>     – flush every 5 minutes.</item>
/// </list>
/// </summary>
public class MeteringEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly MeteringService _metering;
    private readonly ILogger<MeteringEndpoint> _logger;

    public MeteringEndpoint(MeteringService metering, ILogger<MeteringEndpoint> logger)
    {
        _metering = metering;
        _logger = logger;
    }

    [Function("metering-record")]
    public async Task<IActionResult> Record(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "metering/record")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        MeteringRecordDto? dto;
        try
        {
            dto = await JsonSerializer
                .DeserializeAsync<MeteringRecordDto>(req.Body, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize metering record.");
            return new BadRequestObjectResult(new { error = "Invalid JSON payload." });
        }

        if (dto is null ||
            string.IsNullOrWhiteSpace(dto.Resource) ||
            string.IsNullOrWhiteSpace(dto.Dimension) ||
            string.IsNullOrWhiteSpace(dto.PlanId) ||
            dto.Quantity <= 0)
        {
            return new BadRequestObjectResult(new { error = "resource, dimension, planId and a positive quantity are required." });
        }

        _metering.Record(new MeteringRecord
        {
            Resource = dto.Resource!,
            Dimension = dto.Dimension!,
            PlanId = dto.PlanId!,
            Quantity = dto.Quantity,
            ResourceKind = dto.ResourceKind ?? MeteringResourceKind.ResourceId,
            EffectiveTime = dto.EffectiveTime,
        });

        return new AcceptedResult();
    }

    [Function("metering-flush")]
    public async Task<IActionResult> Flush(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "metering/flush")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        FlushSummary summary = await _metering.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new OkObjectResult(summary);
    }

    [Function("metering-pending")]
    public IActionResult Pending(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "metering/pending")] HttpRequest req)
    {
        return new OkObjectResult(new
        {
            buckets = _metering.PendingBucketCount,
            events = _metering.Peek(),
        });
    }

    /// <summary>
    /// Flushes the in-memory aggregator every 5 minutes. The marketplace
    /// only accepts one event per hour so the schedule mainly reduces the
    /// time a process crash could lose buffered usage.
    /// </summary>
    [Function("metering-timer-flush")]
    public async Task TimerFlush(
        [TimerTrigger("0 */5 * * * *", RunOnStartup = false)] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        if (_metering.PendingBucketCount == 0)
        {
            return;
        }

        _logger.LogInformation("Timer-triggered metering flush; pending buckets={Count}.", _metering.PendingBucketCount);
        await _metering.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class MeteringRecordDto
    {
        public string? Resource { get; set; }
        public string? Dimension { get; set; }
        public string? PlanId { get; set; }
        public double Quantity { get; set; }
        public MeteringResourceKind? ResourceKind { get; set; }
        public DateTimeOffset? EffectiveTime { get; set; }
    }
}
