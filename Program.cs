using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Services.AddHttpClient();
builder.Services.AddSingleton(sp =>
{
    var connectionString = Environment.GetEnvironmentVariable("COSMOS_CONNECTION_STRING");
    var options = new Microsoft.Azure.Cosmos.CosmosClientOptions
    {
        ConnectionMode = Microsoft.Azure.Cosmos.ConnectionMode.Gateway
    };

    return new Microsoft.Azure.Cosmos.CosmosClient(connectionString, options);
});

builder.Build().Run();
