using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PclCmu.Admin;

internal static class HttpUploadTests
{
    public static async Task Run(string tempRoot, Action<bool, string> check, bool browser = false)
    {
        Console.WriteLine("\n== real Kestrel HTTP upload and restart ==");
        var root = Path.Combine(tempRoot, "http");
        var data = Path.Combine(root, "data");
        var web = Path.Combine(root, "web");
        Directory.CreateDirectory(data);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "key.pem");
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(data, "instances.json"), """[{"id":"test","name":"Test","revision":0}]""");
        Directory.CreateDirectory(Path.Combine(web, "update", "windows-x86_64"));
        File.WriteAllBytes(Path.Combine(web, "update", "windows-x86_64", "0.2.0.zip"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(web, "update", "windows-x86_64", "latest.json"), "{}");
        var adminPort = FreePort();
        var publicPort = FreePort();
        while (publicPort == adminPort) publicPort = FreePort();
        var adminOrigin = $"http://127.0.0.1:{adminPort}";
        var publicOrigin = $"http://127.0.0.1:{publicPort}";
        File.WriteAllText(Path.Combine(root, "appsettings.json"), JsonSerializer.Serialize(new
        {
            PublicBaseUrl = publicOrigin, AdminPort = adminPort, DataDir = data, WwwRoot = web,
            UploadLimitBytes = 1048576, ManifestSigningKeyPath = keyPath,
            ChunkUpload = new { ChunkSizeBytes = 65536, MinFreeBytes = 0, QuotaBytes = 4194304,
                PublicIngressEnabled = true, PublicUploadBaseUrl = "https://cdn.example.com", AllowedOrigins = new[] { adminOrigin } },
            Kestrel = new { Endpoints = new { Public = new { Url = publicOrigin }, Admin = new { Url = adminOrigin } } },
            Logging = new { LogLevel = new Dictionary<string, string> { ["Default"] = "Warning" } }
        }));
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Process? process = null;
        var errors = new StringBuilder();
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = new CookieContainer() };
        using var admin = new HttpClient(handler) { BaseAddress = new Uri(adminOrigin), Timeout = TimeSpan.FromSeconds(10) };
        using var publicClient = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
            { BaseAddress = new Uri(publicOrigin), Timeout = TimeSpan.FromSeconds(10) };

        async Task Start()
        {
            // dotnet run launches this test through its apphost. Reusing that
            // executable would recursively start the tests instead of Kestrel.
            var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (string.IsNullOrWhiteSpace(host))
                host = Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath!
                    : Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } runtimeRoot
                        ? Path.Combine(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet") : "dotnet";
            var start = new ProcessStartInfo(host)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "PclCmu.Admin.dll"));
            start.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
            start.Environment["PCL_CMU_ADMIN_PASSWORD_HASH"] = PasswordHasher.Hash(password);
            process = Process.Start(start)!;
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (process.HasExited) throw new Exception("Test server exited: " + errors);
                try { using var response = await admin.GetAsync("/admin/login"); if (response.IsSuccessStatusCode) return; }
                catch (HttpRequestException) { }
                await Task.Delay(50);
            }
            throw new TimeoutException("Test server startup timeout: " + errors);
        }
        async Task Stop()
        {
            if (process is null) return;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(); process.Dispose(); process = null;
        }
        async Task Login()
        {
            using var login = await admin.GetAsync("/admin/login");
            var csrf = handler.CookieContainer.GetCookies(new Uri(adminOrigin + "/admin"))[AuthService.CsrfCookieName]!.Value;
            using var response = await admin.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["password"] = password, ["_csrf"] = csrf }));
            check(response.StatusCode == HttpStatusCode.Redirect, "HTTP login succeeds");
            admin.DefaultRequestHeaders.Remove("X-CSRF-Token");
            admin.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        }
        try
        {
            await Start();
            using (var noAuth = await admin.GetAsync("/admin/api/uploads/" + new string('a',32)))
                check(noAuth.StatusCode == HttpStatusCode.Unauthorized, "private control API requires login (no HTML redirect)");
            using (var hidden = await publicClient.GetAsync("/admin/login"))
                check(hidden.StatusCode == HttpStatusCode.NotFound, "admin stays hidden on public listener");
            await Login();
            var uploadPage = await admin.GetStringAsync("/admin/instances/test/upload");
            check(uploadPage.Contains("/admin/assets/upload.js") && uploadPage.Contains("data-chunk-size=\"65536\""), "upload page uses configured chunk size and embedded JS");
            using (var asset = await admin.GetAsync("/admin/assets/sha256-worker.js"))
                check(asset.IsSuccessStatusCode && (await asset.Content.ReadAsStringAsync()).Contains("class IncrementalSha256"), "hash worker served with authenticated admin assets");
            var zipPath = Path.Combine(root, "http.mrpack");
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                using (var text = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                    text.Write("""{"formatVersion":1,"game":"minecraft","versionId":"1","name":"Test","files":[{"path":"mods/test.jar","downloads":["https://cdn.example.com/test.jar"],"hashes":{"sha1":"aa"},"fileSize":1}],"dependencies":{"minecraft":"1.21.1"}}""");
                using var payload = zip.CreateEntry("overrides/test.bin").Open();
                payload.Write(RandomNumberGenerator.GetBytes(150000));
            }
            var bytes = File.ReadAllBytes(zipPath);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var create = new CreateUpload("test", "http.mrpack", bytes.Length, sha, 0);
            admin.DefaultRequestHeaders.Remove("X-CSRF-Token");
            using (var denied = await admin.PostAsJsonAsync("/admin/api/uploads", create))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "create requires CSRF header before body processing");
            var csrfValue = handler.CookieContainer.GetCookies(new Uri(adminOrigin + "/admin"))[AuthService.CsrfCookieName]!.Value;
            admin.DefaultRequestHeaders.Add("X-CSRF-Token", csrfValue);
            using var created = await admin.PostAsJsonAsync("/admin/api/uploads", create);
            created.EnsureSuccessStatusCode();
            var task = (await created.Content.ReadFromJsonAsync<UploadStatus>())!;
            check(task.ChunkBaseUrl == $"https://cdn.example.com/api/uploads/{task.Id}/chunks/", "server issues CDN URL for chunk data only");
            using var renewed = await admin.PostAsync($"/admin/api/uploads/{task.Id}/renew", null);
            renewed.EnsureSuccessStatusCode();
            var grant = (await renewed.Content.ReadFromJsonAsync<UploadGrant>())!;
            await using var gateway = await BoundedUploadProxy.StartAsync(publicOrigin, task.ChunkSize, root);
            using var throughGateway = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false })
                { BaseAddress = new Uri(gateway.Address), Timeout = TimeSpan.FromSeconds(10) };
            var chunkPath = $"/api/uploads/{task.Id}/chunks/0";
            using (var forbidden = await publicClient.GetAsync($"/api/uploads/{task.Id}"))
                check(forbidden.StatusCode == HttpStatusCode.NotFound, "public endpoint does not expose session status");
            using (var preflight = new HttpRequestMessage(HttpMethod.Options, chunkPath))
            {
                preflight.Headers.Add("Origin", adminOrigin);
                preflight.Headers.Add("Access-Control-Request-Method", "PUT");
                preflight.Headers.Add("Access-Control-Request-Headers", "authorization,x-chunk-sha256,content-type");
                using var response = await throughGateway.SendAsync(preflight);
                check(response.StatusCode == HttpStatusCode.NoContent && response.Headers.GetValues("Access-Control-Allow-Origin").Single() == adminOrigin,
                    "CDN chunk preflight allows only the configured admin origin");
                check(response.Headers.CacheControl?.NoStore == true, "chunk API responses are never cacheable");
            }
            async Task<HttpResponseMessage> Put(int index, string token, string origin = "")
            {
                var chunk = bytes.Skip(index * task.ChunkSize).Take(task.ChunkSize).ToArray();
                using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/uploads/{task.Id}/chunks/{index}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Add("X-Chunk-SHA256", Convert.ToHexString(SHA256.HashData(chunk)));
                if (origin.Length > 0) request.Headers.Add("Origin", origin);
                request.Content = new ByteArrayContent(chunk);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                try { return await throughGateway.SendAsync(request); }
                catch { Console.Error.WriteLine("Origin diagnostics:\n" + errors); throw; }
            }
            using (var oversized = new HttpRequestMessage(HttpMethod.Put, chunkPath))
            {
                oversized.Content = new ByteArrayContent(bytes);
                var before = gateway.ForwardedRequests;
                using var denied = await throughGateway.SendAsync(oversized);
                check(denied.StatusCode == HttpStatusCode.RequestEntityTooLarge && gateway.ForwardedRequests == before,
                    "gateway rejects the complete pack at its per-request cap before origin transfer");
            }
            using (var denied = await Put(0, grant.Token, "https://attacker.example"))
                check(denied.StatusCode == HttpStatusCode.Forbidden, "unapproved cross-origin upload denied");
            using (var denied = await Put(0, new string('0',64)))
                check(denied.StatusCode == HttpStatusCode.Unauthorized, "public chunk requires its scoped bearer grant");
            using (var oversized = new HttpRequestMessage(HttpMethod.Put, chunkPath))
            {
                oversized.Content = new ByteArrayContent(new byte[8 * 1024 * 1024 + 1]);
                oversized.Headers.ExpectContinue = true; // Inspect early 413 without racing a large body against connection shutdown.
                oversized.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var rejected = await publicClient.SendAsync(oversized);
                check(rejected.StatusCode == HttpStatusCode.RequestEntityTooLarge && rejected.Headers.ConnectionClose == true,
                    "origin oversized HTTP/1 request explicitly closes its undrained connection");
            }
            using (var next = await publicClient.GetAsync("/manifest.json"))
                check(next.IsSuccessStatusCode, "origin remains reachable after oversized rejection");
            using (var chunk = await Put(0, grant.Token, adminOrigin)) check(chunk.StatusCode == HttpStatusCode.NoContent, "first public HTTP chunk accepted");
            gateway.LoseNextSuccess = true;
            using (var lost = await Put(0, grant.Token, adminOrigin)) check(lost.StatusCode == HttpStatusCode.BadGateway, "gateway can lose success after origin accepted the chunk");
            using (var retried = await Put(0, grant.Token, adminOrigin)) check(retried.StatusCode == HttpStatusCode.NoContent, "same chunk can be retried through the gateway without conflict");
            await Stop(); await Start();
            using (var expiredLogin = await admin.GetAsync($"/admin/api/uploads/{task.Id}"))
                check(expiredLogin.StatusCode == HttpStatusCode.Unauthorized, "restart requires admin reauthentication");
            await Login();
            var resumed = (await admin.GetFromJsonAsync<UploadStatus>($"/admin/api/uploads/{task.Id}"))!;
            check(resumed.Received.SequenceEqual([0]), "HTTP restart retains acknowledged chunk");
            using (var duplicate = await Put(0, grant.Token)) check(duplicate.StatusCode == HttpStatusCode.NoContent, "grant and duplicate acknowledgement survive restart");
            for (var i = task.ChunkCount - 1; i > 0; i--)
            { using var chunk = await Put(i, grant.Token, adminOrigin); chunk.EnsureSuccessStatusCode(); }
            using (var complete = await admin.PostAsync($"/admin/api/uploads/{task.Id}/complete", null))
                check(complete.StatusCode == HttpStatusCode.Accepted, "complete returns 202 without waiting for ZIP validation");
            for (var attempt = 0; attempt < 200; attempt++)
            {
                resumed = (await admin.GetFromJsonAsync<UploadStatus>($"/admin/api/uploads/{task.Id}"))!;
                if (resumed.State is not ("queued" or "verifying")) break;
                await Task.Delay(25);
            }
            check(resumed.State == "completed" && resumed.Revision == 1, "real HTTP workflow publishes once");
            check(bytes.Length > task.ChunkSize && gateway.ForwardedRequests >= task.ChunkCount,
                "pack larger than gateway request limit publishes through bounded chunks");
            using (var repeat = await admin.PostAsync($"/admin/api/uploads/{task.Id}/complete", null))
                check(repeat.StatusCode == HttpStatusCode.OK, "duplicate completed HTTP request returns existing result");
            var manifest = await publicClient.GetByteArrayAsync("/manifest.json");
            using (var readable = await admin.GetAsync("/admin/manifest"))
            {
                var html = await readable.Content.ReadAsStringAsync();
                check(readable.Content.Headers.ContentType?.MediaType == "text/html" && html.Contains("<table>") && html.Contains("Test"),
                    "admin manifest renders the published resources as an HTML table");
                check(html.Contains("/admin/manifest?format=json") && html.Contains("/packs/test/1.mrpack"),
                    "readable manifest retains raw JSON and published file links");
            }
            check((await admin.GetByteArrayAsync("/admin/manifest?format=json")).SequenceEqual(manifest),
                "admin raw JSON is identical to the public signed manifest");
            using var signature = JsonDocument.Parse(await publicClient.GetByteArrayAsync("/manifest.json.sig"));
            check(key.VerifyData(manifest, Convert.FromBase64String(signature.RootElement.GetProperty("signature").GetString()!), HashAlgorithmName.SHA256),
                "published manifest and detached signature verify over HTTP");
            using var range = new HttpRequestMessage(HttpMethod.Get, "/packs/test/1.mrpack");
            range.Headers.Range = new RangeHeaderValue(11, 89);
            using var download = await publicClient.SendAsync(range);
            check(download.StatusCode == HttpStatusCode.PartialContent && (await download.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes.Skip(11).Take(79)),
                "published pack supports exact HTTP Range download");
            check(download.Headers.CacheControl?.Extensions.Any(x => x.Name == "immutable") == true, "published pack retains immutable CDN caching");
            using (var archive = await publicClient.GetAsync("/update/windows-x86_64/0.2.0.zip"))
                check(archive.Headers.CacheControl?.Extensions.Any(x => x.Name == "immutable") == true, "versioned launcher ZIP can be cached by CDN");
            using (var feed = await publicClient.GetAsync("/update/windows-x86_64/latest.json"))
                check(feed.Headers.CacheControl?.NoCache == true, "mutable update feed still requires revalidation");
            await AdminManagementTests.Run(admin, publicClient, admin.DefaultRequestHeaders.GetValues("X-CSRF-Token").Single(), data, check);
            await Stop();
            await Start();
            await Login();
            var orderedPage = await admin.GetStringAsync("/admin");
            check(orderedPage.IndexOf("data-instance-id=\"icon-test\"", StringComparison.Ordinal) < orderedPage.IndexOf("data-instance-id=\"test\"", StringComparison.Ordinal),
                "card order survives a real server restart");
            if (browser)
            {
                await using var browserGateway = await BoundedUploadProxy.StartAsync(publicOrigin, task.ChunkSize, root, tls: true);
                using (var probe = new HttpClient(new HttpClientHandler { UseProxy = false,
                    ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri?.Host == "127.0.0.1" }))
                using (var response = await probe.GetAsync(browserGateway.Address + "/api/uploads"))
                    check(response.StatusCode == HttpStatusCode.NotFound, "disposable HTTPS gateway accepts loopback TLS before browser testing");
                browserGateway.DelayMilliseconds = 100;
                browserGateway.LoseNextSuccess = true;
                await Stop();
                var settingsPath = Path.Combine(root, "appsettings.json");
                var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
                settings["ChunkUpload"]!["PublicUploadBaseUrl"] = browserGateway.Address;
                settings["UploadLimitBytes"] = 8 * 1024 * 1024;
                settings["ChunkUpload"]!["QuotaBytes"] = 32 * 1024 * 1024;
                File.WriteAllText(settingsPath, settings.ToJsonString());
                var browserPack = Path.Combine(root, "browser.mrpack");
                using (var zip = ZipFile.Open(browserPack, ZipArchiveMode.Create))
                {
                    using (var text = new StreamWriter(zip.CreateEntry("modrinth.index.json").Open()))
                        text.Write("""{"files":[{"path":"mods/test.jar","downloads":["https://cdn.example.com/test.jar"],"hashes":{"sha1":"aa"},"fileSize":1}],"dependencies":{"minecraft":"1.21.1"}}""");
                    using var payload = zip.CreateEntry("overrides/browser.bin", CompressionLevel.NoCompression).Open();
                    payload.Write(RandomNumberGenerator.GetBytes(2 * 1024 * 1024));
                }
                await Start();
                await BrowserUploadTests.Run(adminOrigin, password, browserPack, browserGateway.Address, check);
                check(browserGateway.PreflightRequests > 0, "real Chromium sends cross-origin preflight through HTTPS gateway");
                check(File.ReadAllBytes(Path.Combine(web, "packs", "test", "3.mrpack")).SequenceEqual(File.ReadAllBytes(browserPack)),
                    "browser refresh/resume publishes exact original pack bytes");
            }
        }
        finally { await Stop(); }
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
