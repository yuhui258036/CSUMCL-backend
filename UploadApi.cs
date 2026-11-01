using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace PclCmu.Admin;

public static class UploadApi
{
    public static async Task HandleAsync(HttpContext context, ChunkUploadService uploads, AuthService auth, bool isAdmin)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = context.Request;
        var prefix = isAdmin ? "/admin/api/uploads" : "/api/uploads";
        var rest = request.Path.Value![prefix.Length..].Trim('/');
        var parts = rest.Length == 0 ? [] : rest.Split('/');
        var chunkRoute = parts.Length == 3 && parts[1] == "chunks";
        try
        {
            if (uploads.StorageUnavailable && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsOptions(request.Method))
                throw new UploadApiException(503, "Upload storage is temporarily unavailable; your task is retained. Retry shortly.");
            if (!isAdmin)
            {
                // The public listener has no login, creation, status or publish route.
                if (!uploads.Options.PublicIngressEnabled || !chunkRoute)
                    throw new UploadApiException(404, "Not found.");
                var origin = request.Headers.Origin.ToString();
                if (origin.Length != 0)
                {
                    if (!uploads.Options.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
                        throw new UploadApiException(403, "Origin not allowed.");
                    context.Response.Headers.AccessControlAllowOrigin = origin;
                    context.Response.Headers.Vary = "Origin";
                    context.Response.Headers.AccessControlAllowMethods = "PUT, OPTIONS";
                    context.Response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-Chunk-SHA256";
                    context.Response.Headers.AccessControlMaxAge = "600";
                }
                if (HttpMethods.IsOptions(request.Method))
                {
                    if (origin.Length == 0 || request.Headers.AccessControlRequestMethod != "PUT")
                        throw new UploadApiException(403, "Invalid preflight.");
                    context.Response.StatusCode = 204;
                    return;
                }
            }
            if (chunkRoute)
            {
                if (!HttpMethods.IsPut(request.Method)) throw new UploadApiException(405, "PUT required.");
                if (request.ContentType?.Split(';')[0] != "application/octet-stream")
                    throw new UploadApiException(415, "application/octet-stream required.");
                if (!int.TryParse(parts[2], out var index)) throw new UploadApiException(400, "Invalid chunk index.");
                // Existing sessions retain their original chunk size after config changes.
                // PutChunkAsync enforces the exact session-specific length before reading.
                LimitBody(context, 8 * 1024 * 1024);
                var authorization = request.Headers.Authorization.ToString();
                if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal)) throw new UploadApiException(401, "Upload grant required.");
                await uploads.PutChunkAsync(parts[0], index, authorization[7..], request.Headers["X-Chunk-SHA256"].ToString(),
                    request.ContentLength, request.Body, context.RequestAborted);
                context.Response.StatusCode = 204;
                return;
            }
            if (!isAdmin) throw new UploadApiException(404, "Not found.");
            if (!auth.IsAuthenticated(context)) throw new UploadApiException(401, "Log in again to resume your upload.");
            if (!HttpMethods.IsGet(request.Method) && !auth.ValidateCsrf(context, request.Headers["X-CSRF-Token"].ToString()))
                throw new UploadApiException(403, "Invalid CSRF token.");
            LimitBody(context, 16 * 1024);
            if (parts.Length == 0 && HttpMethods.IsPost(request.Method))
            {
                var create = await request.ReadFromJsonAsync<CreateUpload>(context.RequestAborted)
                    ?? throw new UploadApiException(400, "Missing request.");
                await context.Response.WriteAsJsonAsync(await uploads.CreateAsync(create, context.RequestAborted));
            }
            else if (parts.Length == 1 && HttpMethods.IsGet(request.Method))
                await context.Response.WriteAsJsonAsync(await uploads.GetAsync(parts[0], context.RequestAborted));
            else if (parts.Length == 1 && HttpMethods.IsDelete(request.Method))
            {
                await uploads.CancelAsync(parts[0], context.RequestAborted);
                context.Response.StatusCode = 204;
            }
            else if (parts.Length == 2 && parts[1] == "renew" && HttpMethods.IsPost(request.Method))
                await context.Response.WriteAsJsonAsync(await uploads.RenewAsync(parts[0], context.RequestAborted));
            else if (parts.Length == 2 && parts[1] == "complete" && HttpMethods.IsPost(request.Method))
            {
                var status = await uploads.CompleteAsync(parts[0], context.RequestAborted);
                context.Response.StatusCode = status.State == "completed" ? 200 : 202;
                await context.Response.WriteAsJsonAsync(status);
            }
            else throw new UploadApiException(404, "Not found.");
        }
        catch (UploadApiException ex)
        {
            context.Response.StatusCode = ex.Status;
            if (ex.Status == 413 && request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal))
                context.Response.Headers.Connection = "close";
            if (ex.Status == 429) context.Response.Headers.RetryAfter = "2";
            await context.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            context.Response.StatusCode = ex is BadHttpRequestException bad ? bad.StatusCode : 400;
            if (context.Response.StatusCode == 413 && request.Protocol.StartsWith("HTTP/1", StringComparison.Ordinal))
                context.Response.Headers.Connection = "close";
            await context.Response.WriteAsJsonAsync(new { error = "Invalid request body." });
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[upload-api] Storage failure: {ex.GetType().Name}");
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "Storage temporarily unavailable; retry status before continuing." });
        }
    }

    private static void LimitBody(HttpContext context, long limit)
    {
        if (context.Request.ContentLength > limit) throw new UploadApiException(413, "Request body too large.");
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = limit;
    }
}
