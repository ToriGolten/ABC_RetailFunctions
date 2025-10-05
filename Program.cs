using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Files.Shares;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);


// Use your Azure Storage connection string from settings:
//var conn = Environment.GetEnvironmentVariable("StorageConnection") ?? "UseDevelopmentStorage=true";
var conn = Environment.GetEnvironmentVariable("AzureWebJobsStorage");//?? "UseDevelopmentStorage = true";

// Register Azure Storage client
builder.Services.AddSingleton(new BlobServiceClient(conn));
builder.Services.AddSingleton(new ShareServiceClient(conn));
builder.Services.AddSingleton(new QueueServiceClient(conn));
builder.Services.AddSingleton(new TableServiceClient(conn));

// (Optional) insights
builder.Services.AddApplicationInsightsTelemetryWorkerService();

builder.ConfigureFunctionsWebApplication();

builder.Build().Run();
