"use strict";
(() => {
 function mount(root) {
  const runtime = typeof window !== "undefined" ? window.CmuUploads : null;
  const el = id => root ? root.querySelector("#" + id) : document.getElementById(id);
  const form = el("upload-form");
  if (!form) return;
  const csrf = form.elements._csrf.value, instance = form.dataset.instance, key = "pcl-upload:" + instance;
  const existing = runtime?.get(instance);
  if (existing) { if (root !== existing.root) root.replaceWith(existing.root); existing.attach(); runtime.changed(); return; }
  const submit = el("upload-submit"), pause = el("upload-pause"), cancel = el("upload-cancel"), input = el("upload-file");
  let running = false, stopped = false, task = null, grant = null, renewing = null, worker = null, cancelHash = null, phase = "idle";
  const uiKey = "pcl-upload-state:" + instance;
  let transferBytes = 0, selectedFile = null, fileWrite = Promise.resolve(), cachedHashes = null, dismissed = false;
  let lastPersist = 0, fileCached = false;
  let activeRun = null, cancelling = false, selectionVersion = 0;
  form.noValidate = true;
  input.required = false;
  function showSelection() {
    // The retained File is authoritative; moving a form must not depend on its native FileList.
    if (selectedFile && !input.files[0]) {
      try { const transfer = new DataTransfer(); transfer.items.add(selectedFile); input.files = transfer.files; } catch {}
    }
    el("selected-file-name").textContent = selectedFile?.name || "未选择文件";
    el("file-summary").hidden = !selectedFile && !task;
    el("file-summary").textContent = selectedFile ? (selectedFile.size / 1048576).toFixed(2) + " MiB" : task ? (task.size / 1048576).toFixed(2) + " MiB" : "";
  }
  const savedUi = () => { try { return JSON.parse(localStorage.getItem(uiKey) || "null"); } catch { return null; } };
  function changed(force = false) {
    runtime?.changed();
    if (!runtime || !task && !selectedFile) return;
    if (phase === "completed") { try { localStorage.removeItem(uiKey); } catch {} return; }
    if (!force && Date.now() - lastPersist < 300) return;
    lastPersist = Date.now();
    try { localStorage.setItem(uiKey, JSON.stringify({ bytes: transferBytes, id: task?.id,
      auto: running && !stopped && ["hashing", "uploading", "queued", "verifying", "restoring"].includes(phase), phase })); } catch {}
  }
  async function cacheFile(file) {
    if (!runtime || !file || fileCached) return;
    try { await runtime.putFile(instance, file); if (file === selectedFile) fileCached = true; }
    catch { fileCached = false; el("upload-error").hidden = false; el("upload-error").textContent = "文件无法保存在浏览器中，刷新后请重新选择。"; }
  }
  const requests = new Set();
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  const remembered = () => { try { return JSON.parse(localStorage.getItem(key) || "null"); } catch { return null; } };
  const remember = () => { try { localStorage.setItem(key, JSON.stringify({id:task.id, sha256:task.sha256, baseRevision:task.baseRevision})); } catch {} };
  const forget = (keepFile = false) => { try { localStorage.removeItem(key); localStorage.removeItem(uiKey); } catch {} if (!keepFile) { fileCached = false; runtime?.removeFile(instance); } };
  const isPublishing = () => task && ["queued", "verifying"].includes(task.state);
  const canCancel = () => !cancelling && (running ? ["hashing", "uploading"].includes(phase)
    : task && !["completed", "cancelled", "queued", "verifying"].includes(task.state));
  const labels = {idle:"准备上传", hashing:"正在校验文件", uploading:"正在上传", queued:"等待服务器校验", verifying:"正在校验与发布", completed:"发布成功", paused:"已暂停", failed:"上传失败", cancelled:"已取消", restoring:"正在恢复上传"};
  function state(next, message) {
    const previousPhase = phase;
    phase = next; form.dataset.state = next;
    el("upload-state").textContent = next === "paused" && previousPhase === "hashing" ? "已暂停" : labels[next] || next;
    el("upload-stage").textContent = next === "paused" ? "已暂停。" : next === "uploading" ? "正在上传。" : message;
    const current = {hashing:0, uploading:1, paused:previousPhase === "hashing" ? 0 : 1, queued:2, verifying:2, completed:4, failed:task?.state === "failed" ? 2 : -1}[next] ?? -1;
    ["hash", "upload", "publish", "done"].forEach((step, index) => el("step-" + step).className = index < current ? "complete" : index === current ? "active" : "");
    if (["queued", "verifying", "restoring"].includes(next)) {
      el("upload-progress").removeAttribute("value"); el("upload-percent").textContent = "处理中";
      el("upload-speed").textContent = "";
    }
    controls();
  }
  function controls() {
    submit.disabled = running || cancelling || task?.state === "failed"; input.disabled = running || cancelling;
    input.required = false;
    pause.disabled = cancelling || !running || !["hashing", "uploading"].includes(phase);
    cancel.disabled = !canCancel();
    if (cancel.disabled && !cancelling) el("upload-cancel-confirm").hidden = true;
    submit.textContent = isPublishing() ? "刷新发布结果" : task?.state === "failed" ? "请先取消上传" : task?.state === "receiving" ? "继续上传" : phase === "completed" ? "上传新版本" : "开始上传";
    changed(true);
  }
  const translations = [
    [/Instance revision changed|revision changed|Revision conflict/i, "整合包版本已更新，请刷新后重新上传。"],
    [/Full file SHA-256|SHA-256 does not match|hash mismatch/i, "文件校验不一致，请重新上传。"],
    [/No safe HTTPS download entries|Unsafe or unsupported pack entries/i, "整合包中没有有效的下载条目，或包含不支持的路径与下载地址，请检查整合包。"],
    [/modrinth.index|Invalid.*pack|Invalid.*archive|central directory/i, "整合包内容无效，请检查 .mrpack 格式及 modrinth.index.json。"],
    [/Insufficient free disk|quota|Too many upload sessions/i, "服务器可用空间或上传配额不足，请清理未完成任务后重试。"],
    [/Storage|storage|publication recovery/i, "服务器存储暂时不可用。任务已保留，请稍后重试或联系维护人员。"],
    [/Upload.*expired|Session.*expired/i, "上传任务已过期，请重新选择文件上传。"],
    [/exceeds.*limit|too large/i, "文件或请求超出服务器允许的大小。"],
    [/cannot.*cancel|cannot.*cancelled|cannot.*canceled/i, "服务器正在发布，暂时无法取消。请等待发布结果。"],
    [/Wait for active requests/i, "正在处理上传，请稍后重试。"],
    [/missing chunks|Incomplete|not all chunks/i, "文件尚未上传完成，请继续上传。"]
  ];
  function errorText(error) {
    if (error.status === 401) return "登录已过期，请重新登录。";
    const raw = error.message || String(error);
    if (/[\u3400-\u9fff]/.test(raw)) return raw;
    for (const [pattern, message] of translations) if (pattern.test(raw)) return message;
    return ({403:"请求被拒绝，请刷新页面或重新登录后重试。",404:"上传任务不存在，请刷新后重新上传。",409:"任务状态发生变化，请刷新页面确认最新版本。",410:"上传任务已过期，请重新上传。",413:"文件超出服务器允许的大小。",429:"服务器上传任务较多，请稍后重试。",503:"服务器暂时不可用，请稍后重试。"})[error.status] || "操作未完成，请检查网络后重试；如仍失败，请联系后台维护人员。";
  }
  function showError(error) {
    const failed = task?.state === "failed";
    const pending = !failed && (isPublishing() || ["queued", "verifying"].includes(phase));
    state("failed", failed ? "校验失败，请检查文件后重新上传。" : pending ? "正在发布，请稍后查看结果。" : "上传未完成，请重试或取消上传。");
    if (task) progress(task.received.reduce((sum, index) => sum + Math.min(task.chunkSize, task.size - index * task.chunkSize), 0), task.size);
    else { progress(0, input.files[0]?.size || 0); el("upload-percent").textContent = "未上传"; }
    if (task?.state === "failed") el("upload-percent").textContent = "未发布";
    else if (pending) el("upload-percent").textContent = "待确认";
    el("upload-speed").textContent = "";
    el("upload-error").hidden = false; el("upload-error").textContent = errorText(error);
    el("upload-login").hidden = error.status !== 401;
  }
  function clearError() { el("upload-error").hidden = true; el("upload-login").hidden = true; }
  function progress(bytes, total) {
    if (["uploading", "paused", "restoring"].includes(phase)) {
      transferBytes = Math.min(total, Math.max(transferBytes, bytes)); bytes = transferBytes;
    } else if (phase === "hashing" && task) bytes = transferBytes;
    const percent = total ? Math.min(100, bytes / total * 100) : 0;
    el("upload-progress").value = percent;
    el("upload-percent").textContent = percent.toFixed(1) + "%";
    el("upload-bytes").textContent = (bytes / 1048576).toFixed(2) + " / " + (total / 1048576).toFixed(2) + " MiB";
    changed();
  }
  function taskDetails() {
    if (!task) return;
    el("upload-chunks").textContent = "已确认 " + task.received.length + " / " + task.chunkCount;
    el("file-summary").hidden = false;
    el("file-summary").textContent = (task.size / 1048576).toFixed(2) + " MiB";
    el("upload-detail").textContent = "目标版本 " + (task.baseRevision + 1);
  }
  async function api(path, method = "GET", body) {
    const response = await fetch("/admin/api/uploads" + path, {method, credentials:"same-origin", cache:"no-store",
      headers:{"Content-Type":"application/json", "X-CSRF-Token":csrf}, body:body === undefined ? undefined : JSON.stringify(body)});
    const value = response.status === 204 ? null : await response.json().catch(() => ({error:"服务响应异常，请检查登录状态。"}));
    if (!response.ok) { const error = new Error(value.error || "HTTP " + response.status); error.status = response.status; throw error; }
    return value;
  }
  function checkStopped() { if (stopped) throw new DOMException("已暂停。", "AbortError"); }
  function hashFile(file, chunkSize) {
    state("hashing", "正在校验文件。");
    el("upload-chunks").textContent = "本地校验"; el("upload-speed").textContent = "";
    progress(0, file.size);
    return new Promise((resolve, reject) => {
      worker = new Worker("/admin/assets/sha256-worker.js");
      const finish = (error, value) => { worker?.terminate(); worker = null; cancelHash = null; error ? reject(error) : resolve(value); };
      cancelHash = () => finish(new DOMException("已暂停文件校验。", "AbortError"));
      worker.onmessage = ({data}) => {
        if (data.error) finish(new Error(data.error));
        else if (data.sha256) finish(null, data);
        else progress(data.progress, file.size);
      };
      worker.onerror = () => finish(new Error("文件校验失败，请重试。"));
      worker.onmessageerror = () => finish(new Error("文件校验失败，请重试。"));
      worker.postMessage({file, chunkSize});
    });
  }
  async function renew() {
    if (!renewing) renewing = api("/" + task.id + "/renew", "POST").then(value => grant = value).finally(() => renewing = null);
    await renewing;
  }
  function sendChunk(index, blob, hash, onProgress) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest(); requests.add(xhr);
      const finish = error => { requests.delete(xhr); error ? reject(error) : resolve(); };
      xhr.open("PUT", task.chunkBaseUrl + index); xhr.timeout = 120000; xhr.withCredentials = false;
      xhr.setRequestHeader("Content-Type", "application/octet-stream");
      xhr.setRequestHeader("Authorization", "Bearer " + grant.token); xhr.setRequestHeader("X-Chunk-SHA256", hash);
      xhr.upload.onprogress = event => onProgress(event.loaded);
      xhr.onload = () => {
        if (xhr.status === 204) { finish(); return; }
        let message = "HTTP " + xhr.status;
        try { message = JSON.parse(xhr.responseText).error || message; } catch {}
        const error = new Error(message); error.status = xhr.status; finish(error);
      };
      xhr.onerror = () => { const error = new Error("网络连接中断，请重试。"); error.status = 0; finish(error); };
      xhr.ontimeout = () => { const error = new Error("上传超时，请重试。"); error.status = 0; finish(error); };
      xhr.onabort = () => finish(new DOMException("已暂停。", "AbortError"));
      xhr.send(blob);
    });
  }
  async function poll() {
    while (true) {
      task = await api("/" + task.id); taskDetails();
      if (task.state === "completed") {
        progress(task.size, task.size); state("completed", "发布完成：版本 " + task.revision + "。");
        el("upload-speed").textContent = ""; el("upload-result").hidden = false;
        el("upload-result-title").textContent = "版本 " + task.revision + " 发布成功";
        form.dataset.revision = String(task.revision); el("current-revision").textContent = "当前版本 " + task.revision; el("next-revision").textContent = String(task.revision + 1);
        forget(); return;
      }
      if (task.state === "failed") throw new Error(task.error || "校验失败，请取消上传后重试。");
      if (task.state === "cancelled") { forget(true); task = null; transferBytes = 0; state("cancelled", "已取消。"); progress(0, 0); return; }
      if (!isPublishing()) return;
      state(task.state, task.state === "queued" ? "上传完成，等待校验。" : "正在校验与发布。");
      await sleep(1000);
    }
  }
  async function run(event) {
    event.preventDefault(); if (running || cancelling) return;
    if (!selectedFile && !input.files[0] && !isPublishing()) {
      el("upload-error").textContent = "请选择要上传的 .mrpack 文件。";
      el("upload-error").hidden = false;
      return;
    }
    running = true; stopped = false; clearError(); el("upload-result").hidden = true; el("upload-cancel-confirm").hidden = true;
    el("upload-progress-panel").hidden = false; state("hashing", "正在校验文件。");
    try {
      const old = remembered();
      if (old) {
        try { task = await api("/" + old.id); }
        catch (error) { if (![404, 410].includes(error.status)) throw error; task = null; forget(); }
      } else if (task?.state === "completed" || task?.state === "cancelled") { task = null; transferBytes = 0; }
      if (isPublishing()) { await poll(); return; }
      if (task?.state === "completed") { await poll(); return; }
      if (task?.state === "failed") throw new Error("发布失败，请取消上传后重试。");
      const file = selectedFile || input.files[0];
      if (!file) { state("idle", "请选择要上传的 .mrpack 文件。"); return; }
      if (!file.name.toLowerCase().endsWith(".mrpack") || !file.size) throw new Error("请选择非空的 .mrpack 整合包文件。");
      if (file.size > Number(form.dataset.maxBytes || Infinity)) throw new Error("文件超过允许上传的大小，请检查文件选择。");
      selectedFile = file; dismissed = false;
      await fileWrite;
      await cacheFile(file); checkStopped();
      changed(true);
      const chunkSize = task?.chunkSize || Number(form.dataset.chunkSize);
      const hashes = cachedHashes?.file === file && cachedHashes.value.chunkSize === chunkSize
        ? cachedHashes.value : await hashFile(file, chunkSize); checkStopped();
      cachedHashes = { file, value: hashes };
      if (task?.state === "receiving" && (task.sha256 !== hashes.sha256 || task.size !== file.size)) throw new Error("所选文件与未完成的上传不同，请选择原文件或取消上传。");
      task = await api("", "POST", {instanceId:instance, fileName:file.name, size:file.size, sha256:hashes.sha256, baseRevision:task?.baseRevision ?? Number(form.dataset.revision)});
      remember(); taskDetails();
      checkStopped();
      if (task.state !== "receiving") { await poll(); return; }
      if (task.chunkSize !== hashes.chunkSize) throw new Error("上传配置已更新，请刷新后重试。");
      await renew(); checkStopped();
      const received = new Set(task.received), inFlight = new Map();
      const missing = Array.from({length:task.chunkCount}, (_, index) => index).filter(index => !received.has(index));
      let acknowledged = task.received.reduce((sum, index) => sum + Math.min(task.chunkSize, file.size - index * task.chunkSize), 0);
      let sent = 0; const started = performance.now();
      const update = () => {
        progress(acknowledged + Array.from(inFlight.values()).reduce((a, b) => a + b, 0), file.size);
        el("upload-chunks").textContent = "已确认 " + received.size + " / " + task.chunkCount;
        el("upload-speed").textContent = (sent / 1048576 / Math.max(.01, (performance.now() - started) / 1000)).toFixed(2) + " MiB/s";
      };
      state("uploading", "正在上传。"); update();
      async function lane() {
        while (missing.length) {
          checkStopped(); const index = missing.shift(); const blob = file.slice(index * task.chunkSize, Math.min(file.size, (index + 1) * task.chunkSize));
          for (let retry = 0; ; retry++) {
            checkStopped(); if (!grant || Date.parse(grant.expiresUtc) - Date.now() < 60000) await renew(); checkStopped();
            let previous = 0;
            try {
              await sendChunk(index, blob, hashes.chunks[index], loaded => { sent += Math.max(0, loaded - previous); previous = loaded; inFlight.set(index, loaded); update(); });
              inFlight.delete(index); acknowledged += blob.size; received.add(index); task.received = [...received]; update(); break;
            } catch (error) {
              inFlight.delete(index); update(); checkStopped();
              if (retry >= 5 || ![0, 401, 408, 429, 500, 502, 503, 504].includes(error.status)) throw error;
              el("upload-detail").textContent = "上传失败，正在重试（" + (retry + 1) + "/5）…";
              if (error.status === 401) await renew();
              await sleep(Math.min(15000, 1000 * 2 ** retry) + Math.random() * 300);
            }
          }
        }
      }
      const lanes = [lane(), lane()];
      try { await Promise.all(lanes); }
      catch (error) { stopped = true; for (const xhr of requests) xhr.abort(); await Promise.allSettled(lanes); throw error; }
      checkStopped();
      state("queued", "上传完成，正在提交…");
      task = await api("/" + task.id + "/complete", "POST"); await poll();
    } catch (error) {
      if (error.name === "AbortError") {
        if (task) { taskDetails(); }
        if (!cancelling) state("paused", "已暂停。"); el("upload-speed").textContent = "";
      } else showError(error);
    } finally { running = false; controls(); }
  }
  async function startRun(event) {
    if (running || cancelling) return;
    activeRun = run(event);
    try { await activeRun; } finally { activeRun = null; }
  }
  form.addEventListener("submit", async event => {
    event.preventDefault();
    await ready;
    if (running || cancelling) return;
    if (typeof navigator !== "undefined" && navigator.locks) {
      await navigator.locks.request("cmumcl-upload:" + instance, { ifAvailable: true }, async lock => {
        if (lock) await startRun(event);
        else { el("upload-error").hidden = false; el("upload-error").textContent = "文件正在其他标签页上传。"; }
      });
    } else await startRun(event);
  });
  pause.addEventListener("click", () => {
    if (!running || !["hashing", "uploading"].includes(phase)) return;
    stopped = true; for (const xhr of requests) xhr.abort(); if (cancelHash) cancelHash();
    changed(true);
  });
  cancel.addEventListener("click", () => { if (canCancel()) el("upload-cancel-confirm").hidden = false; changed(); });
  el("upload-cancel-no").addEventListener("click", () => { el("upload-cancel-confirm").hidden = true; changed(); });
  el("upload-cancel-yes").addEventListener("click", async () => {
    if (!canCancel()) return;
    cancelling = true; stopped = true; controls(); el("upload-cancel-confirm").hidden = true;
    for (const xhr of requests) xhr.abort(); if (cancelHash) cancelHash();
    el("upload-state").textContent = el("upload-stage").textContent = "正在取消…";
    changed(true);
    try {
      // Finish the active pipeline first, including an in-flight session creation.
      await activeRun;
      if (task) {
        for (let attempt = 0; ; attempt++) {
          try { await api("/" + task.id, "DELETE"); break; }
          catch (error) {
            if (attempt >= 4 || error.status !== 409 || !/Wait for active requests/i.test(error.message)) throw error;
            await sleep(200 * (attempt + 1));
          }
        }
      }
      forget(true); task = null; grant = null; transferBytes = 0; clearError();
      state("cancelled", "已取消。"); progress(0, 0); el("upload-chunks").textContent = ""; el("upload-detail").textContent = "";
    }
    catch (error) { showError(error); }
    finally { cancelling = false; running = false; controls(); }
  });
  input.addEventListener("change", () => {
    const file = input.files[0];
    // Cancelling the picker (or an emptied native input) does not discard the retained file.
    if (!file) { showSelection(); controls(); return; }
    selectionVersion++;
    selectedFile = file; cachedHashes = null; fileCached = false;
    if (!task) transferBytes = 0;
    fileWrite = fileWrite.then(() => cacheFile(file));
    clearError(); showSelection(); controls();
  });
  const dropzone = el("upload-dropzone");
  if (dropzone) {
    let entered = 0;
    const resetDrop = () => { entered = 0; dropzone.classList.remove("drag-over"); };
    dropzone.addEventListener("keydown", event => {
      if (event.target === dropzone && ["Enter", " "].includes(event.key)) {
        event.preventDefault();
        if (!input.disabled) input.click();
      }
    });
    dropzone.addEventListener("dragenter", event => {
      event.preventDefault();
      if (!input.disabled) { entered++; dropzone.classList.add("drag-over"); }
    });
    dropzone.addEventListener("dragover", event => {
      event.preventDefault();
      if (event.dataTransfer) event.dataTransfer.dropEffect = input.disabled ? "none" : "copy";
    });
    dropzone.addEventListener("dragleave", event => {
      event.preventDefault();
      if (--entered <= 0) resetDrop();
    });
    dropzone.addEventListener("drop", event => {
      event.preventDefault(); resetDrop();
      if (input.disabled) return;
      const files = event.dataTransfer?.files;
      const file = files?.[0];
      const error = !file || files.length !== 1 ? "每次请选择一个整合包文件。"
        : !file.name.toLowerCase().endsWith(".mrpack") || !file.size ? "请选择非空的 .mrpack 整合包文件。"
        : file.size > Number(form.dataset.maxBytes || 2147483648) ? "文件超出允许上传的大小。" : "";
      if (error) { el("upload-error").textContent = error; el("upload-error").hidden = false; return; }
      input.files = files;
      input.dispatchEvent(new Event("change", { bubbles: true }));
      clearError();
    });
    // Prevent dropped files outside the target from navigating away from a live task.
    document.addEventListener("dragover", event => {
      if (event.dataTransfer?.types.includes("Files")) event.preventDefault();
    });
    document.addEventListener("drop", event => {
      if (event.dataTransfer?.types.includes("Files")) { event.preventDefault(); resetDrop(); }
    });
  }
  async function restore() {
    const old = remembered(), ui = savedUi();
    const restoreVersion = selectionVersion;
    if (runtime) {
      try {
        const cached = await runtime.getFile(instance);
        if (cached?.file && selectionVersion === restoreVersion && !selectedFile) {
          fileCached = true;
          selectedFile = new File([cached.file], cached.name, { lastModified: cached.modified });
          showSelection();
        }
      } catch { /* Server-confirmed recovery remains available without browser file storage. */ }
      if (ui && (!old || ui.id === old.id)) transferBytes = Math.max(0, Number(ui.bytes) || 0);
    }
    if (!old) {
      if (ui?.phase === "cancelled" && selectedFile) {
        el("upload-progress-panel").hidden = false; state("cancelled", "已取消。"); progress(0, 0);
      }
      controls();
      if (ui?.auto && selectedFile) form.requestSubmit();
      return;
    }
    running = true; el("upload-progress-panel").hidden = false; state("restoring", "正在恢复上传…");
    try {
      task = await api("/" + old.id); taskDetails();
      if (isPublishing() || task.state === "completed" || task.state === "cancelled") await poll();
      else {
        progress(task.received.reduce((sum, index) => sum + Math.min(task.chunkSize, task.size - index * task.chunkSize), 0), task.size);
        if (task.state === "failed") throw new Error(task.error || "发布失败，请取消上传后重试。");
        state("paused", "已暂停。");
        if (!selectedFile) { el("upload-error").hidden = false; el("upload-error").textContent = "请选择原文件继续上传。"; }
      }
    } catch (error) {
      if ([404, 410].includes(error.status)) { forget(); task = null; state("idle", "上传已过期，请重新上传。"); progress(0, 0); }
      else showError(error);
    } finally { running = false; controls(); }
    if (ui?.auto && selectedFile && task?.state === "receiving") form.requestSubmit();
  }
  runtime?.register(instance, {
    root,
    attach: () => { showSelection(); controls(); },
    persist: () => changed(true),
    snapshot: () => ({ name: root?.querySelector("h2")?.textContent || instance,
      running, recoverable: fileCached, visible: !dismissed && phase !== "idle" && phase !== "cancelled",
      label: el("upload-error").hidden ? el("upload-state").textContent : el("upload-error").textContent,
      percent: el("upload-percent").textContent, value: el("upload-progress").value,
      indeterminate: ["queued", "verifying", "restoring"].includes(phase),
      resume: !submit.disabled && phase !== "completed", pause: !pause.disabled, cancel: !cancel.disabled,
      dismiss: ["completed", "cancelled"].includes(phase), confirm: !el("upload-cancel-confirm").hidden }),
    dismiss: () => { dismissed = true; },
    action: action => {
      if (action === "resume") {
        if (selectedFile || isPublishing()) form.requestSubmit();
        else window.CmuNavigate("/admin/instances/" + encodeURIComponent(instance) + "/upload");
      } else ({ pause, cancel, confirm: el("upload-cancel-yes"), keep: el("upload-cancel-no") })[action]?.click();
    }
  });
  const ready = restore();
 }
 if (typeof window !== "undefined") {
   window.CmuMountUpload = mount;
   const initialize = () => {
     const root = document.querySelector("#main .upload-layout");
     if (root) mount(root);
   };
   window.addEventListener("cmu:navigate", initialize);
   initialize();
 } else mount(null);
})();
