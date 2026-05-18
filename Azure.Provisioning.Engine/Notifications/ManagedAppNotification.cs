using System.Text.Json.Serialization;

namespace Azure.Provisioning.Engine.Notifications;

/// <summary>
/// Payload schema for Azure Managed Application notifications sent by the
/// Azure Resource Manager to the publisher's webhook endpoint.
/// See https://learn.microsoft.com/azure/azure-resource-manager/managed-applications/publish-notifications
/// </summary>
public sealed class ManagedAppNotification
{
    /// <summary>
    /// The type of event that triggered the notification (PUT, PATCH, DELETE).
    /// </summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    /// <summary>
    /// The fully qualified resource identifier of the managed application instance.
    /// </summary>
    [JsonPropertyName("applicationId")]
    public string? ApplicationId { get; set; }

    /// <summary>
    /// The UTC timestamp of the event that triggered the notification.
    /// </summary>
    [JsonPropertyName("eventTime")]
    public DateTimeOffset? EventTime { get; set; }

    /// <summary>
    /// The provisioning state of the managed application instance
    /// (Accepted, Succeeded, Failed, Deleting, Deleted).
    /// </summary>
    [JsonPropertyName("provisioningState")]
    public string? ProvisioningState { get; set; }

    /// <summary>
    /// Service Catalog only: the application definition identifier.
    /// </summary>
    [JsonPropertyName("applicationDefinitionId")]
    public string? ApplicationDefinitionId { get; set; }

    /// <summary>
    /// Marketplace only: billing details containing the resourceUsageId.
    /// </summary>
    [JsonPropertyName("billingDetails")]
    public BillingDetails? BillingDetails { get; set; }

    /// <summary>
    /// Marketplace only: publisher, offer, SKU and version of the application.
    /// </summary>
    [JsonPropertyName("plan")]
    public MarketplacePlan? Plan { get; set; }

    /// <summary>
    /// Populated only when <see cref="ProvisioningState"/> is Failed.
    /// </summary>
    [JsonPropertyName("error")]
    public NotificationError? Error { get; set; }
}

public sealed class BillingDetails
{
    [JsonPropertyName("resourceUsageId")]
    public string? ResourceUsageId { get; set; }
}

public sealed class MarketplacePlan
{
    [JsonPropertyName("publisher")]
    public string? Publisher { get; set; }

    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

public sealed class NotificationError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public List<NotificationErrorDetail>? Details { get; set; }
}

public sealed class NotificationErrorDetail
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// Well-known event type values used by Azure Managed Application notifications.
/// </summary>
public static class NotificationEventTypes
{
    public const string Put = "PUT";
    public const string Patch = "PATCH";
    public const string Delete = "DELETE";
}

/// <summary>
/// Well-known provisioning state values used by Azure Managed Application
/// notifications.
/// </summary>
public static class NotificationProvisioningStates
{
    public const string Accepted = "Accepted";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Deleting = "Deleting";
    public const string Deleted = "Deleted";
}
