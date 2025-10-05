using Azure;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Files.Shares;
using Azure.Storage.Files.Shares.Models;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;
using static ABCRetail_Functions.Models;

namespace ABCRetail_Functions;

public class StorageFunction
{
    private readonly BlobServiceClient _blobs;
    private readonly ShareServiceClient _shares;
    private readonly QueueServiceClient _queues;
    private readonly TableServiceClient _tables;
    private readonly ILogger<StorageFunction> _logger;

    public StorageFunction(BlobServiceClient blobs, ShareServiceClient shares, QueueServiceClient queues, TableServiceClient tables, ILogger<StorageFunction> logger)
    {
        _blobs = blobs; _shares = shares; _queues = queues; _tables = tables; _logger = logger;
    }

    // ---------- BLOBS ----------
    // OLD
    // [Function("ListBlobs")]
    // [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "blobs/{container}")]

    [Function("ListBlobs")]
    public async Task<HttpResponseData> ListBlobs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get",
        Route = "bloblist/{container}")]   // <<< unique prefix to avoid collision
    HttpRequestData req, string container)
    {
        try
        {
            string? prefix = System.Web.HttpUtility.ParseQueryString(req.Url.Query).Get("prefix");
            var containerClient = _blobs.GetBlobContainerClient(container);
            await containerClient.CreateIfNotExistsAsync();

            var results = new List<object>();
            await foreach (var b in containerClient.GetBlobsAsync(prefix: prefix))
            {
                results.Add(new
                {
                    b.Name,
                    b.Properties.ContentLength,
                    b.Properties.ContentType,
                    b.Properties.LastModified
                });
            }

            return await req.JsonAsync(new { container, count = results.Count, blobs = results });
        }
        catch (Exception ex)
        {
            return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError);
        }
    }


    [Function("BlobItem")]
    public async Task<HttpResponseData> BlobItem([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "delete",Route = "blobs/{container}/{*name}")] HttpRequestData req, string container, string name)
    {
        try
        {
            var cc = _blobs.GetBlobContainerClient(container);
            await cc.CreateIfNotExistsAsync();
            var blob = cc.GetBlobClient(name);

            if (req.Method == "GET")
            {
                if (!await blob.ExistsAsync())
                    return await req.ProblemAsync("Blob not found.", HttpStatusCode.NotFound);
                var res = req.CreateResponse(HttpStatusCode.OK);
                var dl = await blob.DownloadAsync();
                if (dl.Value.Details.ContentType is string ct) res.Headers.Add("Content-Type", ct);
                await dl.Value.Content.CopyToAsync(res.Body);
                return res;
            }
            else if (req.Method == "DELETE")
            {
                await blob.DeleteIfExistsAsync();
                return await req.JsonAsync(new { deleted = true, container, name });
            }
            else
            {
                string contentType = req.Headers.TryGetValues("Content-Type", out var ctVals) ? ctVals.FirstOrDefault() ?? "application/octet-stream" : "application/octet-stream";
                await blob.UploadAsync(req.Body, overwrite: true);
                await blob.SetHttpHeadersAsync(new BlobHttpHeaders { ContentType = contentType });
                var props = await blob.GetPropertiesAsync();
                return await req.JsonAsync(new { uploaded = true, container, name, size = props.Value.ContentLength, contentType = props.Value.ContentType, uri = blob.Uri.ToString() }, HttpStatusCode.Created);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "BlobItem"); return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError); }
    }

    // ---------- TABLES ----------
    [Function("TableUpsert")]
    public async Task<HttpResponseData> TableUpsert([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "tables/{table}")] HttpRequestData req, string table)
    {
        try
        {
            var dto = await JsonSerializer.DeserializeAsync<UpsertEntityDto>(req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null || string.IsNullOrWhiteSpace(dto.PartitionKey) || string.IsNullOrWhiteSpace(dto.RowKey))
                return await req.ProblemAsync("partitionKey and rowKey are required.");
            var tc = _tables.GetTableClient(table);
            await tc.CreateIfNotExistsAsync();
            var entity = new TableEntity(dto.PartitionKey, dto.RowKey);
            if (dto.Properties != null) foreach (var kv in dto.Properties) entity[kv.Key] = kv.Value;
            await tc.UpsertEntityAsync(entity, TableUpdateMode.Merge);
            return await req.JsonAsync(new { upserted = true, dto.PartitionKey, dto.RowKey });
        }
        catch (Exception ex) { _logger.LogError(ex, "TableUpsert"); return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError); }
    }

    [Function("TableByKey")]
    public async Task<HttpResponseData> TableByKey([HttpTrigger(AuthorizationLevel.Anonymous, "get", "delete", Route = "tables/{table}/bykey/{pk}/{rk}")] HttpRequestData req, string table, string pk, string rk)
    {
        try
        {
            var tc = _tables.GetTableClient(table);
            if (req.Method == "GET")
            {
                try { var e = await tc.GetEntityAsync<TableEntity>(pk, rk); return await req.JsonAsync(e.Value); }
                catch (RequestFailedException ex) when (ex.Status == 404) { return await req.ProblemAsync("Entity not found.", HttpStatusCode.NotFound); }
            }
            else { await tc.DeleteEntityAsync(pk, rk); return await req.JsonAsync(new { deleted = true, pk, rk }); }
        }
        catch (Exception ex) { _logger.LogError(ex, "TableByKey"); return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError); }
    }

    [Function("TableQueryByPartition")]
    public async Task<HttpResponseData> TableQueryByPartition([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tables/{table}/partition/{pk}")] HttpRequestData req, string table, string pk)
    {
        try
        {
            var tc = _tables.GetTableClient(table);
            await tc.CreateIfNotExistsAsync();
            string filter = TableClient.CreateQueryFilter<TableEntity>(e => e.PartitionKey == pk);
            var items = new List<TableEntity>();
            await foreach (var e in tc.QueryAsync<TableEntity>(filter)) items.Add(e);
            return await req.JsonAsync(new { table, partitionKey = pk, count = items.Count, items });
        }
        catch (Exception ex) { _logger.LogError(ex, "TableQueryByPartition"); return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError); }
    }

    // ---------- QUEUES ----------
    [Function("Queue")]
    public async Task<HttpResponseData> Queue([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "delete", Route = "queues/{queue}")] HttpRequestData req, string queue)
    {
        try
        {
            var qc = _queues.GetQueueClient(queue);
            await qc.CreateIfNotExistsAsync();
            if (req.Method == "GET")
            {
                var q = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                int max = int.TryParse(q.Get("max"), out var n) ? Math.Clamp(n, 1, 32) : 1;
                var peek = await qc.PeekMessagesAsync(max);
                var items = peek.Value.Select(m => new { m.MessageId, body = m.Body.ToString(), m.InsertedOn, m.ExpiresOn }).ToList();
                return await req.JsonAsync(new { queue, count = items.Count, items });
            }
            else if (req.Method == "DELETE")
            {
                await qc.ClearMessagesAsync();
                return await req.JsonAsync(new { cleared = true, queue });
            }
            else
            {
                var dto = await JsonSerializer.DeserializeAsync<EnqueueRequest>(req.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (dto is null || dto.Message is null) return await req.ProblemAsync("Message is required.");
                BinaryData body = dto.Message is string s ? new BinaryData(s) : BinaryData.FromObjectAsJson(dto.Message, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                TimeSpan? vis = dto.VisibilityTimeoutSeconds is int v ? TimeSpan.FromSeconds(v) : null;
                TimeSpan? ttl = dto.TimeToLiveSeconds is int t ? TimeSpan.FromSeconds(t) : null;
                var resp = await qc.SendMessageAsync(body, vis, ttl);
                return await req.JsonAsync(new { enqueued = true, messageId = resp.Value.MessageId, popReceipt = resp.Value.PopReceipt, expirationTime = resp.Value.ExpirationTime }, HttpStatusCode.Accepted);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "Queue"); return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError); }
    }

    // ---------- AZURE FILES ----------
    // Upload/Download/Delete
    [Function("FileItem")]
    public async Task<HttpResponseData> FileItem([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "delete", Route = "files/{share}/{*path}")] HttpRequestData req, string share, string path)
    {
        try
        {
            var sc = _shares.GetShareClient(share);
            await sc.CreateIfNotExistsAsync();

            var parts = (path ?? string.Empty).Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return await req.ProblemAsync("Path is required.", HttpStatusCode.BadRequest);

            ShareDirectoryClient dir = sc.GetRootDirectoryClient();
            for (int i = 0; i < parts.Length - 1; i++) { dir = dir.GetSubdirectoryClient(parts[i]); await dir.CreateIfNotExistsAsync(); }
            var file = dir.GetFileClient(parts[^1]);

            if (req.Method == "GET")
            {
                if (!await file.ExistsAsync()) return await req.ProblemAsync("File not found.", HttpStatusCode.NotFound);
                var res = req.CreateResponse(HttpStatusCode.OK);
                res.Headers.Add("Content-Type", "application/octet-stream");
                await using var stream = await file.OpenReadAsync();
                await stream.CopyToAsync(res.Body);
                return res;
            }
            else if (req.Method == "DELETE")
            {
                await file.DeleteIfExistsAsync();
                return await req.JsonAsync(new { deleted = true, share, path });
            }
            else
            {
                using var ms = new MemoryStream();
                await req.Body.CopyToAsync(ms);
                ms.Position = 0;
                await file.CreateAsync(ms.Length);
                await file.UploadAsync(ms);
                return await req.JsonAsync(new { uploaded = true, share, path }, HttpStatusCode.Created);
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "FileItem"); return await req.ProblemAsync(ex.Message, HttpStatusCode.InternalServerError); }
    }

    // Directory listing (route avoids collision)
    [Function("ListFiles")]
    public async Task<HttpResponseData> ListFiles(
    [HttpTrigger(AuthorizationLevel.Anonymous, "get",
        Route = "filesdir/{share}/{*path}")]    // <<< changed from files/list/... to filesdir/...
    HttpRequestData req, string share, string path)
    {
        var sc = _shares.GetShareClient(share);
        await sc.CreateIfNotExistsAsync();

        var parts = (path ?? string.Empty).Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        ShareDirectoryClient dir = sc.GetRootDirectoryClient();
        for (int i = 0; i < parts.Length; i++)
        {
            dir = dir.GetSubdirectoryClient(parts[i]);
            await dir.CreateIfNotExistsAsync();
        }

        var items = new List<object>();
        await foreach (var e in dir.GetFilesAndDirectoriesAsync())
            items.Add(new { name = e.Name, isDirectory = e.IsDirectory });

        return await req.JsonAsync(new { share, path, count = items.Count, items });
    }


}