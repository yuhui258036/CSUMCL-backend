using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Http.Features;

namespace PclCmu.Admin;

/// <summary>
/// Hand-rolled router + HTML rendering for the loopback-only admin listener.
/// No frontend build: pages are server-rendered HTML with inline CSS.
/// </summary>
public static class AdminRouter
{
    private const string InstancesPrefix = "/admin/instances/";
    private const string UploadSuffix = "/upload";
    private const string DeleteSuffix = "/delete";

    private static readonly Regex InstanceIdRegex = new("^[a-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static async Task HandleAsync(
        HttpContext context,
        InstanceStore store,
        AuthService auth,
        string? passwordHash,
        long uploadLimitBytes,
        string wwwrootDir, PackPublishService publisher, int chunkSize = 2 * 1024 * 1024)
    {
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;

        // ---- Login (no auth required) -------------------------------------
        if (path == "/admin/login")
        {
            if (method == "GET")
            {
                await GetLoginAsync(context, auth, passwordHash, null);
                return;
            }

            if (method == "POST")
            {
                await PostLoginAsync(context, auth, passwordHash);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Logout --------------------------------------------------------
        if (path == "/admin/logout")
        {
            if (method == "POST")
            {
                await PostLogoutAsync(context, auth);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // Only fixed, non-sensitive presentation assets are available before login.
        if (path is "/admin/assets/upload.js" or "/admin/assets/sha256-worker.js"
            or "/admin/assets/upload-background.js" or "/admin/assets/admin-navigation.js"
            or "/admin/assets/admin.js" or "/admin/assets/admin-validation.js" or "/admin/assets/admin.css" or "/admin/assets/admin-logo.png")
        {
            if (method != "GET") { await MethodNotAllowedAsync(context); return; }
            if (path.EndsWith("upload.js") || path.EndsWith("sha256-worker.js") || path.EndsWith("upload-background.js") || path.EndsWith("admin-navigation.js"))
                if (!auth.IsAuthenticated(context)) { Redirect(context, "/admin/login"); return; }
            context.Response.ContentType = path.EndsWith(".css") ? "text/css; charset=utf-8"
                : path.EndsWith(".png") ? "image/png" : "text/javascript; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await using var asset = typeof(AdminRouter).Assembly.GetManifestResourceStream("PclCmu.Admin." + Path.GetFileName(path))!;
            await asset.CopyToAsync(context.Response.Body, context.RequestAborted);
            return;
        }
        if (!auth.IsAuthenticated(context)) { Redirect(context, "/admin/login"); return; }

        if (path == "/admin/instances/reorder")
        {
            if (method != "POST") { await MethodNotAllowedAsync(context); return; }
            await PostReorderAsync(context, auth, store);
            return;
        }

        if (path == "/admin" || path == "/admin/")
        {
            if (method == "GET")
            {
                await GetDashboardAsync(context, store, auth);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Manifest preview ---------------------------------------------
        if (path == "/admin/manifest")
        {
            if (method == "GET")
            {
                await GetManifestPreviewAsync(context, store);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- New instance --------------------------------------------------
        if (path == "/admin/instances/new")
        {
            if (method == "GET")
            {
                await GetInstanceFormAsync(context, auth, null, null, null);
                return;
            }

            if (method == "POST")
            {
                await PostNewInstanceAsync(context, auth, store, wwwrootDir);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Instance upload / edit ---------------------------------------
        if (path.StartsWith(InstancesPrefix, StringComparison.Ordinal))
        {
            var rest = path.Substring(InstancesPrefix.Length);

            if (rest.EndsWith(DeleteSuffix, StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(rest.Substring(0, rest.Length - DeleteSuffix.Length));
                if (id.Length > 0)
                {
                    if (method == "GET")
                    {
                        await GetDeleteFormAsync(context, auth, store, id);
                        return;
                    }

                    if (method == "POST")
                    {
                        await PostDeleteAsync(context, auth, store, id);
                        return;
                    }

                    await MethodNotAllowedAsync(context);
                    return;
                }
            }
            else if (rest.EndsWith(UploadSuffix, StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(rest.Substring(0, rest.Length - UploadSuffix.Length));
                if (id.Length > 0)
                {
                    if (method == "GET")
                    {
                        await GetUploadFormAsync(context, auth, store, id, chunkSize, uploadLimitBytes);
                        return;
                    }

                    if (method == "POST")
                    {
                        await PostUploadAsync(context, auth, store, id, uploadLimitBytes, wwwrootDir, publisher);
                        return;
                    }

                    await MethodNotAllowedAsync(context);
                    return;
                }
            }
            else if (!rest.Contains('/') && rest.Length > 0)
            {
                var id = Uri.UnescapeDataString(rest);
                if (method == "GET")
                {
                    await GetEditFormAsync(context, auth, store, id);
                    return;
                }

                if (method == "POST")
                {
                    await PostEditAsync(context, auth, store, id, wwwrootDir);
                    return;
                }

                await MethodNotAllowedAsync(context);
                return;
            }
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // ------------------------------------------------------------------ Login

    private static async Task GetLoginAsync(HttpContext context, AuthService auth, string? passwordHash, string? error)
    {
        if (auth.IsAuthenticated(context))
        {
            Redirect(context, "/admin");
            return;
        }

        var csrf = auth.EnsureCsrfCookie(context);
        var notice = string.IsNullOrWhiteSpace(passwordHash)
            ? "<p class=\"err\">尚未配置管理员密码，请联系后台维护人员完成设置。</p>"
            : "";
        var err = error is null ? "" : $"<p class=\"err\">{H(error)}</p>";

        var content = $$"""
<div class="panel">{{notice}}
{{err}}
<form novalidate method="post" action="/admin/login">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  <label>管理员密码 <input type="password" name="password" autocomplete="current-password" autofocus required placeholder="请输入管理员密码"></label>
  <div class="actions"><button type="submit">登录</button></div>
</form>
""";

        await WriteHtmlAsync(context, Layout("后台登录", content + "</div>"));
    }

    private static async Task PostLoginAsync(HttpContext context, AuthService auth, string? passwordHash)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var password = form["password"].ToString();

        if (!string.IsNullOrWhiteSpace(passwordHash) && PasswordHasher.Verify(password, passwordHash))
        {
            auth.StartSession(context);
            Redirect(context, "/admin");
            return;
        }

        await GetLoginAsync(
            context,
            auth,
            passwordHash,
            string.IsNullOrWhiteSpace(passwordHash)
                ? "尚未配置管理员密码。"
                : "密码不正确，请重新输入。");
    }

    private static async Task PostLogoutAsync(HttpContext context, AuthService auth)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        auth.SignOut(context);
        Redirect(context, "/admin/login");
    }

    // -------------------------------------------------------------- Dashboard

    private static async Task GetDashboardAsync(HttpContext context, InstanceStore store, AuthService auth)
    {
        var csrf = auth.EnsureCsrfCookie(context);
        var rows = new StringBuilder();

        var instances = store.List();
        foreach (var i in instances)
        {
            var idEnc = Uri.EscapeDataString(i.Id);
            var published = i.Pack is not null;
            var version = i.Minecraft?.GameVersion;
            rows.Append($"<article class=\"instance-card\" data-instance-id=\"{H(i.Id)}\" aria-label=\"{H(i.Name)}\">");
            rows.Append($"<button type=\"button\" class=\"drag-handle\" aria-label=\"排序：{H(i.Name)}\" title=\"拖拽排序；方向键上移或下移\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M8 5h.01M16 5h.01M8 12h.01M16 12h.01M8 19h.01M16 19h.01\"/></svg></button>");
            var iconSource = Uri.TryCreate(i.IconUrl, UriKind.Absolute, out var iconUri) && iconUri.Scheme is "http" or "https" ? iconUri.AbsoluteUri : "";
            rows.Append($"<div class=\"pack-icon\"><span class=\"icon-placeholder\" aria-hidden=\"true\"><svg viewBox=\"0 0 24 24\"><rect x=\"4\" y=\"4\" width=\"16\" height=\"16\" rx=\"3\"/><path d=\"m4 16 5-5 4 4 3-3 4 4\"/></svg></span>{(iconSource.Length == 0 ? "" : $"<img class=\"pack-icon-image\" src=\"{H(iconSource)}\" alt=\"\" referrerpolicy=\"no-referrer\" decoding=\"async\" loading=\"lazy\" hidden>")}</div>");
            rows.Append($"<div class=\"instance-name\"><span class=\"card-label\">名称 / 标识</span><strong>{H(i.Name)}</strong><small>{H(i.Id)}</small></div>");
            rows.Append($"<div><span class=\"card-label\">游戏 / 加载器</span>{H(string.IsNullOrWhiteSpace(version) ? "待上传后识别" : version)}<small>{H(i.Minecraft?.Loader)} {H(i.Minecraft?.LoaderVersion)}</small></div>");
            rows.Append($"<div><span class=\"card-label\">大小 / 版本</span>{(published ? FormatSize(i.Pack!.Size) : "—")}<small>版本 {i.Revision}</small></div>");
            rows.Append($"<div><span class=\"card-label\">必须</span>{(i.Required ? "是" : "否")}</div>");
            rows.Append($"<div><span class=\"card-label\">发布状态</span><span class=\"tag {(published ? "success" : "pending")}\">{(published ? "已发布" : "待发布")}</span></div>");
            rows.Append($"<div class=\"table-actions\"><a class=\"button secondary small\" href=\"/admin/instances/{idEnc}/upload\">上传新版本</a><a class=\"quiet small\" href=\"/admin/instances/{idEnc}\">编辑</a><a class=\"quiet small danger\" href=\"/admin/instances/{idEnc}/delete\">移除</a></div></article>");
        }
        var content = $$"""
<div class="stats">
 <div class="stat"><span>整合包</span><strong>{{instances.Count}} <small>个</small></strong></div>
 <div class="stat"><span>已发布</span><strong>{{instances.Count(i => i.Pack is not null)}} <small>个</small></strong></div>
 <div class="stat"><span>当前版本资源体积</span><strong>{{FormatSize(instances.Sum(i => i.Pack?.Size ?? 0))}}</strong></div>
</div>
<section class="instance-section"><div class="panel-title"><h2>整合包</h2><a class="button" href="/admin/instances/new">＋ 新建整合包</a></div>
<div id="instance-list" data-csrf="{{H(csrf)}}">{{(instances.Count == 0 ? "<div class=\"panel empty\"><h2>还没有整合包</h2></div>" : rows.ToString())}}</div>
<p id="reorder-status" role="status" aria-live="polite" hidden></p>
</section>

""";
        await WriteHtmlAsync(context, Layout("整合包管理", content));
    }

    // ------------------------------------------------------- Manifest preview

    private static async Task GetManifestPreviewAsync(HttpContext context, InstanceStore store)
    {
        var json = store.GetManifestJson();
        if (context.Request.Query["format"] == "json")
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(json);
            return;
        }
        // Render the same published snapshot served to clients, excluding drafts.
        var manifest = JsonSerializer.Deserialize<Manifest>(json)!;
        var rows = new StringBuilder();
        foreach (var item in manifest.Instances)
        {
            var pack = item.Pack;
            var server = string.IsNullOrWhiteSpace(item.Server?.Address) ? "—" : $"{item.Server.Address}:{item.Server.Port}";
            rows.Append($"<tr><td><strong>{H(item.Name)}</strong><small>{H(item.Id)}</small></td><td>{item.Revision}</td>");
            rows.Append($"<td>{H(item.Minecraft?.GameVersion)}<small>{H(item.Minecraft?.Loader)} {H(item.Minecraft?.LoaderVersion)}</small></td>");
            rows.Append($"<td>{FormatSize(pack?.Size ?? 0)}</td><td>{ResourceLink(pack?.Url, "整合包")}{ResourceLink(pack?.FilesUrl, "文件索引")}</td>");
            rows.Append($"<td><details class=\"resource-details\"><summary>详情</summary><dl><dt>服务器</dt><dd>{H(server)}</dd><dt>必需</dt><dd>{(item.Required ? "是" : "否")}</dd><dt>SHA-256</dt><dd>{H(pack?.Sha256 ?? "—")}</dd><dt>索引 SHA-256</dt><dd>{H(pack?.FilesSha256 ?? "—")}</dd></dl></details></td></tr>");
        }
        var content = $$"""
<section class="panel manifest-panel">
 <div class="panel-title"><span class="muted">{{manifest.Instances.Count}} 个资源 · 格式版本 {{manifest.FormatVersion}}</span><a class="button secondary small" href="/admin/manifest?format=json">原始 JSON</a></div>
 {{(manifest.Instances.Count == 0 ? "<div class=\"empty\">暂无已发布资源</div>" : "<div class=\"table-scroll\"><table><thead><tr><th>名称 / 标识</th><th>版本</th><th>游戏 / 加载器</th><th>大小</th><th>文件</th><th></th></tr></thead><tbody>" + rows + "</tbody></table></div>")}}
</section>
""";
        await WriteHtmlAsync(context, Layout("资源清单", content));
    }

    private static string ResourceLink(string? url, string label)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")
            ? $"<a class=\"resource-link\" href=\"{H(uri.AbsoluteUri)}\" target=\"_blank\" rel=\"noopener noreferrer\">{H(label)} ↗</a>" : "";

    // ----------------------------------------------------------- New instance

    private static async Task GetInstanceFormAsync(
        HttpContext context,
        AuthService auth,
        Instance? instance,
        string? error,
        Dictionary<string, string>? submitted)
    {
        var csrf = auth.EnsureCsrfCookie(context);
        var isEdit = instance is not null;

        var values = instance is not null
            ? new Dictionary<string, string>
            {
                ["id"] = instance.Id,
                ["name"] = instance.Name,
                ["description"] = instance.Description ?? "",
                ["icon_url"] = instance.IconUrl ?? "",
                ["required"] = instance.Required ? "true" : "",
                ["game_version"] = instance.Minecraft?.GameVersion ?? "",
                ["loader"] = instance.Minecraft?.Loader ?? "",
                ["loader_version"] = instance.Minecraft?.LoaderVersion ?? "",
                ["server_address"] = instance.Server?.Address ?? "",
                ["server_port"] = instance.Server?.Port.ToString() ?? "25565",
            }
            : new Dictionary<string, string> { ["server_port"] = "25565" };
        if (submitted is not null) values = submitted;

        var err = error is null ? "" : $"<p class=\"err\">{H(error)}</p>";
        var action = isEdit
            ? $"/admin/instances/{Uri.EscapeDataString(instance!.Id)}"
            : "/admin/instances/new";

        var idField = isEdit
            ? $"<input type=\"hidden\" name=\"id\" value=\"{H(instance!.Id)}\"><p>整合包标识：<strong>{H(instance!.Id)}</strong></p>"
            : $"<label>整合包标识 <input name=\"id\" required pattern=\"[a-z0-9_-]{{1,64}}\" maxlength=\"64\" value=\"{H(Get(values, "id"))}\" placeholder=\"my-instance\"></label>";

        var content = $$"""
{{err}}
<form novalidate class="panel form-card" method="post" enctype="multipart/form-data" action="{{action}}"><div class="form-grid">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  {{idField}}
  <label>显示名称 <input name="name" required value="{{H(Get(values, "name"))}}"></label>
  <label class="full">简介 <textarea name="description" rows="3">{{H(Get(values, "description"))}}</textarea></label>
  <div class="full icon-field">
    <label for="icon-url">图标（可选）</label>
    <div class="icon-editor"><div class="pack-icon icon-preview" id="icon-preview" aria-label="图标预览"><span class="icon-placeholder" aria-hidden="true"><svg viewBox="0 0 24 24"><rect x="4" y="4" width="16" height="16" rx="3"/><path d="m4 16 5-5 4 4 3-3 4 4"/></svg></span><img id="icon-preview-image" alt="图标预览" referrerpolicy="no-referrer" hidden></div><div class="icon-editor-fields">
    <div class="icon-inputs"><input id="icon-url" name="icon_url" type="url" aria-label="图标网络地址" placeholder="https://" value="{{H(Get(values, "icon_url"))}}">
      <label class="file-select icon-select"><span class="file-select-button">上传图片</span><span id="icon-file-name">未选择文件</span><input id="icon-file" name="icon_file" type="file" accept="image/png,image/jpeg,.png,.jpg,.jpeg" aria-label="上传图标文件"></label>
      <button type="button" class="quiet small" id="icon-file-clear" hidden>取消选择</button></div>
    <small class="field-help">支持PNG / JPG， 最大 2 MiB。</small>
    </div></div>
    <span id="icon-preview-status" class="field-help" role="status" hidden></span>
    <p id="icon-error" class="err" role="alert" hidden></p>
  </div>
  <fieldset>
    <legend>游戏与加载器</legend>
    <label>游戏版本 <input name="game_version" value="{{H(Get(values, "game_version"))}}"></label>
    <label>加载器 <input name="loader" value="{{H(Get(values, "loader"))}}"></label>
    <label>加载器版本 <input name="loader_version" value="{{H(Get(values, "loader_version"))}}"></label>
  </fieldset>
  <fieldset>
    <legend>游戏服务器</legend>
    <label>服务器地址 <input name="server_address" value="{{H(Get(values, "server_address"))}}"></label>
    <label>端口 <input name="server_port" type="number" min="1" max="65535" step="1" inputmode="numeric" value="{{H(Get(values, "server_port"))}}"></label>
  </fieldset>
  </div>
  <label class="check-row required-field"><input name="required" type="checkbox" value="true" {{(Get(values, "required") == "true" ? "checked" : "")}}> 必需整合包</label>
  <div class="actions">
    <button type="submit">{{(isEdit ? "保存修改" : "创建整合包")}}</button>
    <a href="/admin">返回概览</a>
  </div>
</form>
""";

        await WriteHtmlAsync(context, Layout(isEdit ? "编辑整合包" : "新建整合包", content), error is null ? 200 : 422);
    }

    private static async Task PostNewInstanceAsync(HttpContext context, AuthService auth, InstanceStore store, string wwwrootDir)
    {
        var form = await ReadMetadataFormAsync(context);
        if (form is null) return;
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var id = form["id"].ToString().Trim();
        var name = form["name"].ToString().Trim();

        if (!InstanceIdRegex.IsMatch(id))
        {
            await GetInstanceFormAsync(context, auth, null, "标识须为 1–64 位小写字母、数字、短横线或下划线。", ToDict(form));
            return;
        }

        if (store.Exists(id))
        {
            await GetInstanceFormAsync(context, auth, null, $"标识 \"{id}\" 已存在，请使用其他标识。", ToDict(form));
            return;
        }

        if (name.Length == 0)
        {
            await GetInstanceFormAsync(context, auth, null, "请填写显示名称。", ToDict(form));
            return;
        }

        if (!TryParseServerPort(form["server_port"], out var serverPort))
        {
            await GetInstanceFormAsync(context, auth, null, "端口须为 1–65535 的整数。", ToDict(form));
            return;
        }
        string? iconUrl;
        try { iconUrl = await InstanceIcons.ResolveAsync(form, wwwrootDir, store.PublicBaseUrl, context.RequestAborted); }
        catch (InvalidDataException ex)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await GetInstanceFormAsync(context, auth, null, ex.Message, ToDict(form));
            return;
        }
        var instance = new Instance
        {
            Id = id,
            Name = name,
            Description = NullIfEmpty(form["description"]),
            IconUrl = iconUrl,
            Revision = 0,
            Required = IsChecked(form["required"]),
            Minecraft = new MinecraftInfo
            {
                GameVersion = form["game_version"].ToString().Trim(),
                Loader = form["loader"].ToString().Trim(),
                LoaderVersion = form["loader_version"].ToString().Trim(),
            },
            Pack = null,
            Server = new ServerInfo
            {
                Address = form["server_address"].ToString().Trim(),
                Port = serverPort,
            },
        };

        store.Create(instance, append: true);
        Redirect(context, "/admin");
    }

    // ------------------------------------------------------------- Edit

    private static async Task GetEditFormAsync(HttpContext context, AuthService auth, InstanceStore store, string id)
    {
        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await GetInstanceFormAsync(context, auth, instance, null, null);
    }

    private static async Task PostEditAsync(HttpContext context, AuthService auth, InstanceStore store, string id, string wwwrootDir)
    {
        var form = await ReadMetadataFormAsync(context);
        if (form is null) return;
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var existing = store.Get(id);
        if (existing is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var name = form["name"].ToString().Trim();
        if (name.Length == 0)
        {
            await GetInstanceFormAsync(context, auth, existing, "请填写显示名称。", ToDict(form));
            return;
        }

        if (!TryParseServerPort(form["server_port"], out var serverPort))
        {
            await GetInstanceFormAsync(context, auth, existing, "端口须为 1–65535 的整数。", ToDict(form));
            return;
        }
        string? iconUrl;
        try { iconUrl = await InstanceIcons.ResolveAsync(form, wwwrootDir, store.PublicBaseUrl, context.RequestAborted); }
        catch (InvalidDataException ex)
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            await GetInstanceFormAsync(context, auth, existing, ex.Message, ToDict(form));
            return;
        }
        var updated = new Instance
        {
            Id = existing.Id,
            Name = name,
            Description = NullIfEmpty(form["description"]),
            IconUrl = iconUrl,
            Revision = existing.Revision, // preserved by UpdateMetadata too
            Required = IsChecked(form["required"]),
            Minecraft = new MinecraftInfo
            {
                GameVersion = form["game_version"].ToString().Trim(),
                Loader = form["loader"].ToString().Trim(),
                LoaderVersion = form["loader_version"].ToString().Trim(),
            },
            Pack = existing.Pack,
            Server = new ServerInfo
            {
                Address = form["server_address"].ToString().Trim(),
                Port = serverPort,
            },
        };

        store.UpdateMetadata(updated);
        Redirect(context, "/admin");
    }

    // ------------------------------------------------------------- Remove
    private static async Task GetDeleteFormAsync(HttpContext context, AuthService auth, InstanceStore store, string id)
    {
        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var csrf = auth.EnsureCsrfCookie(context);
        var idEnc = Uri.EscapeDataString(instance.Id);
        var content = $$"""
<div class="panel form-card">
 <h2>从列表移除整合包</h2>
 <p>确认将 <strong>{{H(instance.Name)}}</strong>（<code>{{H(instance.Id)}}</code>，当前版本 r{{instance.Revision}}）从资源清单中移除？</p>
 <p class="notice">只会移除列表和签名清单，不会删除 <code>wwwroot/packs/{{H(instance.Id)}}/</code> 下已经发布的文件。需要恢复时可保留这些文件。</p>
 <form method="post" action="/admin/instances/{{idEnc}}/delete">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  <div class="actions">
   <button type="submit" class="secondary danger">确认移除</button>
   <a href="/admin">取消</a>
  </div>
 </form>
</div>
""";
        await WriteHtmlAsync(context, Layout("移除整合包", content));
    }

    private static async Task PostDeleteAsync(HttpContext context, AuthService auth, InstanceStore store, string id)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!store.Delete(id))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Redirect(context, "/admin");
    }

    private static async Task<IFormCollection?> ReadMetadataFormAsync(HttpContext context)
    {
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = InstanceIcons.MaxBytes + 65536;
        try
        {
            return await context.Request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = InstanceIcons.MaxBytes, ValueLengthLimit = 16384,
                ValueCountLimit = 32, MultipartHeadersCountLimit = 16
            }, context.RequestAborted);
        }
        catch (Exception ex) when (ex is InvalidDataException or BadHttpRequestException)
        {
            await WriteHtmlAsync(context, Layout("无法保存", "<p class=\"err\">表单或图标文件过大，图标最大支持 2 MiB。</p><a href=\"/admin\">返回概览</a>"), 413);
            return null;
        }
    }

    private static async Task PostReorderAsync(HttpContext context, AuthService auth, InstanceStore store)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!auth.ValidateCsrf(context, context.Request.Headers["X-CSRF-Token"]))
        { context.Response.StatusCode = 403; return; }
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = 65536;
        try
        {
            var request = await context.Request.ReadFromJsonAsync<ReorderRequest>(context.RequestAborted);
            if (request?.Ids is null || request.Expected is null) throw new ArgumentException("排序列表无效。");
            store.Reorder(request.Ids, request.Expected);
            await context.Response.WriteAsJsonAsync(new { saved = true });
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or BadHttpRequestException)
        {
            context.Response.StatusCode = ex is InvalidOperationException ? 409 : 400;
            await context.Response.WriteAsJsonAsync(new { error = ex is InvalidOperationException ? "整合包列表已变化，请刷新后重新排序。" : "排序列表无效。" });
        }
    }

    private sealed record ReorderRequest(string[] Ids, string[] Expected);

    // ------------------------------------------------------------- Upload

    private static async Task GetUploadFormAsync(HttpContext context, AuthService auth, InstanceStore store, string id, int chunkSize, long uploadLimitBytes)
    {
        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var csrf = auth.EnsureCsrfCookie(context);
        var idEnc = Uri.EscapeDataString(id);
        var nextRevision = instance.Revision + 1;

        var content = $$"""
<div class="upload-layout"><section class="panel">
 <div class="panel-title"><div><h2>{{H(instance.Name)}}</h2><div class="upload-meta"><span>{{H(id)}}</span><span class="tag" id="current-revision">当前版本 {{instance.Revision}}</span><span>下一版本 <strong id="next-revision">{{nextRevision}}</strong></span></div></div></div>
 <form novalidate id="upload-form" data-upload-form data-instance="{{H(id)}}" data-revision="{{instance.Revision}}" data-chunk-size="{{chunkSize}}" data-max-bytes="{{uploadLimitBytes}}" method="post" enctype="multipart/form-data" action="/admin/instances/{{idEnc}}/upload">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  <label class="file-picker upload-dropzone" id="upload-dropzone" for="upload-file" tabindex="0" aria-label="拖拽整合包到此处，或点击选择文件"><svg class="upload-drop-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M12 16V3m-6 6 6-6 6 6M3 16v4a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1v-4"/></svg><span class="file-label">将文件拖入此处，或点击选择文件</span><span class="hint">支持mrpack，最大 {{FormatSize(uploadLimitBytes)}}。</span><span id="selected-file-name">未选择文件</span><input type="file" name="file" id="upload-file" accept=".mrpack" aria-label="选择整合包文件" required><span id="file-summary" class="file-summary" hidden></span></label>
  <ol class="steps" aria-label="发布流程"><li id="step-hash">校验文件</li><li id="step-upload">上传文件</li><li id="step-publish">校验与发布</li><li id="step-done">发布完成</li></ol>
  <div id="upload-progress-panel" hidden>
   <div class="progress-heading"><span id="upload-state">准备上传</span><span id="upload-percent">0%</span></div>
   <progress id="upload-progress" aria-label="当前阶段进度" max="100" value="0"></progress>
   <div class="upload-stats"><span id="upload-bytes">0 / 0 MiB</span><span id="upload-chunks" hidden></span><span id="upload-speed"></span></div>
   <p id="upload-stage" class="upload-stage" role="status" aria-live="polite">请选择整合包。</p>
   <p id="upload-detail" class="upload-detail"></p>
  </div>
  <p id="upload-error" class="err" role="alert" hidden></p>
  <p id="upload-login" hidden><a href="/admin/login">重新登录</a>后可回到此页继续任务。</p>
  <div class="actions"><button type="submit" id="upload-submit">开始上传</button><button type="button" id="upload-pause" class="secondary" disabled>暂停上传</button><button type="button" id="upload-cancel" class="secondary danger" disabled>取消上传</button></div>
  <p id="upload-cancel-confirm" class="notice" hidden>确定取消上传？<button type="button" id="upload-cancel-yes" class="secondary small danger">确认取消</button> <button type="button" id="upload-cancel-no" class="quiet small">暂不取消</button></p>
 </form>
 <div id="upload-result" class="success-box" role="status" hidden><strong id="upload-result-title">发布成功</strong><a href="/admin">返回整合包管理 →</a></div>
 <noscript><p class="err">上传文件需要启用 JavaScript。</p></noscript>
</section></div>
""";
        await WriteHtmlAsync(context, Layout("上传与发布", content));
    }

    private static async Task PostUploadAsync(
        HttpContext context,
        AuthService auth,
        InstanceStore store,
        string id,
        long uploadLimitBytes,
        string wwwrootDir, PackPublishService publisher, int chunkSize = 2 * 1024 * 1024)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            await WriteHtmlAsync(
                context,
                Layout("上传未完成", "<p class=\"err\">请选择要上传的整合包。</p><p><a href=\"/admin\">返回概览</a></p>"));
            return;
        }

        if (file.Length > uploadLimitBytes)
        {
            await WriteHtmlAsync(
                context,
                Layout("上传未完成", "<p class=\"err\">文件超出允许上传的大小。</p><p><a href=\"/admin\">返回概览</a></p>"),
                StatusCodes.Status413PayloadTooLarge);
            return;
        }

        try
        {
            await using var input = file.OpenReadStream();
            await publisher.PublishAsync(id, instance.Revision, input, cancellationToken: context.RequestAborted);
            Redirect(context, "/admin");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            var status = ex is PublicationPendingException ? 503
                : ex is InvalidOperationException ? 409 : 422;
            await WriteHtmlAsync(context, Layout("上传未完成",
                "<p class=\"err\">发布未完成，请检查整合包格式、当前版本及服务器存储状态后重试。</p><p><a href=\"/admin\">返回概览</a></p>"), status);
        }
    }
    private static string Layout(string title, string content)
    {
        return AdminUi.Render(title, content);
    }

    private static async Task WriteHtmlAsync(HttpContext context, string html, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(html);
    }

    private static async Task MethodNotAllowedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        await context.Response.WriteAsync("不支持此请求方式。");
    }

    private static void Redirect(HttpContext context, string location)
        => context.Response.Redirect(location, permanent: false);

    private static string FormatSize(long size) => size >= 1073741824 ? $"{size / 1073741824d:0.##} GiB"
        : size >= 1048576 ? $"{size / 1048576d:0.##} MiB" : $"{size / 1024d:0.##} KiB";

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Get(Dictionary<string, string> d, string key)
        => d.TryGetValue(key, out var v) ? v : "";

    private static string? NullIfEmpty(StringValues v)
    {
        var s = v.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    private static int? ParseNullableInt(StringValues v)
    {
        var s = v.ToString().Trim();
        return int.TryParse(s, out var n) ? n : null;
    }

    private static bool TryParseServerPort(StringValues value, out int port)
    {
        port = 25565;
        if (value.Count > 1) return false;
        var text = value.ToString().Trim();
        return text.Length == 0 || (int.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535);
    }

    private static bool IsChecked(StringValues v)
        => string.Equals(v.ToString(), "true", StringComparison.Ordinal);

    private static Dictionary<string, string> ToDict(IFormCollection form)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in form.Keys)
            d[key] = form[key].ToString();
        return d;
    }


}
