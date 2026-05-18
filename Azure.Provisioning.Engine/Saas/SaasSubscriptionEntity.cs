using Azure;
using Azure.Data.Tables;

namespace Azure.Provisioning.Engine.Saas;

/// <summary>
/// Table entity that tracks the local state of a SaaS subscription mirrored
/// from Microsoft Partner Center fulfillment events.
/// </summary>
public sealed class SaasSubscriptionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public ETag ETag { get; set; }
    public DateTimeOffset? Timestamp { get; set; }

    public string? SubscriptionId { get; set; }
    public string? Name { get; set; }
    public string? PublisherId { get; set; }
    public string? OfferId { get; set; }
    public string? PlanId { get; set; }
    public int? Quantity { get; set; }
    public string? Status { get; set; }

    public string? BeneficiaryEmail { get; set; }
    public string? BeneficiaryTenantId { get; set; }
    public string? BeneficiaryObjectId { get; set; }
    public string? PurchaserEmail { get; set; }
    public string? PurchaserTenantId { get; set; }

    public DateTimeOffset? TermStart { get; set; }
    public DateTimeOffset? TermEnd { get; set; }
    public string? TermUnit { get; set; }
    public bool? AutoRenew { get; set; }
    public bool? IsFreeTrial { get; set; }
    public bool? IsTest { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? LastModifiedAt { get; set; }

    public string? LastAction { get; set; }
    public string? LastOperationId { get; set; }
    public string? LastOperationStatus { get; set; }
    public DateTimeOffset? LastEventTime { get; set; }

    public bool IsActive { get; set; }
}
