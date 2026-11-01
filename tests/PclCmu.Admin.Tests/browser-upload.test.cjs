"use strict";
// Only called by the bounded .NET fixture. Never uses a personal browser profile.
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "playwright");
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs/promises");
const admin = process.env.CSUMCL_TEST_ADMIN;
const gateway = process.env.CSUMCL_TEST_GATEWAY;
for (const address of [admin, gateway]) assert.equal(new URL(address).hostname, "127.0.0.1");
const pack = process.env.CSUMCL_TEST_PACK;
let browser;
let page;
(async () => {
  browser = await chromium.launch({ headless: true, args: ["--no-proxy-server", "--disable-background-networking"] });
  // The HTTPS gateway uses a one-hour, disposable test certificate. No machine
  // trust settings are changed; this option exists only in this disposable context.
  const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1100, height: 850 } });
  context.setDefaultTimeout(15000);
  // Do not intercept requests: interception can change Chromium's CORS
  // preflight behavior and would weaken this real-browser acceptance check.
  const origins = new Set();
  context.on("request", request => origins.add(new URL(request.url()).origin));
  page = await context.newPage();
  page.on("console", message => { if (message.type() === "error") console.error("BROWSER console:", message.text()); });
  page.on("requestfailed", request => console.error("BROWSER request failed:", request.method(), new URL(request.url()).pathname, request.failure()?.errorText));
  const pageErrors = [];
  page.on("pageerror", error => pageErrors.push(error.message));
  const puts = [];
  const successes = [];
  page.on("request", request => { if (request.method() === "PUT") puts.push(request.url()); });
  page.on("response", response => { if (response.request().method() === "PUT" && response.status() === 204) successes.push(response.url()); });
  await context.addInitScript(() => { if (!localStorage.getItem("cmumcl-admin-theme")) localStorage.setItem("cmumcl-admin-theme", "light"); });
  await page.goto(admin + "/admin/login");
  assert.equal(await page.locator("html").getAttribute("lang"), "zh-CN");
  await screenshot("login-light");
  await page.locator('input[name="password"]').fill("preview-theme-check");
  await page.locator("#theme-toggle").click();
  assert.equal(await page.locator('input[name="password"]').inputValue(), "preview-theme-check");
  await screenshot("login-dark");
  await page.locator("#theme-toggle").click();
  await page.locator('input[name="password"]').fill(process.env.CSUMCL_TEST_PASSWORD);
  await Promise.all([page.waitForURL(admin + "/admin"), page.locator('button[type="submit"]').click()]);
  console.log("BROWSER ok: disposable administrator login");
  await screenshot("dashboard-light");
  await page.locator("#theme-toggle").click(); await screenshot("dashboard-dark");
  await page.locator("#theme-toggle").click();
  await page.goto(admin + "/admin/instances/new"); await screenshot("instance-new");
  assert.equal(await page.locator("h1").innerText(), "新建整合包");
  async function openUpload() { await page.goto(admin + "/admin/instances/test/upload"); }
  async function selectAndUpload() {
    await page.locator("#upload-file").setInputFiles(pack);
    await page.locator("#upload-submit").click();
  }
  async function waitPublished() {
    await page.waitForFunction(() => !document.getElementById("upload-result").hidden || !document.getElementById("upload-error").hidden, null, { timeout: 60000 });
    assert.equal(await page.locator("#upload-error").isVisible(), false, await page.locator("#upload-error").innerText());
  }
  async function screenshot(name) {
    if (!process.env.CSUMCL_BROWSER_ARTIFACTS) return;
    await fs.mkdir(process.env.CSUMCL_BROWSER_ARTIFACTS, { recursive: true });
    await page.screenshot({ path: path.join(process.env.CSUMCL_BROWSER_ARTIFACTS, name + ".png"), fullPage: true });
  }
  await openUpload();
  await selectAndUpload();
  await waitPublished();
  assert.match(await page.locator("#upload-stage").innerText(), /版本 2/);
  assert.ok(new Set(puts).size < puts.length, "lost success retries only a previously sent chunk");
  assert.equal(await page.locator("#upload-error").isVisible(), false);
  console.log("BROWSER ok: Worker hash + real XHR/CORS upload + automatic lost-response retry");
  await screenshot("upload-completed");

  puts.length = 0; successes.length = 0;
  // Upload the next revision without navigating away: the page must refresh its base revision.
  assert.equal(await page.locator("#upload-form").getAttribute("data-revision"), "2");
  await selectAndUpload();
  const timeout = Date.now() + 30000;
  while (successes.length < 4 && Date.now() < timeout) await page.waitForTimeout(25);
  assert.ok(successes.length >= 4, "upload has durable progress before pause");
  await page.locator("#upload-pause").click();
  await page.waitForFunction(() => !document.getElementById("upload-submit").disabled);
  const taskId = await page.evaluate(() => JSON.parse(localStorage.getItem("pcl-upload:test")).id);
  const statusResponse = await context.request.get(admin + "/admin/api/uploads/" + taskId);
  const status = await statusResponse.json();
  assert.ok(status.received.length >= 4 && status.received.length < status.chunkCount);
  await screenshot("upload-paused");
  await page.locator("#theme-toggle").click(); await screenshot("upload-paused-dark");
  await page.setViewportSize({width:390,height:844}); await screenshot("upload-mobile");
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), "mobile upload has no horizontal overflow");
  await page.setViewportSize({width:1100,height:850});
  await page.locator("#theme-toggle").click();
  await page.reload();
  await page.waitForFunction(() => document.getElementById("upload-stage").textContent.includes("有未完成的上传"));
  puts.length = 0;
  await selectAndUpload();
  await waitPublished();
  assert.match(await page.locator("#upload-stage").innerText(), /版本 3/);
  const acknowledged = new Set(status.received);
  assert.ok(puts.every(url => !acknowledged.has(Number(url.split("/").pop()))), "refresh skips all previously acknowledged chunks");
  assert.equal(await page.evaluate(() => localStorage.getItem("pcl-upload:test")), null);
  assert.deepEqual(pageErrors, []);
  assert.ok([...origins].every(origin => [admin, gateway].includes(origin)), "all browser requests stayed on owned loopback listeners");
  await screenshot("upload-resumed-completed");
  console.log("BROWSER ok: pause + reload + reselect + skip confirmed chunks + revision 3 + no page errors");
  await page.locator("#upload-file").setInputFiles({name:"invalid.mrpack",mimeType:"application/zip",buffer:Buffer.from("invalid preview archive")});
  await page.locator("#upload-submit").click();
  await page.waitForFunction(() => !document.getElementById("upload-error").hidden);
  assert.ok(await page.locator("#upload-submit").isDisabled());
  assert.match(await page.locator("#upload-stage").innerText(), /此版本未发布/);
  assert.ok(await page.locator("#upload-error").innerText(), "failed archive gives a user-visible error");
  await screenshot("upload-failed");
  await page.locator("#upload-cancel").click();
  await page.locator("#upload-cancel-yes").click();
  await page.waitForFunction(() => document.getElementById("upload-form").dataset.state === "cancelled");
  assert.equal(await page.evaluate(() => localStorage.getItem("pcl-upload:test")), null);
  assert.ok(await page.locator("#upload-submit").isEnabled());
  assert.deepEqual(pageErrors, []);
  console.log("BROWSER ok: malformed archive failure is visible and can be cancelled without changing the published version");
})().catch(async error => {
  console.error(error);
  if (page && !page.isClosed()) {
    console.error("BROWSER diagnostics:", await page.locator("#upload-stage, #upload-error").allTextContents().catch(() => []));
    if (process.env.CSUMCL_BROWSER_ARTIFACTS) {
      await fs.mkdir(process.env.CSUMCL_BROWSER_ARTIFACTS, { recursive: true });
      await page.screenshot({ path: path.join(process.env.CSUMCL_BROWSER_ARTIFACTS, "upload-failure.png"), fullPage: true }).catch(() => {});
    }
  }
  process.exitCode = 1;
}).finally(async () => { if (browser) await browser.close(); });
