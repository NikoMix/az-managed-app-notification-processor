using System.Text.Json.Serialization;

namespace Azure.Provisioning.Engine.Saas;

/// <summary>
/// Payload schema for the SaaS fulfillment webhook documented at
/// https://learn.microsoft.com/partner-center/marketplace-offers/pc-saas-fulfillment-webhook
/// Microsoft reserves the right to expand the schema, so unknown fields are
/// ignored (no strict deserialization).
/// </summary>
public sealed class SaasWebhookPayload
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; set; }

    [JsonPropertyName("publisherId")]
    public string? PublisherId { get; set; }

    [JsonPropertyName("offerId")]
    public string? OfferId { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("timeStamp")]
    public DateTimeOffset? TimeStamp { get; set; }

    /// <summary>Webhook action (ChangePlan, ChangeQuantity, Renew, Suspend, Unsubscribe, Reinstate).</summary>
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    /// <summary>Operation status, typically InProgress or Succeeded.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("operationRequestSource")]
    public string? OperationRequestSource { get; set; }

    [JsonPropertyName("subscription")]
    public SaasSubscription? Subscription { get; set; }

    [JsonPropertyName("purchaseToken")]
    public string? PurchaseToken { get; set; }
}

public sealed class SaasSubscription
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("publisherId")]
    public string? PublisherId { get; set; }

    [JsonPropertyName("offerId")]
    public string? OfferId { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("beneficiary")]
    public SaasUser? Beneficiary { get; set; }

    [JsonPropertyName("purchaser")]
    public SaasUser? Purchaser { get; set; }

    [JsonPropertyName("allowedCustomerOperations")]
    public List<string>? AllowedCustomerOperations { get; set; }

    [JsonPropertyName("sessionMode")]
    public string? SessionMode { get; set; }

    [JsonPropertyName("isFreeTrial")]
    public bool? IsFreeTrial { get; set; }

    [JsonPropertyName("isTest")]
    public bool? IsTest { get; set; }

    [JsonPropertyName("sandboxType")]
    public string? SandboxType { get; set; }

    [JsonPropertyName("saasSubscriptionStatus")]
    public string? SaasSubscriptionStatus { get; set; }

    [JsonPropertyName("term")]
    public SaasTerm? Term { get; set; }

    [JsonPropertyName("autoRenew")]
    public bool? AutoRenew { get; set; }

    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; set; }

    [JsonPropertyName("lastModified")]
    public DateTimeOffset? LastModified { get; set; }
}

public sealed class SaasUser
{
    [JsonPropertyName("emailId")]
    public string? EmailId { get; set; }

    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    [JsonPropertyName("puid")]
    public string? Puid { get; set; }
}

public sealed class SaasTerm
{
    [JsonPropertyName("startDate")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateTimeOffset? EndDate { get; set; }

    [JsonPropertyName("termUnit")]
    public string? TermUnit { get; set; }

    [JsonPropertyName("chargeDuration")]
    public string? ChargeDuration { get; set; }
}

/// <summary>
/// Well-known SaaS webhook actions.
/// </summary>
public static class SaasWebhookActions
{
    public const string ChangePlan = "ChangePlan";
    public const string ChangeQuantity = "ChangeQuantity";
    public const string Renew = "Renew";
    public const string Suspend = "Suspend";
    public const string Unsubscribe = "Unsubscribe";
    public const string Reinstate = "Reinstate";
}

/// <summary>
/// Well-known SaaS operation status values.
/// </summary>
public static class SaasOperationStatuses
{
    public const string InProgress = "InProgress";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Conflict = "Conflict";
}
