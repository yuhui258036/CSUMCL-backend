using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using PclCmu.Admin;

// ---------------------------------------------------------------------------
// PCL-CMU admin backend
//
// Two listeners on a single process:
//   * Public  — HTTPS 0.0.0.0:443   serves static resources from wwwroot
//   * Admin   — HTTP 127.0.0.1:9443 serves the loopback-only admin UI
//
// Routing between the two is by local port (AdminPort, default 9443).
// ---------------------------------------------------------------------------

// CLI utility: print a PBKDF2 hash for the admin password. No server started.
if (args.Length >= 2 && string.Equals(args[0], "--hash-password", StringComparison.Ordinal))
{
    Console.WriteLine(PasswordHasher.Hash(args[1]));
    return;
}

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var publicBaseUrl = config["PublicBaseUrl"] ?? "https://example.com";
var uploadLimitBytes = ParseLong(config["UploadLimitBytes"], 2L * 1024 * 1024 * 1024);
var adminPort = ParseInt(config["AdminPort"], 9443);
var dataDir = config["DataDir"] ?? "/opt/pcl-cmu/data";
var wwwrootDir = config["WwwRoot"] ?? "/opt/pcl-cmu/wwwroot";
var manifestSigningKeyPath = config["ManifestSigningKeyPath"] ?? "/opt/pcl-cmu/keys/manifest-signing-key.pem";
var privatePath = Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
var publicPath = Path.GetFullPath(wwwrootDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (privatePath.StartsWith(publicPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
    throw new ArgumentException("DataDir must be outside WwwRoot; upload sessions are private.");

// Allow large pack uploads (2 GiB) plus multipart boundary overhead.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = uploadLimitBytes + (1L << 20);
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadLimitBytes;
});

Directory.CreateDirectory(dataDir);
// This deployment uses file-backed state: two processes must never write it.
using var writerLease = new FileStream(Path.Combine(dataDir, "writer.lock"), FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None);
var store = new InstanceStore(dataDir, wwwrootDir, publicBaseUrl, manifestSigningKeyPath);
var publisher = new PackPublishService(store, uploadLimitBytes);
var chunkOptions = new ChunkUploadOptions { MaxFileBytes = uploadLimitBytes };
config.GetSection("ChunkUpload").Bind(chunkOptions);
chunkOptions.MaxFileBytes = Math.Min(chunkOptions.MaxFileBytes, uploadLimitBytes);
var uploads = new ChunkUploadService(dataDir, wwwrootDir, store, publisher, chunkOptions);
builder.Services.AddSingleton(uploads);
builder.Services.AddHostedService(services => services.GetRequiredService<ChunkUploadService>());
var app = builder.Build();
var auth = new AuthService();
var adminPasswordHash = Environment.GetEnvironmentVariable("PCL_CMU_ADMIN_PASSWORD_HASH");

// --- Security headers on every response ------------------------------------
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (store.RecoveryRequired)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync("Publication recovery required. Check server storage and restart.");
        return;
    }
    await next(context);
});

// --- Reject path traversal (decoded + raw target) with 404 ------------------
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? string.Empty;

    if (path.Contains("..", StringComparison.Ordinal)
        || rawTarget.Contains("..", StringComparison.OrdinalIgnoreCase)
        || rawTarget.Contains("%2e", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next(context);
});

// --- Split admin vs public by local port ------------------------------------
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == adminPort)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (context.Request.Path.StartsWithSegments("/admin/api/uploads"))
        {
            await UploadApi.HandleAsync(context, uploads, auth, isAdmin: true);
            return;
        }
        await AdminRouter.HandleAsync(context, store, auth, adminPasswordHash, uploadLimitBytes, wwwrootDir, publisher, chunkOptions.ChunkSizeBytes);
        return;
    }

    await next(context);
});

// --- Public listener: /admin and /admin/** are not served here --------------
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    if (context.Request.Path.StartsWithSegments("/api/uploads"))
    {
        await UploadApi.HandleAsync(context, uploads, auth, isAdmin: false);
        return;
    }

    await next(context);
});

// --- Public static files (manifest/announcements/friends/update/packs) ------
var contentTypeProvider = new FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".mrpack"] = "application/zip";
contentTypeProvider.Mappings[".bin"] = "application/octet-stream";
contentTypeProvider.Mappings[".sig"] = "application/json";

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(wwwrootDir),
    ContentTypeProvider = contentTypeProvider,
    ServeUnknownFileTypes = false,

    OnPrepareResponse = ctx =>
    {
        var path = ctx.Context.Request.Path.Value ?? string.Empty;

        if (path.StartsWith("/packs/", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(path, @"^/icons/[a-f0-9]{64}\.(?:png|jpg)$")
            || System.Text.RegularExpressions.Regex.IsMatch(path,
                @"^/update/windows-x86_64/[0-9]+\.[0-9]+\.[0-9]+(?:[-+][a-zA-Z0-9.-]+)?\.zip$"))
            ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        else
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
    }
});

app.Run();

static long ParseLong(string? value, long fallback)
    => long.TryParse(value, out var n) ? n : fallback;

static int ParseInt(string? value, int fallback)
    => int.TryParse(value, out var n) ? n : fallback;
