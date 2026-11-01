"use strict";
(() => {
  const controllers = new Map();
  let database, tray, taskList, taskHost, bubble, badge, closeButton, expanded = false;
  const open = () => database ||= new Promise((resolve, reject) => {
    const request = indexedDB.open("cmumcl-upload-files", 1);
    request.onupgradeneeded = () => request.result.createObjectStore("files");
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
  async function cache(mode, instance, value) {
    const db = await open();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction("files", mode === "get" ? "readonly" : "readwrite");
      const store = transaction.objectStore("files");
      const request = mode === "get" ? store.get(instance) : mode === "put" ? store.put(value, instance) : store.delete(instance);
      transaction.oncomplete = () => resolve(request.result);
      transaction.onerror = transaction.onabort = () => reject(transaction.error || new Error("File cache unavailable"));
    });
  }
  const make = (tag, className, text) => {
    const element = document.createElement(tag);
    element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  };
  function setExpanded(value, focus = false) {
    const next = Boolean(value && !bubble.hidden), changing = next !== expanded;
    const shell = document.querySelector(".shell");
    const scroll = expanded ? shell.scrollTop : window.scrollY;
    expanded = next;
    tray.hidden = !expanded;
    bubble.setAttribute("aria-expanded", String(expanded));
    document.body.classList.toggle("tasks-open", expanded);
    if (changing) { if (expanded) shell.scrollTop = scroll; else window.scrollTo(0, scroll); }
    if (focus) (expanded ? closeButton : bubble.hidden ? document.getElementById("theme-toggle") : bubble)?.focus({ preventScroll: true });
  }
  function render() {
    if (!taskHost) { taskHost = make("div", "upload-task-host"); taskHost.hidden = true; document.body.append(taskHost); }
    if (!tray) {
      // MCSManager-style compact upload entry; the opened panel reserves layout space.
      bubble = make("button", "icon-button upload-bubble"); bubble.id = "upload-tasks-toggle"; bubble.type = "button";
      bubble.setAttribute("aria-controls", "upload-task-panel"); bubble.setAttribute("aria-expanded", "false");
      bubble.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 17H6a4 4 0 0 1-.5-7.97 6.5 6.5 0 0 1 12.5-1.2A4.6 4.6 0 0 1 18 17h-1M12 21V11m-4 4 4-4 4 4"/></svg>';
      badge = make("span", "upload-badge"); badge.setAttribute("aria-hidden", "true"); bubble.append(badge);
      bubble.addEventListener("click", () => setExpanded(!expanded, true));
      document.querySelector(".sidebar-tools").append(bubble);
      tray = make("aside", "upload-tray"); tray.id = "upload-task-panel"; tray.setAttribute("aria-label", "后台上传");
      const header = make("div", "upload-tray-header"); header.append(make("h2", "", "后台上传"));
      closeButton = make("button", "icon-button"); closeButton.type = "button";
      closeButton.setAttribute("aria-label", "收起上传任务"); closeButton.title = "收起上传任务";
      closeButton.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M18 6 6 18"/></svg>';
      closeButton.addEventListener("click", () => setExpanded(false, true)); header.append(closeButton);
      taskList = make("div", "upload-task-list"); tray.append(header, taskList);
      document.body.append(tray);
      document.addEventListener("keydown", event => {
        if (event.key === "Escape" && expanded) { event.preventDefault(); setExpanded(false, true); }
      });
    }
    const wanted = new Set(), summaries = [];
    for (const [id, controller] of controllers) {
      const state = controller.snapshot();
      // Keep forms in this document so requestSubmit and cached file inputs work after reload.
      if (!controller.root.closest("#main")?.isConnected && controller.root.parentElement !== taskHost) taskHost.append(controller.root);
      if (!state.visible) continue;
      wanted.add(id);
      summaries.push(state.name + " · " + state.label + " · " + state.percent);
      let item = [...taskList.children].find(child => child.dataset.instance === id);
      if (!item) {
        item = make("section", "upload-tray-item"); item.dataset.instance = id;
        item.append(make("strong", "tray-name"), make("span", "tray-state"), make("progress", "tray-progress"), make("span", "tray-percent"));
        const actions = make("div", "tray-actions");
        for (const [action, text] of [["open", "查看"], ["resume", "继续"], ["pause", "暂停"], ["cancel", "取消"], ["dismiss", "关闭"]]) {
          const button = make("button", "secondary small", text); button.type = "button"; button.dataset.action = action;
          button.addEventListener("click", () => {
            if (action === "open") { setExpanded(false); window.CmuNavigate("/admin/instances/" + encodeURIComponent(id) + "/upload"); }
            else if (action === "dismiss") { controller.dismiss(); render(); }
            else controller.action(action);
          });
          actions.append(button);
        }
        item.append(actions);
        const confirmation = make("div", "tray-confirm");
        confirmation.append(make("span", "", "确定取消上传？"));
        for (const [action, text] of [["confirm", "确认取消"], ["keep", "暂不取消"]]) {
          const button = make("button", "secondary small", text); button.type = "button";
          button.addEventListener("click", () => controller.action(action)); confirmation.append(button);
        }
        item.append(confirmation); taskList.append(item);
      }
      item.querySelector(".tray-name").textContent = state.name;
      item.querySelector(".tray-state").textContent = state.label;
      item.querySelector(".tray-percent").textContent = state.percent;
      const progress = item.querySelector("progress"); progress.max = 100;
      if (state.indeterminate) progress.removeAttribute("value"); else progress.value = state.value;
      for (const action of ["resume", "pause", "cancel", "dismiss"])
        item.querySelector(`[data-action=${action}]`).hidden = !state[action];
      item.querySelector(".tray-confirm").hidden = !state.confirm;
    }
    const restoreFocus = tray.contains(document.activeElement);
    for (const item of [...taskList.children]) if (!wanted.has(item.dataset.instance)) item.remove();
    bubble.hidden = !wanted.size;
    badge.textContent = wanted.size > 99 ? "99+" : String(wanted.size);
    bubble.setAttribute("aria-label", "上传任务（" + wanted.size + "）");
    bubble.title = "上传任务（" + wanted.size + "）\n" + summaries.join("\n");
    setExpanded(expanded && wanted.size > 0, restoreFocus && !wanted.size);
  }
  window.CmuUploads = {
    get: id => controllers.get(id), register: (id, controller) => { controllers.set(id, controller); render(); },
    changed: render,
    park: main => {
      render();
      for (const controller of controllers.values()) {
        if (main.contains(controller.root)) { controller.persist(); taskHost.append(controller.root); }
      }
    },
    getFile: id => cache("get", id),
    putFile: (id, file) => cache("put", id, { file, name: file.name, modified: file.lastModified }),
    removeFile: id => cache("delete", id).catch(() => {}),
    needsWarning: () => [...controllers.values()].some(controller => { const state = controller.snapshot(); return state.running && !state.recoverable; }),
    persist: () => { for (const controller of controllers.values()) controller.persist(); }
  };
  // Recreate detached task views on any admin page after a reload, not only on /upload.
  document.addEventListener("DOMContentLoaded", async () => {
    if (!document.body.classList.contains("admin-page")) return;
    const instances = new Set();
    try {
      for (let n = 0; n < localStorage.length; n++) {
        const key = localStorage.key(n);
        if (key.startsWith("pcl-upload:")) instances.add(key.slice(11));
        if (key.startsWith("pcl-upload-state:")) instances.add(key.slice(17));
      }
    } catch {}
    for (const id of instances) {
      if (!/^[a-z0-9_-]{1,64}$/.test(id) || controllers.has(id)) continue;
      try {
        const response = await fetch("/admin/instances/" + id + "/upload", { cache: "no-store" });
        if (!response.ok) continue;
        const doc = new DOMParser().parseFromString(await response.text(), "text/html");
        const root = doc.querySelector(".upload-layout");
        if (root && !controllers.has(id)) { root.remove(); window.CmuMountUpload(root); }
      } catch { /* The next visit retries recovery. */ }
    }
  });
})();
