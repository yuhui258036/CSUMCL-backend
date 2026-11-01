using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// A local gateway fixture, not an EdgeOne emulator. Exercises real forwarding,
// per-request limits and a lost success response after the origin has committed.
internal sealed class BoundedUploadProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _origin = new(new HttpClientHandler { UseProxy = false, UseCookies = false });
    public string Address { get; private set; } = "";
    public bool LoseNextSuccess { get; set; }
    public int ForwardedRequests { get; private set; }
    public int PreflightRequests { get; private set; }
    public int DelayMilliseconds { get; set; }
    private X509Certificate2? _certificate;

    private BoundedUploadProxy(WebApplication app) => _app = app;

    public static async Task<BoundedUploadProxy> StartAsync(string origin, long limit, string contentRoot, bool tls = false)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = contentRoot, Args = [] });
        builder.Configuration.Sources.Clear(); // Do not inherit the origin fixture's endpoints.
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        if (!tls) builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = limit);
        X509Certificate2? certificate = null;
        if (tls)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            // Windows Schannel cannot serve an ephemeral-key certificate. A
            // default import gives it a temporary key container, removed on
            // Dispose (no PersistKeySet and no certificate-store/trust changes).
            certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
        }
        var app = builder.Build();
        var proxy = new BoundedUploadProxy(app) { _certificate = certificate };
        app.Run(async context =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/uploads")) { context.Response.StatusCode = 404; return; }
            if (context.Request.ContentLength > limit)
            {
                // The oversized body will not be drained. Do not advertise a
                // reusable HTTP/1 connection then close it under the next PUT.
                context.Response.Headers.Connection = "close";
                context.Response.StatusCode = 413;
                return;
            }
            if (context.Request.Method == "OPTIONS") proxy.PreflightRequests++;
            if (context.Request.Method == "PUT" && proxy.DelayMilliseconds > 0)
                await Task.Delay(proxy.DelayMilliseconds, context.RequestAborted);
            using var outgoing = new HttpRequestMessage(new HttpMethod(context.Request.Method), origin + context.Request.Path + context.Request.QueryString);
            foreach (var name in new[] { "Authorization", "Origin", "X-Chunk-SHA256", "Access-Control-Request-Method", "Access-Control-Request-Headers" })
                if (context.Request.Headers.TryGetValue(name, out var value)) outgoing.Headers.TryAddWithoutValidation(name, value.ToArray());
            if (context.Request.ContentLength is > 0)
            {
                // Kestrel owns Request.Body; disposing an outgoing StreamContent
                // would close it before Kestrel drains/reuses the incoming connection.
                outgoing.Content = new ForwardedBody(context.Request.Body, context.Request.ContentLength.Value);
                outgoing.Content.Headers.ContentLength = context.Request.ContentLength;
                outgoing.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
            using var response = await proxy._origin.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            proxy.ForwardedRequests++;
            foreach (var header in response.Headers)
                if (header.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase) || header.Key is "Vary" or "Cache-Control" or "Retry-After")
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            if (proxy.LoseNextSuccess && context.Request.Method == "PUT" && response.IsSuccessStatusCode)
            {
                proxy.LoseNextSuccess = false;
                context.Response.StatusCode = 502;
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        });
        await app.StartAsync();
        proxy.Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return proxy;
    }

    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(stop.Token);
        await _app.DisposeAsync();
        _origin.Dispose();
        _certificate?.Dispose();
    }

    private sealed class ForwardedBody(Stream body, long length) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => body.CopyToAsync(stream);
        protected override bool TryComputeLength(out long result) { result = length; return true; }
    }
}
