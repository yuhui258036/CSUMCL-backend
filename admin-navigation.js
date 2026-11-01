"use strict";
(() => {
  let sequence = 0;
  const eligible = url => url.origin === location.origin && /^\/admin(?:\/|$)/.test(url.pathname)
    && !url.pathname.startsWith("/admin/assets/") && !url.pathname.startsWith("/admin/api/")
    && !["/admin/login", "/admin/logout"].includes(url.pathname) && !url.search;
  async function navigate(url, options = {}) {
    const current = ++sequence;
    try {
      const response = await fetch(url, { credentials: "same-origin", cache: "no-store", ...options.request });
      const doc = new DOMParser().parseFromString(await response.text(), "text/html");
      if (current !== sequence) return;
      if (doc.body.classList.contains("login-page")) { location.assign(response.url); return; }
      const next = doc.getElementById("main");
      if (!next) throw new Error("页面加载失败，请重试。");
      const main = document.getElementById("main");
      window.CmuUploads.park(main);
      main.replaceWith(next);
      document.title = doc.title;
      if (!options.pop) history.pushState(null, "", response.url);
      window.dispatchEvent(new Event("cmu:navigate"));
      window.scrollTo(0, 0);
      document.querySelector(".shell").scrollTo(0, 0);
      window.CmuUploads.changed();
    } catch {
      let error = document.getElementById("navigation-error");
      if (!error) { error = document.createElement("p"); error.id = "navigation-error"; error.className = "err"; document.getElementById("main").prepend(error); }
      error.textContent = "页面加载失败，请重试。";
    }
  }
  window.CmuNavigate = navigate;
  document.addEventListener("click", event => {
    const anchor = event.target.closest("a[href]");
    if (!anchor || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey
        || anchor.target || anchor.hasAttribute("download")) return;
    const url = new URL(anchor.href);
    if (!eligible(url)) return;
    event.preventDefault(); void navigate(url.href);
  });
  document.addEventListener("submit", event => {
    const form = event.target;
    if (event.defaultPrevented) return;
    if (form.matches("[data-upload-form]") || form.method.toLowerCase() !== "post") return;
    const url = new URL(form.action);
    if (!eligible(url)) return;
    event.preventDefault(); void navigate(url.href, { request: { method: "POST", body: new FormData(form) } });
  });
  window.addEventListener("popstate", () => {
    if (eligible(new URL(location.href))) void navigate(location.href, { pop: true });
    else location.reload();
  });
  // Warn only when file persistence failed and leaving would require reselecting it.
  window.addEventListener("beforeunload", event => {
    window.CmuUploads.persist();
    if (window.CmuUploads.needsWarning()) { event.preventDefault(); event.returnValue = ""; }
  });
})();
