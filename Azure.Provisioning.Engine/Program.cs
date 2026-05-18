using Azure.Data.Tables;
using Azure.Identity;
using Azure.Provisioning.Engine.Fulfillment;
using Azure.Provisioning.Engine.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Services.AddSingleton(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    string tableName = config["ManagedApp:FulfillmentTableName"] ?? "ManagedAppFulfillment";

    string? connectionString = config["ManagedApp:StorageConnectionString"]
        ?? config["AzureWebJobsStorage"];

    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        return new TableClient(connectionString, tableName);
    }

    string? serviceUri = config["ManagedApp:StorageTableServiceUri"];
    if (string.IsNullOrWhiteSpace(serviceUri))
    {
        throw new InvalidOperationException(
            "Configure either 'ManagedApp:StorageConnectionString', 'AzureWebJobsStorage' or 'ManagedApp:StorageTableServiceUri' for the fulfillment table.");
    }

    return new TableClient(new Uri(serviceUri), tableName, new DefaultAzureCredential());
});

builder.Services.AddSingleton<FulfillmentService>();
builder.Services.AddSingleton<NotificationDispatcher>();

var app = builder.Build();

// Ensure the fulfillment table exists before serving notifications.
using (IServiceScope scope = app.Services.CreateScope())
{
    var fulfillment = scope.ServiceProvider.GetRequiredService<FulfillmentService>();
    await fulfillment.EnsureTableExistsAsync(CancellationToken.None).ConfigureAwait(false);
}

app.Run();

