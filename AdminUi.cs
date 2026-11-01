using System.Net;

namespace PclCmu.Admin;

internal static class AdminUi
{
    internal static string Render(string title, string content)
    {
        bool login = title == "后台登录";
        string safeTitle = WebUtility.HtmlEncode(title);
        string navigation = login ? "" : """
<nav aria-label="后台导航">
  <a href="/admin" class="nav-link"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/></svg> 整合包管理</a>
  <a href="/admin/instances/new" class="nav-link"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg> 新建整合包</a>
  <a href="/admin/manifest" class="nav-link"><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="4" width="18" height="16" rx="2"/><path d="M3 9h18M9 9v11"/></svg> 资源清单</a>
</nav>
""";
        string logout = login ? "" : """
<form novalidate method="post" action="/admin/logout" class="logout-form">
<input type="hidden" name="_csrf" data-logout-csrf><button type="submit" class="quiet small" disabled data-logout>退出登录</button></form>
""";
        return $$"""
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{safeTitle}} · CMUmcl 资源管理</title>
<link rel="icon" href="/admin/assets/admin-logo.png">
<script src="/admin/assets/admin.js"></script>
<script src="/admin/assets/admin-validation.js" defer></script>
{{(login ? "" : "<script src=\"/admin/assets/upload-background.js\" defer></script><script src=\"/admin/assets/upload.js\" defer></script><script src=\"/admin/assets/admin-navigation.js\" defer></script>")}}
<link rel="stylesheet" href="/admin/assets/admin.css">
</head>
<body class="{{(login ? "login-page" : "admin-page")}}">
<div class="shell">
 <aside class="sidebar">
  <a class="brand" href="/admin"><img src="/admin/assets/admin-logo.png" width="40" height="40" alt=""><span>CMUmcl<small>资源管理</small></span></a>
  {{navigation}}
  <div class="sidebar-tools">
   <button type="button" id="theme-toggle" class="icon-button" title="切换主题" aria-label="切换主题">
    <svg class="moon" viewBox="0 0 24 24" aria-hidden="true"><path d="M20.9 13A9 9 0 1 1 11 3a7 7 0 0 0 9.9 10Z"/></svg>
    <svg class="sun" viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/></svg>
   </button>
  </div>
 </aside>
 <div class="workspace">
  <main id="main"><div class="page-heading"><h1>{{safeTitle}}</h1><div class="toolbar">{{logout}}</div></div>
  {{content}}
  </main>
 </div>
</div>
</body></html>
""";
    }
}
