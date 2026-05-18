using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Saas;

/// <summary>
/// Dispatches SaaS webhook events to the appropriate handler and, for
/// ChangePlan / ChangeQuantity, PATCHes the Marketplace fulfillment operation
/// to acknowledge success or failure.
/// </summary>
public sealed class SaasWebhookDispatcher
{
    private readonly SaasFulfillmentClient _fulfillmentClient;
    private readonly SaasSubscriptionStore _store;
    private readonly ILogger<SaasWebhookDispatcher> _logger;

    public SaasWebhookDispatcher(
        SaasFulfillmentClient fulfillmentClient,
        SaasSubscriptionStore store,
        ILogger<SaasWebhookDispatcher> logger)
    {
        _fulfillmentClient = fulfillmentClient;
        _store = store;
        _logger = logger;
    }

    public async Task DispatchAsync(SaasWebhookPayload payload, CancellationToken cancellationToken)
    {
        string action = payload.Action ?? string.Empty;
        _logger.LogInformation(
            "Dispatching SaaS webhook {Action} for subscription {SubscriptionId} (status={Status}, opId={OperationId}).",
            action, payload.SubscriptionId, payload.Status, payload.Id);

        // The docs strongly recommend re-reading the operation from the
        // Marketplace API before acting on the webhook payload to authorize
        // the call and to confirm payload data is genuine.
        SaasOperation? operation = await TryValidateAsync(payload, cancellationToken).ConfigureAwait(false);

        switch (action)
        {
            case SaasWebhookActions.ChangePlan:
                await HandleChangePlanOrQuantityAsync(payload, operation, cancellationToken).ConfigureAwait(false);
                break;

            case SaasWebhookActions.ChangeQuantity:
                await HandleChangePlanOrQuantityAsync(payload, operation, cancellationToken).ConfigureAwait(false);
                break;

            case SaasWebhookActions.Renew:
                await _store.UpsertAsync(payload, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Renewed subscription {SubscriptionId}.", payload.SubscriptionId);
                break;

            case SaasWebhookActions.Suspend:
                await _store.UpsertAsync(payload, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Suspended subscription {SubscriptionId}.", payload.SubscriptionId);
                break;

            case SaasWebhookActions.Reinstate:
                await _store.UpsertAsync(payload, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Reinstated subscription {SubscriptionId}.", payload.SubscriptionId);
                break;

            case SaasWebhookActions.Unsubscribe:
                await _store.MarkUnsubscribedAsync(payload, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Unsubscribed subscription {SubscriptionId}.", payload.SubscriptionId);
                break;

            default:
                _logger.LogWarning("Unknown SaaS webhook action '{Action}'. Storing payload for diagnostics.", action);
                await _store.UpsertAsync(payload, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleChangePlanOrQuantityAsync(
        SaasWebhookPayload payload,
        SaasOperation? operation,
        CancellationToken cancellationToken)
    {
        if (operation is null)
        {
            _logger.LogError(
                "Cannot validate operation {OperationId} for subscription {SubscriptionId}; sending Failure PATCH.",
                payload.Id, payload.SubscriptionId);

            await TryPatchAsync(payload, SaasOperationPatchStatus.Failure, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Apply the change locally first so we can roll back to Failure if anything throws.
        try
        {
            await _store.UpsertAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local fulfillment for subscription {SubscriptionId} failed.", payload.SubscriptionId);
            await TryPatchAsync(payload, SaasOperationPatchStatus.Failure, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await TryPatchAsync(payload, SaasOperationPatchStatus.Success, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SaasOperation?> TryValidateAsync(SaasWebhookPayload payload, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId) || string.IsNullOrWhiteSpace(payload.Id))
        {
            return null;
        }

        try
        {
            return await _fulfillmentClient
                .GetOperationAsync(payload.SubscriptionId!, payload.Id!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to retrieve operation {OperationId} for subscription {SubscriptionId}.",
                payload.Id, payload.SubscriptionId);
            return null;
        }
    }

    private async Task TryPatchAsync(SaasWebhookPayload payload, string status, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.SubscriptionId) || string.IsNullOrWhiteSpace(payload.Id))
        {
            return;
        }

        try
        {
            await _fulfillmentClient
                .PatchOperationAsync(payload.SubscriptionId!, payload.Id!, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to PATCH operation {OperationId} for subscription {SubscriptionId} with status {Status}.",
                payload.Id, payload.SubscriptionId, status);
        }
    }
}
