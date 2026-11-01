"use strict";
(() => {
  const errors = new WeakMap();
  let sequence = 0;
  function message(field) {
    const value = field.value.trim();
    if (field.disabled) return "";
    switch (field.name) {
      case "password": return field.value ? "" : "请输入管理员密码。";
      case "id": return /^[a-z0-9_-]{1,64}$/.test(value) ? "" : "标识须为 1–64 位小写字母、数字、短横线或下划线。";
      case "name": return value ? "" : "请填写显示名称。";
      case "server_port": return !field.validity.badInput && (!value || /^\d+$/.test(value) && Number(value) >= 1 && Number(value) <= 65535) ? "" : "端口须为 1–65535 的整数。";
      case "icon_url":
        if (!value || field.form.elements.icon_file?.files[0]?.size) return "";
        try {
          const url = new URL(value);
          if (value.length <= 2048 && ["http:", "https:"].includes(url.protocol) && !url.username && !url.password) return "";
        } catch {}
        return "请填写有效的 HTTP 或 HTTPS 图标地址。";
      case "icon_file": {
        const file = field.files[0];
        if (!file) return "";
        if (file.size > 2 * 1024 * 1024) return "图标不能超过 2 MiB。";
        return /\.(png|jpe?g)$/i.test(file.name) && file.size ? "" : "请选择有效的 PNG 或 JPG 图片。";
      }
      default: return field.validity.valid ? "" : "请检查此项填写内容。";
    }
  }
  function validate(field) {
    const text = message(field);
    let error = errors.get(field);
    if (text && !error) {
      error = document.createElement("span");
      error.className = "field-error"; error.id = "field-error-" + (++sequence);
      error.setAttribute("role", "alert");
      (field.closest(".icon-editor-fields") || field.closest("label") || field.parentElement).append(error);
      errors.set(field, error);
      field.setAttribute("aria-describedby", [field.getAttribute("aria-describedby"), error.id].filter(Boolean).join(" "));
    }
    if (error) { error.textContent = text; error.hidden = !text; }
    if (text) field.setAttribute("aria-invalid", "true"); else field.removeAttribute("aria-invalid");
    return !text;
  }
  document.addEventListener("submit", event => {
    const form = event.target;
    if (form.matches("[data-upload-form]")) return;
    let first;
    for (const field of form.querySelectorAll("input:not([type=hidden]),textarea,select")) {
      if (!validate(field)) first ||= field;
    }
    if (first) { event.preventDefault(); event.stopImmediatePropagation(); first.focus(); }
  }, true);
  for (const type of ["input", "change"]) document.addEventListener(type, event => {
    const field = event.target;
    if (errors.has(field)) validate(field);
    if (field.name === "icon_file") {
      const url = field.form?.elements.icon_url;
      if (url && errors.has(url)) validate(url);
    }
  });
  document.addEventListener("invalid", event => { event.preventDefault(); validate(event.target); }, true);
})();
