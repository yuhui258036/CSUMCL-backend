"use strict";
(() => {
  let theme;
  try { theme = localStorage.getItem("cmumcl-admin-theme"); } catch {}
  if (theme !== "dark" && theme !== "light") theme = matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  document.documentElement.dataset.theme = theme;
  let pageCleanup = [];
  function cleanPage() { for (const cleanup of pageCleanup) cleanup(); pageCleanup = []; }
  window.addEventListener("pagehide", cleanPage);
  function initializePage() {
    cleanPage();
    const button = document.getElementById("theme-toggle");
    const label = () => { button.title = button.ariaLabel = document.documentElement.dataset.theme === "dark" ? "切换到亮色模式" : "切换到暗色模式"; };
    label();
    if (!button.dataset.bound) button.addEventListener("click", () => {
      const next = document.documentElement.dataset.theme === "dark" ? "light" : "dark";
      document.documentElement.dataset.theme = next;
      try { localStorage.setItem("cmumcl-admin-theme", next); } catch {}
      label();
    });
    button.dataset.bound = "true";
    const csrf = document.cookie.split("; ").find(item => item.startsWith("pcl_cmu_csrf="))?.slice("pcl_cmu_csrf=".length);
    if (csrf) {
      document.querySelectorAll("[data-logout-csrf]").forEach(input => input.value = decodeURIComponent(csrf));
      document.querySelectorAll("[data-logout]").forEach(input => input.disabled = false);
    }
    document.querySelectorAll(".nav-link").forEach(link => {
      link.removeAttribute("aria-current");
      if (link.pathname === location.pathname || (link.pathname === "/admin" && location.pathname.startsWith("/admin/instances/") && !location.pathname.endsWith("/new"))) link.setAttribute("aria-current", "page");
    });
    document.querySelectorAll(".pack-icon-image").forEach(image => {
      const show = () => {
        const loaded = image.naturalWidth > 0;
        image.hidden = !loaded;
        image.parentElement.classList.toggle("has-image", loaded);
      };
      image.addEventListener("load", show);
      image.addEventListener("error", show);
      image.loading = "eager";
      if (image.complete) show();
    });
    const iconFile = document.getElementById("icon-file");
    if (iconFile && !iconFile.dataset.bound) {
      iconFile.dataset.bound = "true";
      const clear = document.getElementById("icon-file-clear");
      const error = document.getElementById("icon-error");
      const url = document.getElementById("icon-url");
      const preview = document.getElementById("icon-preview");
      const previewImage = document.getElementById("icon-preview-image");
      const previewStatus = document.getElementById("icon-preview-status");
      let objectUrl = null, previewVersion = 0, debounce = 0;
      const updatePreview = () => {
        const version = ++previewVersion;
        clearTimeout(debounce);
        previewImage.hidden = true;
        previewImage.removeAttribute("src");
        preview.classList.remove("has-image");
        previewStatus.hidden = true;
        if (objectUrl) { URL.revokeObjectURL(objectUrl); objectUrl = null; }
        const file = iconFile.files[0];
        let source = "";
        if (file) {
          if (file.size > 2 * 1024 * 1024) return;
          source = objectUrl = URL.createObjectURL(file);
        } else {
          try {
            const parsed = new URL(url.value.trim());
            if (!["http:", "https:"].includes(parsed.protocol) || parsed.username || parsed.password) return;
            source = parsed.href;
          } catch { return; }
        }
        const image = new Image();
        image.referrerPolicy = "no-referrer";
        previewStatus.textContent = "正在加载预览…";
        previewStatus.hidden = false;
        image.onload = () => {
          if (version !== previewVersion) return;
          previewImage.src = source;
          previewImage.hidden = false;
          preview.classList.add("has-image");
          previewStatus.hidden = true;
        };
        image.onerror = () => {
          if (version !== previewVersion) return;
          previewStatus.textContent = "图标无法预览";
        };
        image.src = source;
      };
      const update = () => {
        const file = iconFile.files[0];
        document.getElementById("icon-file-name").textContent = file?.name || "未选择文件";
        clear.hidden = !file;
        const message = file?.size > 2 * 1024 * 1024 ? "图标不能超过 2 MiB。" : "";
        error.textContent = message;
        error.hidden = !message;
        updatePreview();
      };
      iconFile.addEventListener("change", update);
      clear.addEventListener("click", () => { iconFile.value = ""; update(); });
      url.addEventListener("input", () => {
        ++previewVersion;
        clearTimeout(debounce);
        debounce = setTimeout(updatePreview, 300);
      });
      url.addEventListener("change", updatePreview);
      pageCleanup.push(() => {
        ++previewVersion;
        clearTimeout(debounce);
        if (objectUrl) URL.revokeObjectURL(objectUrl);
      });
      updatePreview();
    }

    const list = document.getElementById("instance-list");
    if (!list || list.dataset.bound) return;
    list.dataset.bound = "true";
    const cards = () => [...list.querySelectorAll(".instance-card")];
    const ids = () => cards().map(card => card.dataset.instanceId);
    const status = document.getElementById("reorder-status");
    let busy = false, drag = null, scrollFrame = 0;
    const restore = order => {
      const byId = new Map(cards().map(card => [card.dataset.instanceId, card]));
      order.forEach(id => list.append(byId.get(id)));
    };
    const message = (text, failed = false) => {
      status.textContent = text;
      status.hidden = !text;
      status.className = failed ? "err" : "muted";
    };
    async function save(expected, handle) {
      const order = ids();
      if (order.every((id, n) => id === expected[n])) return;
      busy = true;
      list.setAttribute("aria-busy", "true");
      list.querySelectorAll(".drag-handle").forEach(button => button.disabled = true);
      message("正在保存排序…");
      try {
        const response = await fetch("/admin/instances/reorder", {
          method: "POST", credentials: "same-origin", redirect: "error", keepalive: true,
          headers: { "Content-Type": "application/json", "X-CSRF-Token": list.dataset.csrf },
          body: JSON.stringify({ ids: order, expected }), signal: AbortSignal.timeout(15000)
        });
        if (!response.ok) {
          if (response.status === 409) throw new Error("整合包列表已变化，请刷新后重新排序。");
          throw new Error("排序保存失败，请刷新后重试。");
        }
        message("排序已保存");
      } catch (error) {
        restore(expected);
        message(error.message.includes("刷新") ? error.message : "排序保存失败，请刷新后重试。", true);
      } finally {
        busy = false;
        list.removeAttribute("aria-busy");
        list.querySelectorAll(".drag-handle").forEach(button => button.disabled = false);
        handle.focus({ preventScroll: true });
      }
    }
    const place = y => {
      const others = cards().filter(card => card !== drag.card);
      const next = others.find(card => {
        const rect = card.getBoundingClientRect();
        return y < rect.top + rect.height / 2;
      });
      if (drag.card.nextElementSibling === (next || null)) return;
      const before = new Map(others.map(card => [card, card.getBoundingClientRect().top]));
      list.insertBefore(drag.card, next || null);
      if (!matchMedia("(prefers-reduced-motion: reduce)").matches) {
        others.forEach(card => {
          card.getAnimations().forEach(animation => animation.cancel());
          const dy = before.get(card) - card.getBoundingClientRect().top;
          if (dy) card.animate([{ transform: `translateY(${dy}px)` }, { transform: "translateY(0)" }], { duration: 160, easing: "ease-out" });
        });
      }
    };
    const autoScroll = () => {
      if (!drag) return;
      const scroller = document.body.classList.contains("tasks-open") ? document.querySelector(".shell") : window;
      const bounds = scroller === window ? { top: 0, bottom: innerHeight } : scroller.getBoundingClientRect();
      const speed = drag.y < bounds.top + 70 ? -12 : drag.y > bounds.bottom - 70 ? 12 : 0;
      if (speed) { scroller.scrollBy(0, speed); place(drag.y); }
      scrollFrame = requestAnimationFrame(autoScroll);
    };
    const finish = cancelled => {
      if (!drag) return;
      const current = drag;
      drag = null;
      cancelAnimationFrame(scrollFrame);
      current.card.classList.remove("dragging");
      current.ghost?.remove();
      document.body.classList.remove("sorting");
      if (list.hasPointerCapture(current.pointer)) list.releasePointerCapture(current.pointer);
      if (cancelled) restore(current.expected);
      else void save(current.expected, current.handle);
    };
    list.addEventListener("pointerdown", event => {
      const handle = event.target.closest(".drag-handle");
      if (!handle || busy || drag || event.button !== 0) return;
      event.preventDefault();
      handle.focus({ preventScroll: true });
      const card = handle.closest(".instance-card");
      const rect = card.getBoundingClientRect();
      drag = { handle, card, expected: ids(), pointer: event.pointerId, y: event.clientY,
        startX: event.clientX, startY: event.clientY, offsetX: event.clientX - rect.left,
        offsetY: event.clientY - rect.top, width: rect.width, height: rect.height, ghost: null };
      // Capture on the stationary list: moving a card must not lose capture.
      list.setPointerCapture(event.pointerId);
    });
    list.addEventListener("pointermove", event => {
      if (!drag || event.pointerId !== drag.pointer) return;
      if (!drag.ghost) {
        if (Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) < 4) return;
        const ghost = drag.card.cloneNode(true);
        ghost.removeAttribute("data-instance-id");
        ghost.classList.add("drag-ghost");
        ghost.setAttribute("aria-hidden", "true");
        ghost.inert = true;
        ghost.style.width = `${drag.width}px`;
        ghost.style.height = `${drag.height}px`;
        document.body.append(ghost);
        drag.ghost = ghost;
        drag.card.classList.add("dragging");
        document.body.classList.add("sorting");
        scrollFrame = requestAnimationFrame(autoScroll);
      }
      drag.y = event.clientY;
      drag.ghost.style.transform = `translate3d(${event.clientX - drag.offsetX}px, ${event.clientY - drag.offsetY}px, 0)`;
      place(drag.y);
    });
    list.addEventListener("pointerup", () => finish(false));
    list.addEventListener("pointercancel", () => finish(true));
    list.addEventListener("lostpointercapture", () => finish(true));
    const cancelDrag = () => finish(true);
    window.addEventListener("blur", cancelDrag);
    pageCleanup.push(() => { cancelDrag(); window.removeEventListener("blur", cancelDrag); });
    list.addEventListener("keydown", event => {
      if (event.key === "Escape" && drag) { event.preventDefault(); finish(true); return; }
      const handle = event.target.closest(".drag-handle");
      if (!handle || busy || drag || !["ArrowUp", "ArrowDown"].includes(event.key)) return;
      event.preventDefault();
      const expected = ids(), card = handle.closest(".instance-card");
      if (event.key === "ArrowUp" && card.previousElementSibling) list.insertBefore(card, card.previousElementSibling);
      if (event.key === "ArrowDown" && card.nextElementSibling) list.insertBefore(card.nextElementSibling, card);
      void save(expected, handle);
    });
  }
  document.addEventListener("DOMContentLoaded", initializePage);
  window.addEventListener("cmu:navigate", initializePage);
})();
