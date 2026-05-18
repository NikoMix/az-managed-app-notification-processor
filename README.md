# Azure Managed App Notification Processor

An Azure Functions (.NET 10, isolated worker) implementation of the webhook
endpoint described in
[Azure managed applications with notifications](https://learn.microsoft.com/azure/azure-resource-manager/managed-applications/publish-notifications).

The function accepts ARM notifications for managed application lifecycle
events, persists fulfillment state in **Azure Storage Tables**, and dispatches
every documented `eventType` / `provisioningState` combination to a dedicated
handler.

## Features

- HTTPS webhook at `POST /api/notifications/resource` (Azure appends
  `/resource` to the publisher-supplied notification URI).
- Optional shared-secret validation via `?sig=` query parameter.
- Strongly typed payload for both Service Catalog and Marketplace schemas
  (`applicationDefinitionId`, `billingDetails`, `plan`, `error`).
- Dispatch table covering every supported combination:
  - `PUT / Accepted` – managed resource group projected
  - `PUT / Succeeded` – provisioning completed (Service Catalog & Marketplace)
  - `PUT / Failed`    – provisioning failed (error captured)
  - `PATCH / Succeeded` – tags / JIT / identity updated
  - `DELETE / Deleting` – delete initiated
  - `DELETE / Deleted`  – fulfillment record removed
  - `DELETE / Failed`   – delete blocked, error captured
- Idempotent fulfillment service (`UpsertAsync` / `MarkStateAsync` /
  `RemoveAsync`) safe to retry per ARM's retry policy (10 hours, exponential).
- Auto-creates the Storage Table on startup.

## Configuration

`local.settings.json` (or App Settings in Azure):

| Key | Description |
| --- | --- |
| `AzureWebJobsStorage` | Standard Functions storage connection. |
| `ManagedApp:StorageConnectionString` | Storage account for the fulfillment table. Falls back to `AzureWebJobsStorage`. |
| `ManagedApp:StorageTableServiceUri` | Alternative: table endpoint when using Managed Identity (`DefaultAzureCredential`). |
| `ManagedApp:FulfillmentTableName` | Defaults to `ManagedAppFulfillment`. |
| `ManagedApp:NotificationSignature` | Expected value of the `?sig=` query parameter. Leave empty to disable. |

## Run locally

```powershell
# Requires the Azure Functions Core Tools v4 and Azurite for storage emulation
func start
```

Send a sample notification:

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
