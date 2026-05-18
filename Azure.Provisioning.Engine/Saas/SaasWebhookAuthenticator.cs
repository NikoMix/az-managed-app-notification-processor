using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Azure.Provisioning.Engine.Saas;

/// <summary>
/// Validates the Microsoft Entra ID JWT bearer token that Microsoft sends in
/// the Authorization header of every SaaS fulfillment webhook call.
///
/// The token's <c>aud</c> claim must equal the Microsoft Entra application id
/// configured for the offer in Partner Center, and either <c>appid</c> or
/// <c>azp</c> plus <c>tid</c> must match the configured marketplace resource
/// id / tenant id. See
/// https://learn.microsoft.com/partner-center/marketplace-offers/pc-saas-fulfillment-webhook
/// </summary>
public sealed class SaasWebhookAuthenticator
{
    private const string MarketplaceTokenIssuerResource = "20e940b3-4c77-4b0b-9a53-9e16a1b010a7";

    private readonly ILogger<SaasWebhookAuthenticator> _logger;
    private readonly string? _expectedAudience;
    private readonly string? _expectedTenantId;
    private readonly string? _expectedAppId;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _metadata;

    public SaasWebhookAuthenticator(IConfiguration configuration, ILogger<SaasWebhookAuthenticator> logger)
    {
        _logger = logger;
        _expectedAudience = configuration["Saas:Aad:Audience"];
        _expectedTenantId = configuration["Saas:Aad:TenantId"];
        _expectedAppId = configuration["Saas:Aad:MarketplaceAppId"]
            ?? MarketplaceTokenIssuerResource;

        string authority = configuration["Saas:Aad:Authority"]
            ?? $"https://login.microsoftonline.com/{_expectedTenantId ?? "common"}/v2.0";
        string metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";

        _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever());
    }

    public async Task<bool> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_expectedAudience))
        {
            _logger.LogWarning(
                "Saas:Aad:Audience is not configured; webhook authentication is disabled. Configure it before going to production.");
            return true;
        }

        if (!request.Headers.TryGetValue("Authorization", out var headerValues))
        {
            _logger.LogWarning("Missing Authorization header on SaaS webhook call.");
            return false;
        }

        string? header = headerValues.ToString();
        const string bearerPrefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(header) ||
            !header.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Authorization header is not a bearer token.");
            return false;
        }

        string token = header[bearerPrefix.Length..].Trim();

        OpenIdConnectConfiguration config;
        try
        {
            config = await _metadata.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve OpenID Connect metadata for SaaS webhook validation.");
            return false;
        }

        var parameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = _expectedAudience,
            ValidateIssuer = string.IsNullOrEmpty(_expectedTenantId) ? false : true,
            ValidIssuers = string.IsNullOrEmpty(_expectedTenantId)
                ? null
                : new[]
                {
                    $"https://login.microsoftonline.com/{_expectedTenantId}/v2.0",
                    $"https://sts.windows.net/{_expectedTenantId}/",
                },
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        TokenValidationResult result = await _handler
            .ValidateTokenAsync(token, parameters)
            .ConfigureAwait(false);

        if (!result.IsValid)
        {
            _logger.LogWarning(result.Exception, "SaaS webhook bearer token validation failed.");
            return false;
        }

        // Verify appid/azp claim equals the marketplace fulfillment app id.
        if (!string.IsNullOrEmpty(_expectedAppId))
        {
            string? appId = GetClaim(result, "appid") ?? GetClaim(result, "azp");
            if (!string.Equals(appId, _expectedAppId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SaaS webhook bearer token has unexpected appid/azp '{AppId}'. Expected '{ExpectedAppId}'.",
                    appId, _expectedAppId);
                return false;
            }
        }

        // Verify tid claim matches the configured tenant id.
        if (!string.IsNullOrEmpty(_expectedTenantId))
        {
            string? tid = GetClaim(result, "tid");
            if (!string.Equals(tid, _expectedTenantId, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "SaaS webhook bearer token has unexpected tid '{TenantId}'. Expected '{ExpectedTenantId}'.",
                    tid, _expectedTenantId);
                return false;
            }
        }

        return true;
    }

    private static string? GetClaim(TokenValidationResult result, string claimType)
    {
        if (result.Claims is null)
        {
            return null;
        }

        return result.Claims.TryGetValue(claimType, out object? value) ? value?.ToString() : null;
    }
}
