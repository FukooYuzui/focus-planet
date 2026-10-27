// Optional real Edge smoke test. Uses an isolated temporary profile and a local mock desktop.
// Set PLAYWRIGHT_MODULE to a Playwright module path when it is not installed locally.
import assert from 'node:assert/strict';
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const extension = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const profile = path.join(extension, 'tests', `.qa-profile-${process.pid}`);
const stagedExtension = path.join(extension, 'tests', `.qa-extension-${process.pid}`);
const resultDir = path.join(extension, 'test-results');
const token = 'temporary-browser-smoke-pair-token';
let active = false;
let context;
const evidence = [];
const api = http.createServer((req, res) => {
  res.setHeader('Content-Type', 'application/json');
  if (req.url !== '/state' || req.headers['x-focus-token'] !== token) { res.writeHead(401); res.end('{}'); return; }
  res.end(JSON.stringify({ active, domains: ['focus-garden.test'], leaseExpiresAt: new Date(Date.now() + 20000).toISOString(), sessionEndsAt: new Date(Date.now() + 60000).toISOString() }));
});
const site = http.createServer((req, res) => { res.setHeader('Content-Type', 'text/html'); res.end('<h1>Focus Garden local website test</h1>'); });
try {
  await new Promise((resolve, reject) => { api.once('error', reject); api.listen(0, '127.0.0.1', resolve); });
  const apiPort = api.address().port;
  await fs.mkdir(stagedExtension, { recursive: true });
  for (const name of ['manifest.json', 'background.js', 'controller.js', 'rules.js', 'popup.html', 'popup.css', 'popup.js']) {
    const source = await fs.readFile(path.join(extension, name), 'utf8');
    await fs.writeFile(path.join(stagedExtension, name), source.replaceAll('127.0.0.1:17839', `127.0.0.1:${apiPort}`));
  }
  await new Promise(resolve => site.listen(0, '127.0.0.1', resolve));
  const port = site.address().port;
  context = await chromium.launchPersistentContext(profile, {
    executablePath: process.env.EDGE_EXECUTABLE || 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    headless: true,
    ignoreDefaultArgs: ['--disable-extensions'],
    args: [`--disable-extensions-except=${stagedExtension}`, `--load-extension=${stagedExtension}`, '--no-first-run', '--no-proxy-server', '--host-resolver-rules=MAP focus-garden.test 127.0.0.1, MAP sub.focus-garden.test 127.0.0.1, MAP notfocus-garden.test 127.0.0.1']
  });
  const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker', { timeout: 15000 });
  const id = new URL(worker.url()).hostname;
  const popup = await context.newPage(); await popup.goto(`chrome-extension://${id}/popup.html`);
  const message = (action, extra = {}) => popup.evaluate(message => chrome.runtime.sendMessage(message), { action, ...extra });
  const paired = await message('pair', { token }); assert.equal(paired.ok, true); assert.equal(paired.status.state, 'idle'); evidence.push('real Edge: extension loaded and paired');
  const page = await context.newPage();
  const target = `http://focus-garden.test:${port}/`;
  assert.equal((await page.goto(target)).status(), 200); evidence.push('real Edge: unblocked test site opens before focus');
  active = true; assert.equal((await message('sync')).status.state, 'active');
  for (const host of ['focus-garden.test', 'sub.focus-garden.test']) {
    const blockedPage = await context.newPage();
    await assert.rejects(blockedPage.goto(`http://${host}:${port}/blocked`), /ERR_BLOCKED_BY_CLIENT/);
    await blockedPage.close();
  }
  evidence.push('real Edge: target domain and subdomain navigation blocked');
  assert.equal((await page.goto(`http://notfocus-garden.test:${port}/`)).status(), 200); evidence.push('real Edge: suffix lookalike remains accessible');
  await popup.screenshot({ path: path.join(await fs.mkdir(resultDir, { recursive: true }).then(() => resultDir), 'extension-popup.png') });
  active = false; assert.equal((await message('sync')).status.state, 'idle');
  assert.equal((await page.goto(target)).status(), 200); evidence.push('real Edge: stopping focus clears rules and site opens');
  active = true; await message('sync');
  const cleared = await message('unpair'); assert.equal(cleared.ok, true); assert.equal(cleared.status.state, 'unpaired');
  assert.equal((await page.goto(target)).status(), 200); evidence.push('real Edge: emergency unpair clears active rules');
  await fs.writeFile(path.join(resultDir, 'browser-smoke.json'), JSON.stringify({ passed: true, browser: await context.browser()?.version(), api: 'Isolated local mock desktop on an ephemeral port; staged endpoint and manifest port only', evidence }, null, 2));
  console.log(JSON.stringify({ passed: true, evidence }, null, 2));
} finally {
  if (context) await context.close();
  await new Promise(resolve => api.close(resolve));
  await new Promise(resolve => site.close(resolve));
  if (profile.startsWith(path.join(extension, 'tests') + path.sep)) await fs.rm(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  if (stagedExtension.startsWith(path.join(extension, 'tests') + path.sep)) await fs.rm(stagedExtension, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
