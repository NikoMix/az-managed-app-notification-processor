using System.Net.Http.Headers;
using System.Net.Http.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Azure.Provisioning.Engine.Metering;

/// <summary>
/// Thin client for the Microsoft Marketplace Metered Billing API documented at
/// https://learn.microsoft.com/partner-center/marketplace-offers/marketplace-metering-service-apis
/// </summary>
public sealed class MarketplaceMeteringClient
{
    /// <summary>Marketplace API resource id used to acquire bearer tokens.</summary>
    public const string MarketplaceResource = "20e940b3-4c77-4b0b-9a53-9e16a1b010a7";

    /// <summary>Maximum number of events per batchUsageEvent call.</summary>
    public const int MaxBatchSize = 25;

    private readonly HttpClient _httpClient;
    private readonly TokenCredential _credential;
    private readonly ILogger<MarketplaceMeteringClient> _logger;
    private readonly string _baseAddress;
    private readonly string _apiVersion;
    private readonly string[] _scopes;

    public MarketplaceMeteringClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<MarketplaceMeteringClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseAddress = (configuration["Metering:BaseAddress"]
            ?? "https://marketplaceapi.microsoft.com").TrimEnd('/');
        _apiVersion = configuration["Metering:ApiVersion"] ?? "2018-08-31";

        string resource = configuration["Metering:Resource"] ?? MarketplaceResource;
        _scopes = new[] { resource + "/.default" };

        string? tenantId = configuration["Metering:Aad:TenantId"]
            ?? configuration["Saas:Aad:TenantId"];
        string? clientId = configuration["Metering:Aad:ClientId"]
            ?? configuration["Saas:Aad:ClientId"];
        string? clientSecret = configuration["Metering:Aad:ClientSecret"]
            ?? configuration["Saas:Aad:ClientSecret"];

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

    public async Task<UsageEventResponse?> EmitAsync(UsageEventRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_baseAddress}/api/usageEvent?api-version={_apiVersion}")
        {
            Content = JsonContent.Create(request),
        };

        AddDefaultHeaders(message);
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content
                .ReadFromJsonAsync<UsageEventResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        if ((int)response.StatusCode == 409)
        {
            // Duplicate – the body contains the previously accepted message.
            var error = await response.Content
                .ReadFromJsonAsync<UsageEventResponse>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                error.Status ??= UsageEventStatus.Duplicate;
            }
            return error;
        }

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "usageEvent rejected for resource {Resource} dimension {Dimension} (status {Status}): {Body}",
            request.ResourceId ?? request.ResourceUri, request.Dimension, (int)response.StatusCode, body);

        return new UsageEventResponse
        {
            Status = UsageEventStatus.Error,
            ResourceId = request.ResourceId,
            ResourceUri = request.ResourceUri,
            Quantity = request.Quantity,
            Dimension = request.Dimension,
            EffectiveStartTime = request.EffectiveStartTime,
            PlanId = request.PlanId,
            Error = new UsageEventError
            {
                Code = ((int)response.StatusCode).ToString(),
                Message = body,
            },
        };
    }

    public async Task<BatchUsageEventResponse?> EmitBatchAsync(BatchUsageEventRequest batch, CancellationToken cancellationToken)
    {
        if (batch.Request.Count == 0)
        {
            return new BatchUsageEventResponse { Count = 0 };
        }

        if (batch.Request.Count > MaxBatchSize)
        {
            throw new ArgumentException(
                $"Batch contains {batch.Request.Count} events; the marketplace metering API allows at most {MaxBatchSize}.",
                nameof(batch));
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_baseAddress}/api/batchUsageEvent?api-version={_apiVersion}")
        {
            Content = JsonContent.Create(batch),
        };

        AddDefaultHeaders(message);
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "batchUsageEvent failed ({StatusCode}) with body: {Body}",
                (int)response.StatusCode, body);
            return null;
        }

        return await response.Content
            .ReadFromJsonAsync<BatchUsageEventResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<UsageEventSummary>> ListUsageEventsAsync(
        DateTimeOffset usageStartDate,
        DateTimeOffset? usageEndDate,
        string? offerId,
        string? planId,
        string? dimension,
        string? reconStatus,
        CancellationToken cancellationToken)
    {
        var query = new List<string>
        {
            "api-version=" + Uri.EscapeDataString(_apiVersion),
            "usageStartDate=" + Uri.EscapeDataString(usageStartDate.UtcDateTime.ToString("o")),
        };

        if (usageEndDate.HasValue)
        {
            query.Add("usageEndDate=" + Uri.EscapeDataString(usageEndDate.Value.UtcDateTime.ToString("o")));
        }

        if (!string.IsNullOrWhiteSpace(offerId))
        {
            query.Add("offerId=" + Uri.EscapeDataString(offerId));
        }

        if (!string.IsNullOrWhiteSpace(planId))
        {
            query.Add("planId=" + Uri.EscapeDataString(planId));
        }

        if (!string.IsNullOrWhiteSpace(dimension))
        {
            query.Add("dimension=" + Uri.EscapeDataString(dimension));
        }

        if (!string.IsNullOrWhiteSpace(reconStatus))
        {
            query.Add("reconStatus=" + Uri.EscapeDataString(reconStatus));
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_baseAddress}/api/usageEvents?{string.Join('&', query)}");

        AddDefaultHeaders(message);
        await AuthorizeAsync(message, cancellationToken).ConfigureAwait(false);

        using HttpResponseMessage response = await _httpClient
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError("GET usageEvents failed ({StatusCode}): {Body}", (int)response.StatusCode, body);
            return new();
        }

        List<UsageEventSummary>? summaries = await response.Content
            .ReadFromJsonAsync<List<UsageEventSummary>>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return summaries ?? new();
    }

    private static void AddDefaultHeaders(HttpRequestMessage message)
    {
        message.Headers.TryAddWithoutValidation("x-ms-requestid", Guid.NewGuid().ToString());
        message.Headers.TryAddWithoutValidation("x-ms-correlationid", Guid.NewGuid().ToString());
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(new TokenRequestContext(_scopes), cancellationToken)
            .ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }
}
