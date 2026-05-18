using System.Text.Json;
using Azure.Provisioning.Engine.Saas;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine;

/// <summary>
/// Webhook endpoint that receives SaaS fulfillment events from Microsoft
/// Marketplace as described in
/// https://learn.microsoft.com/partner-center/marketplace-offers/pc-saas-fulfillment-webhook
///
/// The endpoint:
/// <list type="number">
///   <item>Validates the Microsoft Entra JWT bearer token in the Authorization header.</item>
///   <item>Deserializes the payload leniently (Microsoft may add new fields).</item>
///   <item>Acknowledges receipt with HTTP 200 as quickly as possible.</item>
///   <item>Dispatches the action to the <see cref="SaasWebhookDispatcher"/>
///         which PATCHes the operation for ChangePlan / ChangeQuantity.</item>
/// </list>
/// </summary>
public class SaasWebhookEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SaasWebhookAuthenticator _authenticator;
    private readonly SaasWebhookDispatcher _dispatcher;
    private readonly ILogger<SaasWebhookEndpoint> _logger;

    public SaasWebhookEndpoint(
        SaasWebhookAuthenticator authenticator,
        SaasWebhookDispatcher dispatcher,
        ILogger<SaasWebhookEndpoint> logger)
    {
        _authenticator = authenticator;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    [Function("saas-webhook")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "saas/webhook")] HttpRequest req,
        CancellationToken cancellationToken)
    {
        // 1. JWT validation – Microsoft requires bearer-token-secured webhooks.
        if (!await _authenticator.AuthenticateAsync(req, cancellationToken).ConfigureAwait(false))
        {
            return new UnauthorizedResult();
        }

        // 2. Lenient deserialization (ISVs must not strict-deserialize).
        SaasWebhookPayload? payload;
        try
        {
            payload = await JsonSerializer
                .DeserializeAsync<SaasWebhookPayload>(req.Body, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize SaaS webhook payload.");
            return new BadRequestObjectResult(new { error = "Invalid JSON payload." });
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Action))
        {
            _logger.LogWarning("Rejected SaaS webhook: missing action.");
            return new BadRequestObjectResult(new { error = "Missing action." });
        }

        // 3. Dispatch. Any unhandled exception surfaces a 500 so Microsoft
        //    retries per the documented policy (500 retries over 8 hours).
        try
        {
            await _dispatcher.DispatchAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to dispatch SaaS webhook {Action} for subscription {SubscriptionId}.",
                payload.Action, payload.SubscriptionId);
            return new ObjectResult(new { error = "Processing failed." })
            {
                StatusCode = StatusCodes.Status500InternalServerError,
            };
        }

        // 4. ACK with 200 OK as recommended by the docs.
        return new OkResult();
    }
}
