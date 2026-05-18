using System.Text.Json;
using Azure.Provisioning.Engine.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine;

/// <summary>
/// Webhook endpoint that receives Azure Managed Application notifications.
/// Azure appends <c>/resource</c> to the publisher-supplied notification URI,
/// so the function is bound to the <c>notifications/resource</c> route.
/// See https://learn.microsoft.com/azure/azure-resource-manager/managed-applications/publish-notifications
/// </summary>
public class NotificationEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NotificationDispatcher _dispatcher;
    private readonly ILogger<NotificationEndpoint> _logger;
    private readonly string? _expectedSignature;

    public NotificationEndpoint(
        NotificationDispatcher dispatcher,
        IConfiguration configuration,
        ILogger<NotificationEndpoint> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _expectedSignature = configuration["ManagedApp:NotificationSignature"];
    }

    [Function("notifications")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "notifications/resource")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        // 1. Validate the shared-secret query parameter (?sig=...) recommended
        //    by the docs for endpoint authentication.
        if (!string.IsNullOrEmpty(_expectedSignature))
        {
            string? providedSignature = req.Query["sig"];
            if (!string.Equals(providedSignature, _expectedSignature, StringComparison.Ordinal))
            {
                _logger.LogWarning("Rejected notification: missing or invalid 'sig' query parameter.");
                return new UnauthorizedResult();
            }
        }

        // 2. Deserialize the payload.
        ManagedAppNotification? notification;
        try
        {
            notification = await JsonSerializer
                .DeserializeAsync<ManagedAppNotification>(req.Body, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize managed application notification payload.");
            return new BadRequestObjectResult(new { error = "Invalid JSON payload." });
        }

        if (notification is null ||
            string.IsNullOrWhiteSpace(notification.EventType) ||
            string.IsNullOrWhiteSpace(notification.ApplicationId) ||
            string.IsNullOrWhiteSpace(notification.ProvisioningState))
        {
            _logger.LogWarning("Rejected notification: missing required properties.");
            return new BadRequestObjectResult(new { error = "Missing eventType, applicationId or provisioningState." });
        }

        // 3. Dispatch. Any failure surfaces as 500 so ARM retries per the docs
        //    (5xx, 429 or temporarily unreachable -> retry up to 10 hours).
        try
        {
            await _dispatcher.DispatchAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to dispatch notification for {ApplicationId} ({EventType}/{ProvisioningState}).",
                notification.ApplicationId, notification.EventType, notification.ProvisioningState);
            return new ObjectResult(new { error = "Processing failed." })
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
        }

        return new OkResult();
    }
}
