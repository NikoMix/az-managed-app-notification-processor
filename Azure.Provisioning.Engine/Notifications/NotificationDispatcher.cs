using Azure.Provisioning.Engine.Fulfillment;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Notifications;

/// <summary>
/// Dispatches incoming managed application notifications to the correct
/// fulfillment action based on the (eventType, provisioningState) tuple
/// documented at https://learn.microsoft.com/azure/azure-resource-manager/managed-applications/publish-notifications
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly FulfillmentService _fulfillment;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(FulfillmentService fulfillment, ILogger<NotificationDispatcher> logger)
    {
        _fulfillment = fulfillment;
        _logger = logger;
    }

    public async Task DispatchAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        string eventType = notification.EventType ?? string.Empty;
        string provisioningState = notification.ProvisioningState ?? string.Empty;

        _logger.LogInformation(
            "Dispatching notification for {ApplicationId}: {EventType}/{ProvisioningState} at {EventTime}.",
            notification.ApplicationId, eventType, provisioningState, notification.EventTime);

        switch (eventType.ToUpperInvariant(), provisioningState)
        {
            // PUT / Accepted – managed resource group projected, deployment about to start.
            case (NotificationEventTypes.Put, NotificationProvisioningStates.Accepted):
                await OnProvisioningAcceptedAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            // PUT / Succeeded – full provisioning succeeded.
            case (NotificationEventTypes.Put, NotificationProvisioningStates.Succeeded):
                await OnProvisioningSucceededAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            // PUT / Failed – provisioning failed somewhere.
            case (NotificationEventTypes.Put, NotificationProvisioningStates.Failed):
                await OnProvisioningFailedAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            // PATCH / Succeeded – successful update (tags, JIT, identity).
            case (NotificationEventTypes.Patch, NotificationProvisioningStates.Succeeded):
                await OnPatchSucceededAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            // DELETE / Deleting – delete initiated.
            case (NotificationEventTypes.Delete, NotificationProvisioningStates.Deleting):
                await OnDeleteInitiatedAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            // DELETE / Deleted – fully deleted, clean up local state.
            case (NotificationEventTypes.Delete, NotificationProvisioningStates.Deleted):
                await OnDeletedAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            // DELETE / Failed – deletion blocked by an error.
            case (NotificationEventTypes.Delete, NotificationProvisioningStates.Failed):
                await OnDeleteFailedAsync(notification, cancellationToken).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning(
                    "Unhandled notification combination {EventType}/{ProvisioningState} for {ApplicationId}.",
                    eventType, provisioningState, notification.ApplicationId);
                await _fulfillment.UpsertAsync(notification, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private Task OnProvisioningAcceptedAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Managed resource group projected for {ApplicationId}. Deployment is about to start.",
            notification.ApplicationId);
        return _fulfillment.UpsertAsync(notification, cancellationToken);
    }

    private Task OnProvisioningSucceededAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        if (notification.BillingDetails?.ResourceUsageId is { Length: > 0 } usageId)
        {
            _logger.LogInformation(
                "Marketplace fulfillment ready for {ApplicationId}; resourceUsageId={ResourceUsageId}.",
                notification.ApplicationId, usageId);
        }
        else
        {
            _logger.LogInformation(
                "Service catalog fulfillment ready for {ApplicationId}; definition={ApplicationDefinitionId}.",
                notification.ApplicationId, notification.ApplicationDefinitionId);
        }

        return _fulfillment.UpsertAsync(notification, cancellationToken);
    }

    private Task OnProvisioningFailedAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogError(
            "Provisioning failed for {ApplicationId}: {ErrorCode} {ErrorMessage}",
            notification.ApplicationId,
            notification.Error?.Code,
            notification.Error?.Message);
        return _fulfillment.UpsertAsync(notification, cancellationToken);
    }

    private Task OnPatchSucceededAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Patch succeeded for {ApplicationId}; refreshing fulfillment state.",
            notification.ApplicationId);
        return _fulfillment.UpsertAsync(notification, cancellationToken);
    }

    private Task OnDeleteInitiatedAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Delete initiated for {ApplicationId}; marking fulfillment record as Deleting.",
            notification.ApplicationId);
        return _fulfillment.MarkStateAsync(notification, cancellationToken);
    }

    private Task OnDeletedAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Application {ApplicationId} fully deleted; removing fulfillment record.",
            notification.ApplicationId);
        return _fulfillment.RemoveAsync(notification, cancellationToken);
    }

    private Task OnDeleteFailedAsync(ManagedAppNotification notification, CancellationToken cancellationToken)
    {
        _logger.LogError(
            "Delete failed for {ApplicationId}: {ErrorCode} {ErrorMessage}",
            notification.ApplicationId,
            notification.Error?.Code,
            notification.Error?.Message);
        return _fulfillment.MarkStateAsync(notification, cancellationToken);
    }
}
