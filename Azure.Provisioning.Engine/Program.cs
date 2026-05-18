using Azure.Data.Tables;
using Azure.Identity;
using Azure.Provisioning.Engine.Fulfillment;
using Azure.Provisioning.Engine.Notifications;
using Azure.Provisioning.Engine.Saas;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Managed app fulfillment (Azure Storage Tables).
builder.Services.AddSingleton(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    string tableName = config["ManagedApp:FulfillmentTableName"] ?? "ManagedAppFulfillment";
    TableClient client = CreateTableClient(config, tableName, "ManagedApp");
    var logger = sp.GetRequiredService<ILogger<FulfillmentService>>();
    return new FulfillmentService(client, logger);
});
builder.Services.AddSingleton<NotificationDispatcher>();

// SaaS webhook fulfillment.
builder.Services.AddSingleton(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    string tableName = config["Saas:SubscriptionTableName"] ?? "SaasSubscriptions";
    TableClient client = CreateTableClient(config, tableName, "Saas");
    var logger = sp.GetRequiredService<ILogger<SaasSubscriptionStore>>();
    return new SaasSubscriptionStore(client, logger);
});
builder.Services.AddSingleton<SaasWebhookAuthenticator>();
builder.Services.AddSingleton<SaasWebhookDispatcher>();
builder.Services.AddHttpClient<SaasFulfillmentClient>();

var app = builder.Build();

// Ensure both tables exist before serving any traffic.
using (IServiceScope scope = app.Services.CreateScope())
{
    await scope.ServiceProvider
        .GetRequiredService<FulfillmentService>()
        .EnsureTableExistsAsync(CancellationToken.None)
        .ConfigureAwait(false);

    await scope.ServiceProvider
        .GetRequiredService<SaasSubscriptionStore>()
        .EnsureTableExistsAsync(CancellationToken.None)
        .ConfigureAwait(false);
}

app.Run();

static TableClient CreateTableClient(IConfiguration config, string tableName, string section)
{
    string? connectionString = config[$"{section}:StorageConnectionString"]
        ?? config["AzureWebJobsStorage"];

    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        return new TableClient(connectionString, tableName);
    }

    string? serviceUri = config[$"{section}:StorageTableServiceUri"];
    if (string.IsNullOrWhiteSpace(serviceUri))
    {
        throw new InvalidOperationException(
            $"Configure '{section}:StorageConnectionString', 'AzureWebJobsStorage' or '{section}:StorageTableServiceUri' for the '{tableName}' table.");
    }

    return new TableClient(new Uri(serviceUri), tableName, new DefaultAzureCredential());
}


