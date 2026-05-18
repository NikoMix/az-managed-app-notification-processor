using Azure;
using Azure.Data.Tables;
using Azure.Provisioning.Engine.Notifications;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Fulfillment;

/// <summary>
/// Persists fulfillment state for managed application instances in Azure
/// Storage Tables. Provides idempotent upsert / mark-deleted / remove
/// semantics so the notification handler can be safely retried.
/// </summary>
public sealed class FulfillmentService
{
    private readonly TableClient _table;
    private readonly ILogger<FulfillmentService> _logger;

    public FulfillmentService(TableClient table, ILogger<FulfillmentService> logger)
    {
        _table = table;
        _logger = logger;
    }

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken) =>
        _table.CreateIfNotExistsAsync(cancellationToken);

    /// <summary>
    /// Inserts or updates the fulfillment entity for a managed application.
    /// </summary>
    public async Task UpsertAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.ApplicationId))
        {
            _logger.LogWarning("Skipping upsert: notification has no applicationId.");
            return;
        }

        var (partitionKey, rowKey) = KeyHelper.BuildKeys(notification.ApplicationId);
        FulfillmentEntity? existing = await TryGetAsync(partitionKey, rowKey, cancellationToken).ConfigureAwait(false);

        var entity = existing ?? new FulfillmentEntity
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            ApplicationId = notification.ApplicationId,
            RegisteredAt = notification.EventTime ?? DateTimeOffset.UtcNow,
        };

        entity.ApplicationDefinitionId = notification.ApplicationDefinitionId ?? entity.ApplicationDefinitionId;
        entity.ProvisioningState = notification.ProvisioningState;
        entity.LastEventType = notification.EventType;
        entity.LastEventTime = notification.EventTime ?? DateTimeOffset.UtcNow;
        entity.IsActive = true;

        if (notification.BillingDetails is { } billing)
        {
            entity.ResourceUsageId = billing.ResourceUsageId ?? entity.ResourceUsageId;
        }

        if (notification.Plan is { } plan)
        {
            entity.PlanPublisher = plan.Publisher ?? entity.PlanPublisher;
            entity.PlanProduct = plan.Product ?? entity.PlanProduct;
            entity.PlanName = plan.Name ?? entity.PlanName;
            entity.PlanVersion = plan.Version ?? entity.PlanVersion;
        }

        if (notification.Error is { } error)
        {
            entity.LastErrorCode = error.Code;
            entity.LastErrorMessage = error.Message;
        }
        else if (string.Equals(notification.ProvisioningState, NotificationProvisioningStates.Succeeded, StringComparison.OrdinalIgnoreCase))
        {
            entity.LastErrorCode = null;
            entity.LastErrorMessage = null;
        }

        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Upserted fulfillment entity for {ApplicationId} (state={ProvisioningState}, event={EventType}).",
            notification.ApplicationId, notification.ProvisioningState, notification.EventType);
    }

    /// <summary>
    /// Marks the entity as deleting / failed-to-delete without removing it.
    /// </summary>
    public async Task MarkStateAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.ApplicationId))
        {
            return;
        }

        var (partitionKey, rowKey) = KeyHelper.BuildKeys(notification.ApplicationId);
        FulfillmentEntity? existing = await TryGetAsync(partitionKey, rowKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            _logger.LogInformation(
                "No existing fulfillment entity for {ApplicationId}; creating a placeholder for state {State}.",
                notification.ApplicationId, notification.ProvisioningState);
            existing = new FulfillmentEntity
            {
                PartitionKey = partitionKey,
                RowKey = rowKey,
                ApplicationId = notification.ApplicationId,
                RegisteredAt = notification.EventTime ?? DateTimeOffset.UtcNow,
            };
        }

        existing.ProvisioningState = notification.ProvisioningState;
        existing.LastEventType = notification.EventType;
        existing.LastEventTime = notification.EventTime ?? DateTimeOffset.UtcNow;

        if (notification.Error is { } error)
        {
            existing.LastErrorCode = error.Code;
            existing.LastErrorMessage = error.Message;
        }

        await _table.UpsertEntityAsync(existing, TableUpdateMode.Replace, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the fulfillment record after the managed application has been
    /// fully deleted.
    /// </summary>
    public async Task RemoveAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.ApplicationId))
        {
            return;
        }

        var (partitionKey, rowKey) = KeyHelper.BuildKeys(notification.ApplicationId);

        try
        {
            Response response = await _table
                .DeleteEntityAsync(partitionKey, rowKey, ETag.All, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Removed fulfillment entity for {ApplicationId} (status={Status}).",
                notification.ApplicationId, response.Status);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogInformation(
                "Fulfillment entity for {ApplicationId} already removed.",
                notification.ApplicationId);
        }
    }

    private async Task<FulfillmentEntity?> TryGetAsync(string partitionKey, string rowKey, CancellationToken cancellationToken)
    {
        try
        {
            Response<FulfillmentEntity> response = await _table
                .GetEntityAsync<FulfillmentEntity>(partitionKey, rowKey, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private static class KeyHelper
    {
        public static (string PartitionKey, string RowKey) BuildKeys(string applicationId)
        {
            // applicationId format:
            // /subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Solutions/applications/{name}
            string subscriptionId = "unknown";
            string[] parts = applicationId.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int subIndex = Array.FindIndex(parts, p => string.Equals(p, "subscriptions", StringComparison.OrdinalIgnoreCase));
            if (subIndex >= 0 && subIndex + 1 < parts.Length)
            {
                subscriptionId = parts[subIndex + 1];
            }

            // Row keys cannot contain '/', '\\', '#', '?'. Replace them.
            string rowKey = applicationId
                .Replace('/', '|')
                .Replace('\\', '|')
                .Replace('#', '_')
                .Replace('?', '_')
                .TrimStart('|');

            if (rowKey.Length > 1000)
            {
                rowKey = rowKey[..1000];
            }

            return (subscriptionId, rowKey);
        }
    }
}
