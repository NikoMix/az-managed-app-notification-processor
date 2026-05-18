using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Saas;

/// <summary>
/// Persists SaaS subscription state in Azure Storage Tables.
/// </summary>
public sealed class SaasSubscriptionStore
{
    private readonly TableClient _table;
    private readonly ILogger<SaasSubscriptionStore> _logger;

    public SaasSubscriptionStore(TableClient table, ILogger<SaasSubscriptionStore> logger)
    {
        _table = table;
        _logger = logger;
    }

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken) =>
        _table.CreateIfNotExistsAsync(cancellationToken);

    public async Task UpsertAsync(SaasWebhookPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId))
        {
            return;
        }

        (string partitionKey, string rowKey) = BuildKeys(payload);
        SaasSubscriptionEntity? existing = await TryGetAsync(partitionKey, rowKey, cancellationToken).ConfigureAwait(false);
        existing ??= new SaasSubscriptionEntity
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            SubscriptionId = payload.SubscriptionId,
            CreatedAt = payload.Subscription?.Created ?? payload.TimeStamp ?? DateTimeOffset.UtcNow,
        };

        SaasSubscription? sub = payload.Subscription;
        existing.Name = sub?.Name ?? existing.Name;
        existing.PublisherId = sub?.PublisherId ?? payload.PublisherId ?? existing.PublisherId;
        existing.OfferId = sub?.OfferId ?? payload.OfferId ?? existing.OfferId;
        existing.PlanId = payload.PlanId ?? sub?.PlanId ?? existing.PlanId;
        existing.Quantity = payload.Quantity ?? sub?.Quantity ?? existing.Quantity;
        existing.Status = sub?.SaasSubscriptionStatus ?? existing.Status;

        if (sub?.Beneficiary is { } beneficiary)
        {
            existing.BeneficiaryEmail = beneficiary.EmailId ?? existing.BeneficiaryEmail;
            existing.BeneficiaryTenantId = beneficiary.TenantId ?? existing.BeneficiaryTenantId;
            existing.BeneficiaryObjectId = beneficiary.ObjectId ?? existing.BeneficiaryObjectId;
        }

        if (sub?.Purchaser is { } purchaser)
        {
            existing.PurchaserEmail = purchaser.EmailId ?? existing.PurchaserEmail;
            existing.PurchaserTenantId = purchaser.TenantId ?? existing.PurchaserTenantId;
        }

        if (sub?.Term is { } term)
        {
            existing.TermStart = term.StartDate ?? existing.TermStart;
            existing.TermEnd = term.EndDate ?? existing.TermEnd;
            existing.TermUnit = term.TermUnit ?? existing.TermUnit;
        }

        existing.AutoRenew = sub?.AutoRenew ?? existing.AutoRenew;
        existing.IsFreeTrial = sub?.IsFreeTrial ?? existing.IsFreeTrial;
        existing.IsTest = sub?.IsTest ?? existing.IsTest;
        existing.LastModifiedAt = sub?.LastModified ?? payload.TimeStamp ?? existing.LastModifiedAt;

        existing.LastAction = payload.Action;
        existing.LastOperationId = payload.Id;
        existing.LastOperationStatus = payload.Status;
        existing.LastEventTime = payload.TimeStamp ?? DateTimeOffset.UtcNow;
        existing.IsActive = !string.Equals(sub?.SaasSubscriptionStatus, "Unsubscribed", StringComparison.OrdinalIgnoreCase);

        await _table.UpsertEntityAsync(existing, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Upserted SaaS subscription {SubscriptionId} (action={Action}, status={Status}).",
            payload.SubscriptionId, payload.Action, sub?.SaasSubscriptionStatus);
    }

    public async Task MarkUnsubscribedAsync(SaasWebhookPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId))
        {
            return;
        }

        (string partitionKey, string rowKey) = BuildKeys(payload);
        SaasSubscriptionEntity? existing = await TryGetAsync(partitionKey, rowKey, cancellationToken).ConfigureAwait(false);
        existing ??= new SaasSubscriptionEntity
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            SubscriptionId = payload.SubscriptionId,
        };

        existing.Status = "Unsubscribed";
        existing.IsActive = false;
        existing.LastAction = payload.Action;
        existing.LastOperationStatus = payload.Status;
        existing.LastEventTime = payload.TimeStamp ?? DateTimeOffset.UtcNow;

        await _table.UpsertEntityAsync(existing, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaasSubscriptionEntity?> TryGetAsync(string partitionKey, string rowKey, CancellationToken cancellationToken)
    {
        try
        {
            Response<SaasSubscriptionEntity> response = await _table
                .GetEntityAsync<SaasSubscriptionEntity>(partitionKey, rowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static (string PartitionKey, string RowKey) BuildKeys(SaasWebhookPayload payload)
    {
        string partition = payload.OfferId
            ?? payload.Subscription?.OfferId
            ?? "saas";
        string rowKey = payload.SubscriptionId ?? Guid.NewGuid().ToString();
        return (Sanitize(partition), Sanitize(rowKey));
    }

    private static string Sanitize(string value) => value
        .Replace('/', '|')
        .Replace('\\', '|')
        .Replace('#', '_')
        .Replace('?', '_');
}
