using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Saas;

/// <summary>
/// Thin client for the Microsoft Marketplace SaaS fulfillment v2 Operations
/// API. Used to:
/// <list type="bullet">
///   <item>Validate the webhook payload by re-reading the operation.</item>
///   <item>PATCH the operation with success/failure for ChangePlan and
///         ChangeQuantity events.</item>
/// </list>
/// </summary>
public sealed class SaasFulfillmentClient
{
    /// <summary>Marketplace SaaS fulfillment API resource id.</summary>
    public const string MarketplaceResource = "62d94f6c-d599-489b-a797-3e10e42fbe22";

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly ILogger<SaasFulfillmentClient> _logger;
    private readonly string _baseAddress;
    private readonly string _apiVersion;
    private readonly string[] _scopes;

    public SaasFulfillmentClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<SaasFulfillmentClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseAddress = (configuration["Saas:Fulfillment:BaseAddress"]
            ?? "https://marketplaceapi.microsoft.com").TrimEnd('/');
        _apiVersion = configuration["Saas:Fulfillment:ApiVersion"] ?? "2018-08-31";

        string resource = configuration["Saas:Fulfillment:Resource"] ?? MarketplaceResource;
        _scopes = new[] { resource + "/.default" };

        string? tenantId = configuration["Saas:Aad:TenantId"];
        string? clientId = configuration["Saas:Aad:ClientId"];
        string? clientSecret = configuration["Saas:Aad:ClientSecret"];

        if (!string.IsNullOrEmpty(tenantId) &&
            !string.IsNullOrEmpty(clientId) &&
            !string.IsNullOrEmpty(clientSecret))
        {
            _credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        }
        else
        {
            _credential = new DefaultAzureCredential();
        }
    }

    /// <summary>
    /// Retrieves a fulfillment operation in order to validate the webhook
    /// payload before acting on it (recommended by the documentation).
    /// </summary>
    public async Task<SaasOperation?> GetOperationAsync(
        string subscriptionId,
        string operationId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_baseAddress}/api/saas/subscriptions/{subscriptionId}/operations/{operationId}?api-version={_apiVersion}");

        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "GET operation {OperationId} for subscription {SubscriptionId} returned {StatusCode}: {Body}",
                operationId, subscriptionId, (int)response.StatusCode, content);
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<SaasOperation>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Acknowledges a fulfillment operation as <c>Success</c> or <c>Failure</c>.
    /// Used for ChangePlan / ChangeQuantity actions.
    /// </summary>
    public async Task<bool> PatchOperationAsync(
        string subscriptionId,
        string operationId,
        string status,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{_baseAddress}/api/saas/subscriptions/{subscriptionId}/operations/{operationId}?api-version={_apiVersion}")
        {
            Content = JsonContent.Create(new { status }),
        };

        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "PATCH operation {OperationId} for subscription {SubscriptionId} with status {Status} failed ({StatusCode}): {Body}",
                operationId, subscriptionId, status, (int)response.StatusCode, content);
            return false;
        }

        _logger.LogInformation(
            "PATCH operation {OperationId} for subscription {SubscriptionId} acknowledged with status {Status}.",
            operationId, subscriptionId, status);
        return true;
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}

/// <summary>
/// Subset of the Marketplace SaaS Fulfillment Operation resource.
/// </summary>
public sealed class SaasOperation
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("activityId")]
    public string? ActivityId { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("offerId")]
    public string? OfferId { get; set; }

    [JsonPropertyName("planId")]
    public string? PlanId { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("timeStamp")]
    public DateTimeOffset? TimeStamp { get; set; }
}

/// <summary>
/// Status values accepted by the PATCH operation endpoint.
/// </summary>
public static class SaasOperationPatchStatus
{
    public const string Success = "Success";
    public const string Failure = "Failure";
}
