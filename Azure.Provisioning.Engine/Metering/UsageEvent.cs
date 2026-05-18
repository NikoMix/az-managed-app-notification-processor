using System.Text.Json.Serialization;

namespace Azure.Provisioning.Engine.Metering;

/// <summary>
/// Discriminator for the resource identifier used by the metering APIs.
/// SaaS offers use <see cref="ResourceId"/> (SaaS subscription id) while
/// Managed Apps use <see cref="ResourceUri"/> (the application's
/// resourceUsageId).
/// </summary>
public enum MeteringResourceKind
{
    ResourceId,
    ResourceUri,
}

/// <summary>
/// In-memory representation of a usage record submitted by application
/// code. Quantities for the same (resource, dimension, plan, calendar hour)
/// are accumulated into a single emitted event.
/// </summary>
public sealed class MeteringRecord
{
    public required string Resource { get; init; }

    public MeteringResourceKind ResourceKind { get; init; } = MeteringResourceKind.ResourceId;

    public required string Dimension { get; init; }

    public required string PlanId { get; init; }

    public required double Quantity { get; init; }

    /// <summary>
    /// When the consumption occurred. Defaults to <see cref="DateTimeOffset.UtcNow"/>
    /// when omitted. Truncated to the start of the hour during aggregation.
    /// </summary>
    public DateTimeOffset? EffectiveTime { get; init; }
}

/// <summary>
/// Request payload sent to <c>POST /api/usageEvent</c> for a single emission.
/// </summary>
public sealed class UsageEventRequest
{
    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("quantity")]
    public double Quantity { get; set; }

    [JsonPropertyName("dimension")]
    public string? Dimension { get; set; }

    [JsonPropertyName("effectiveStartTime")]
    public DateTimeOffset EffectiveStartTime { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }
}

/// <summary>Wrapper for <c>POST /api/batchUsageEvent</c>.</summary>
public sealed class BatchUsageEventRequest
{
    [JsonPropertyName("request")]
    public List<UsageEventRequest> Request { get; set; } = new();
}

/// <summary>
/// Response body returned by the metering API for a single usage event or
/// for each entry in a batch.
/// </summary>
public sealed class UsageEventResponse
{
    [JsonPropertyName("usageEventId")]
    public string? UsageEventId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("messageTime")]
    public DateTimeOffset? MessageTime { get; set; }

    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("quantity")]
    public double? Quantity { get; set; }

    [JsonPropertyName("dimension")]
    public string? Dimension { get; set; }

    [JsonPropertyName("effectiveStartTime")]
    public DateTimeOffset? EffectiveStartTime { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("error")]
    public UsageEventError? Error { get; set; }
}

public sealed class UsageEventError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public sealed class BatchUsageEventResponse
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("result")]
    public List<UsageEventResponse> Result { get; set; } = new();
}

/// <summary>
/// Item returned by <c>GET /api/usageEvents</c>.
/// </summary>
public sealed class UsageEventSummary
{
    [JsonPropertyName("usageDate")]
    public DateTimeOffset? UsageDate { get; set; }

    [JsonPropertyName("usageResourceId")]
    public string? UsageResourceId { get; set; }

    [JsonPropertyName("dimension")]
    public string? Dimension { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("planName")]
    public string? PlanName { get; set; }

    [JsonPropertyName("offerId")]
    public string? OfferId { get; set; }

    [JsonPropertyName("offerName")]
    public string? OfferName { get; set; }

    [JsonPropertyName("offerType")]
    public string? OfferType { get; set; }

    [JsonPropertyName("azureSubscriptionId")]
    public string? AzureSubscriptionId { get; set; }

    [JsonPropertyName("reconStatus")]
    public string? ReconStatus { get; set; }

    [JsonPropertyName("submittedQuantity")]
    public double? SubmittedQuantity { get; set; }

    [JsonPropertyName("processedQuantity")]
    public double? ProcessedQuantity { get; set; }

    [JsonPropertyName("submittedCount")]
    public int? SubmittedCount { get; set; }
}

/// <summary>
/// Status values returned by the metering API.
/// </summary>
public static class UsageEventStatus
{
    public const string Accepted = "Accepted";
    public const string Duplicate = "Duplicate";
    public const string Expired = "Expired";
    public const string Error = "Error";
    public const string ResourceNotFound = "ResourceNotFound";
    public const string ResourceNotAuthorized = "ResourceNotAuthorized";
    public const string ResourceNotActive = "ResourceNotActive";
    public const string InvalidDimension = "InvalidDimension";
    public const string InvalidQuantity = "InvalidQuantity";
    public const string BadArgument = "BadArgument";
}
