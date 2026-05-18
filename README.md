# Azure Managed App Notification Processor

An Azure Functions (.NET 10, isolated worker) app that hosts **two** webhook
endpoints used by Microsoft commercial marketplace integrations:

1. **Managed Application Notifications** – ARM lifecycle events as documented
   in [Azure managed applications with notifications](https://learn.microsoft.com/azure/azure-resource-manager/managed-applications/publish-notifications).
2. **SaaS Fulfillment Webhooks** – Marketplace SaaS subscription events as
   documented in [Implementing a webhook on the SaaS service](https://learn.microsoft.com/partner-center/marketplace-offers/pc-saas-fulfillment-webhook).

It also ships an in-memory **Metered Billing** aggregator that batches usage
toward the [Marketplace metered billing APIs](https://learn.microsoft.com/partner-center/marketplace-offers/marketplace-metering-service-apis).

Both webhook endpoints persist state in **Azure Storage Tables**.

## Endpoints

| Endpoint | Route | Source |
| --- | --- | --- |
| Managed App notifications | `POST /api/notifications/resource` | Azure Resource Manager |
| SaaS fulfillment webhook  | `POST /api/saas/webhook`           | Microsoft Marketplace |
| Metering – record usage   | `POST /api/metering/record`        | Your application |
| Metering – flush buffer   | `POST /api/metering/flush`         | Operations |
| Metering – inspect buffer | `GET  /api/metering/pending`       | Operations |
| Metering – timer flush    | `0 */5 * * * *`                    | Functions runtime |

## Managed Application notifications

- Azure appends `/resource` to the publisher-supplied notification URI.
- Optional shared-secret validation via `?sig=` query parameter.
- Typed payload for Service Catalog and Marketplace schemas.
- Dispatch table covering every documented combination:
  - `PUT / Accepted` – managed resource group projected
  - `PUT / Succeeded` – provisioning completed
  - `PUT / Failed`    – provisioning failed (error captured)
  - `PATCH / Succeeded` – tags / JIT / identity updated
  - `DELETE / Deleting` – delete initiated
  - `DELETE / Deleted`  – fulfillment record removed
  - `DELETE / Failed`   – delete blocked, error captured
- Idempotent fulfillment service (`UpsertAsync` / `MarkStateAsync` /
  `RemoveAsync`) safe to retry per ARM's retry policy.

## SaaS fulfillment webhook

- Validates the Microsoft Entra **JWT bearer token** in the `Authorization`
  header (audience, tenant, `appid` / `azp`).
- Lenient JSON deserialization (Microsoft may extend the schema).
- Handles every documented action:
  - `ChangePlan` – validate via GET operation, apply locally, PATCH `Success`
    (or `Failure` on error) within the 10-second window.
  - `ChangeQuantity` – same flow as ChangePlan.
  - `Renew` – ACK only; updates local subscription term.
  - `Suspend` – ACK only; flags subscription as suspended.
  - `Reinstate` – ACK only; clears suspension.
  - `Unsubscribe` – notify-only; marks subscription inactive.
- Uses the Marketplace SaaS Fulfillment API v2 via `SaasFulfillmentClient`
  (`ClientSecretCredential` or `DefaultAzureCredential`).
- Returns HTTP 200 as soon as processing completes; any unhandled error
  surfaces a 500 to trigger Microsoft's retry policy (500 retries over 8h).

## Configuration

`local.settings.json` (or App Settings in Azure):

### Managed App notifications

| Key | Description |
| --- | --- |
| `AzureWebJobsStorage` | Standard Functions storage connection. |
| `ManagedApp:StorageConnectionString` | Storage account for the fulfillment table. Falls back to `AzureWebJobsStorage`. |
| `ManagedApp:StorageTableServiceUri` | Alternative: table endpoint when using Managed Identity (`DefaultAzureCredential`). |
| `ManagedApp:FulfillmentTableName` | Defaults to `ManagedAppFulfillment`. |
| `ManagedApp:NotificationSignature` | Expected value of the `?sig=` query parameter. Leave empty to disable. |

### SaaS fulfillment

| Key | Description |
| --- | --- |
| `Saas:StorageConnectionString` | Storage account for the SaaS subscription table. Falls back to `AzureWebJobsStorage`. |
| `Saas:SubscriptionTableName` | Defaults to `SaasSubscriptions`. |
| `Saas:Aad:TenantId` | Microsoft Entra tenant id configured in Partner Center. |
| `Saas:Aad:Audience` | Microsoft Entra application id configured in the offer. |
| `Saas:Aad:MarketplaceAppId` | Expected `appid` / `azp` claim. Default: `20e940b3-4c77-4b0b-9a53-9e16a1b010a7`. |
| `Saas:Aad:ClientId` / `Saas:Aad:ClientSecret` | Used to call the Marketplace fulfillment API. Falls back to `DefaultAzureCredential`. |
| `Saas:Fulfillment:BaseAddress` | Defaults to `https://marketplaceapi.microsoft.com`. |
| `Saas:Fulfillment:ApiVersion` | Defaults to `2018-08-31`. |
| `Saas:Fulfillment:Resource` | Marketplace resource id. Default: `62d94f6c-d599-489b-a797-3e10e42fbe22`. |

### Metered billing

| Key | Description |
| --- | --- |
| `Metering:BaseAddress` | Defaults to `https://marketplaceapi.microsoft.com`. |
| `Metering:ApiVersion` | Defaults to `2018-08-31`. |
| `Metering:Resource` | Marketplace API resource id. Default: `20e940b3-4c77-4b0b-9a53-9e16a1b010a7`. |
| `Metering:Aad:TenantId` / `Metering:Aad:ClientId` / `Metering:Aad:ClientSecret` | Optional override of the credentials used to call the metering API. Falls back to the `Saas:Aad:*` values, then to `DefaultAzureCredential` (e.g. Managed Identity). |

## Metered billing

The `MeteringService` aggregates usage in memory by
`(resource, dimension, planId, calendar-hour)` – which matches the metering
API's constraint that only one event per hour and resource is allowed.
Buffered events are flushed:

- Manually via `POST /api/metering/flush`.
- Automatically every 5 minutes by `metering-timer-flush`.

Quantities for the same hour are summed; expired buckets (>24h old) are
discarded; duplicates returned by the API are treated as success. Failures
re-queue the affected events for the next flush.

> **Note** – Only usage *above* the plan's included base fee should be
> recorded. The metering API itself does not perform that subtraction.

```csharp
public class WidgetService(MeteringService metering)
{
    public void OnWidgetProcessed(string subscriptionId, string planId)
    {
        metering.Record(
            resource: subscriptionId,        // SaaS subscriptionId or Managed App resourceUsageId
            dimension: "widgetsProcessed",
            planId: planId,
            quantity: 1.0);
    }
}
```

## Run locally

```powershell
# Requires the Azure Functions Core Tools v4 and Azurite for storage emulation
func start
```

Send a sample managed app notification:

```powershell
$body = @{
  eventType = 'PUT'
  applicationId = '/subscriptions/aaaa/resourceGroups/rg/providers/Microsoft.Solutions/applications/app'
  eventTime = (Get-Date).ToUniversalTime().ToString('o')
  provisioningState = 'Succeeded'
  applicationDefinitionId = '/subscriptions/aaaa/resourceGroups/rg/providers/Microsoft.Solutions/applicationDefinitions/def'
} | ConvertTo-Json

Invoke-RestMethod -Method Post `
  -Uri 'http://localhost:7071/api/notifications/resource' `
  -ContentType 'application/json' `
  -Body $body
```

Send a sample SaaS webhook (bearer token required in production):

```powershell
$body = @{
  id              = [guid]::NewGuid().ToString()
  activityId      = [guid]::NewGuid().ToString()
  subscriptionId  = [guid]::NewGuid().ToString()
  publisherId     = 'contoso'
  offerId         = 'contoso-saas'
  planId          = 'plan2'
  quantity        = 10
  timeStamp       = (Get-Date).ToUniversalTime().ToString('o')
  action          = 'ChangePlan'
  status          = 'InProgress'
  subscription    = @{
    saasSubscriptionStatus = 'Subscribed'
  }
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post `
  -Uri 'http://localhost:7071/api/saas/webhook' `
  -ContentType 'application/json' `
  -Headers @{ Authorization = 'Bearer <jwt>' } `
  -Body $body
```

Record a metering event and flush:

```powershell
$record = @{
  resource     = '11111111-2222-3333-4444-555555555555'
  resourceKind = 'ResourceId'
  dimension    = 'widgetsProcessed'
  planId       = 'silver'
  quantity     = 3
} | ConvertTo-Json

Invoke-RestMethod -Method Post -Uri 'http://localhost:7071/api/metering/record' `
  -ContentType 'application/json' -Body $record

Invoke-RestMethod -Method Post -Uri 'http://localhost:7071/api/metering/flush'
```

