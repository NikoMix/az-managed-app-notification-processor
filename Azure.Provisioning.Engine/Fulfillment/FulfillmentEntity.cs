using Azure;
using Azure.Data.Tables;

namespace Azure.Provisioning.Engine.Fulfillment;

/// <summary>
/// Table entity that tracks the lifecycle state of a managed application
/// instance for fulfillment, billing and clean-up.
/// </summary>
public sealed class FulfillmentEntity : ITableEntity
{
    /// <summary>
    /// Partition key – the subscription id parsed from the applicationId.
    /// Falls back to "unknown" when the id cannot be parsed.
    /// </summary>
    public string PartitionKey { get; set; } = "unknown";

    /// <summary>
    /// Row key – a sanitized version of the full applicationId so that it is
    /// safe to use as an Azure Table row key.
    /// </summary>
    public string RowKey { get; set; } = string.Empty;

    public ETag ETag { get; set; }
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>The original (un-sanitized) applicationId.</summary>
    public string? ApplicationId { get; set; }
    public string? ApplicationDefinitionId { get; set; }
    public string? ProvisioningState { get; set; }
    public string? LastEventType { get; set; }
    public DateTimeOffset? LastEventTime { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public bool IsActive { get; set; }

    // Marketplace specific fields
    public string? ResourceUsageId { get; set; }
    public string? PlanPublisher { get; set; }
    public string? PlanProduct { get; set; }
    public string? PlanName { get; set; }
    public string? PlanVersion { get; set; }

    // Last error (when ProvisioningState = Failed)
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
}
