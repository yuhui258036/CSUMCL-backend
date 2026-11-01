using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PclCmu.Admin;

internal static class AdminManagementTests
{
    internal static async Task Run(HttpClient admin, HttpClient publicClient, string csrf,
        string dataDir, Action<bool, string> check)
    {
        Console.WriteLine("\n== icons and card ordering ==");
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
        MultipartFormDataContent Form(string id, string? url = null, byte[]? image = null, string? token = null, string port = "")
        {
            var form = new MultipartFormDataContent();
            foreach (var pair in new Dictionary<string, string> { ["id"] = id, ["name"] = id == "test" ? "Test" : "Icon test",
                ["_csrf"] = token ?? csrf, ["sort"] = "-999", ["required"] = "true", ["game_version"] = "1.21.1", ["icon_url"] = url ?? "", ["server_port"] = port })
                form.Add(new StringContent(pair.Value), pair.Key);
            if (image is not null) form.Add(new ByteArrayContent(image), "icon_file", "untrusted-name.jpg");
            return form;
        }
        List<Instance> Stored() => JsonSerializer.Deserialize<List<Instance>>(File.ReadAllText(Path.Combine(dataDir, "instances.json")))!;
        using (var content = Form("icon-test", image: png, token: "wrong"))
        using (var denied = await admin.PostAsync("/admin/instances/new", content))
            check(denied.StatusCode == HttpStatusCode.BadRequest && Stored().Count == 1, "icon create requires CSRF before writing metadata");
        using (var content = Form("icon-test", image: png))
        using (var created = await admin.PostAsync("/admin/instances/new", content))
            check(created.StatusCode == HttpStatusCode.Redirect, "multipart create accepts a PNG icon");
        var icon = Stored().Single(i => i.Id == "icon-test");
        check(icon.Sort == 1 && icon.Required, "new pack appends at end and retains required flag; posted sort ignored");
        check(icon.IconUrl!.EndsWith(".png") && new Uri(icon.IconUrl).AbsolutePath.StartsWith("/icons/"), "icon format and hash filename come from bytes, not filename");
        using (var download = await publicClient.GetAsync(icon.IconUrl))
        {
            check(download.IsSuccessStatusCode && (await download.Content.ReadAsByteArrayAsync()).SequenceEqual(png), "public listener serves exact uploaded icon bytes");
            check(download.Content.Headers.ContentType?.MediaType == "image/png" && download.Headers.CacheControl?.Extensions.Any(e => e.Name == "immutable") == true,
                "uploaded icon has image MIME and immutable cache policy");
        }
        using (var hidden = await publicClient.PostAsJsonAsync("/admin/instances/reorder", new { ids = new[] { "test" }, expected = new[] { "test" } }))
            check(hidden.StatusCode == HttpStatusCode.NotFound, "reorder route stays hidden from public listener");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/admin/instances/reorder"))
        {
            request.Headers.Add("X-CSRF-Token", "wrong");
            request.Content = JsonContent.Create(new { ids = new[] { "icon-test", "test" }, expected = new[] { "test", "icon-test" } });
            using var denied = await admin.SendAsync(request);
            check(denied.StatusCode == HttpStatusCode.Forbidden, "ordering requires valid CSRF");
        }
        using (var sorted = await admin.PostAsJsonAsync("/admin/instances/reorder", new { ids = new[] { "icon-test", "test" }, expected = new[] { "test", "icon-test" } }))
            check(sorted.IsSuccessStatusCode, "card reorder saves successfully");
        check(Stored().OrderBy(i => i.Sort).Select(i => i.Id).SequenceEqual(new[] { "icon-test", "test" }), "order persists in instances.json");
        using (var stale = await admin.PostAsJsonAsync("/admin/instances/reorder", new { ids = new[] { "test", "icon-test" }, expected = new[] { "test", "icon-test" } }))
            check(stale.StatusCode == HttpStatusCode.Conflict, "stale tab cannot overwrite a newer order");
        using (var duplicate = await admin.PostAsJsonAsync("/admin/instances/reorder", new { ids = new[] { "test", "test" }, expected = new[] { "icon-test", "test" } }))
            check(duplicate.StatusCode == HttpStatusCode.BadRequest, "duplicate or missing IDs cannot corrupt ordering");
        using (var content = Form("test", "https://example.com/icon.png"))
        using (var edited = await admin.PostAsync("/admin/instances/test", content))
            check(edited.StatusCode == HttpStatusCode.Redirect, "edit accepts an external HTTPS icon URL");
        var manifest = await publicClient.GetFromJsonAsync<Manifest>("/manifest.json");
        var published = manifest!.Instances.Single(i => i.Id == "test");
        check(published.IconUrl == "https://example.com/icon.png" && published.Sort == 1 && published.Revision == 1 && published.Pack is not null,
            "metadata edit preserves reordered position and published revision in client manifest");
        using (var content = Form("test", image: "<svg onload='alert(1)'/>"u8.ToArray()))
        using (var invalid = await admin.PostAsync("/admin/instances/test", content))
            check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity && Stored().Single(i => i.Id == "test").IconUrl == published.IconUrl,
                "non-raster upload is rejected without changing the existing icon");
        using (var content = Form("test", "javascript:alert(1)"))
        using (var invalid = await admin.PostAsync("/admin/instances/test", content))
            check(invalid.StatusCode == HttpStatusCode.UnprocessableEntity, "non-HTTP icon URLs are rejected");
        using (var content = Form("test", image: new byte[2 * 1024 * 1024 + 1]))
        using (var oversized = await admin.PostAsync("/admin/instances/test", content))
            check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "oversized icon is rejected by bounded multipart parsing");

        foreach (var port in new[] { "0", "-1", "65536", "1.5", "1e3", "abc", "999999999999" })
        foreach (var create in new[] { true, false })
        {
            var before = File.ReadAllText(Path.Combine(dataDir, "instances.json"));
            using var content = Form(create ? "invalid-port" : "test", published.IconUrl, port: port);
            using var response = await admin.PostAsync(create ? "/admin/instances/new" : "/admin/instances/test", content);
            check(response.StatusCode == HttpStatusCode.UnprocessableEntity
                && File.ReadAllText(Path.Combine(dataDir, "instances.json")) == before,
                $"{(create ? "create" : "edit")} rejects port {port} without changing stored data");
        }
        foreach (var port in new[] { "1", "65535", "" })
        foreach (var create in new[] { true, false })
        {
            var id = create ? "port-" + (port.Length == 0 ? "default" : port) : "test";
            using var content = Form(id, published.IconUrl, port: port);
            using var response = await admin.PostAsync(create ? "/admin/instances/new" : "/admin/instances/test", content);
            check(response.StatusCode == HttpStatusCode.Redirect && Stored().Single(i => i.Id == id).Server!.Port == (port.Length == 0 ? 25565 : int.Parse(port)),
                $"{(create ? "create" : "edit")} accepts port {(port.Length == 0 ? "default 25565" : port)}");
        }
    }
}
