import { createController } from './controller.js';

const controller = createController({ chrome });
const ready = controller.initialize();
ready.catch(() => {});

async function ensureAlarm() {
  await chrome.alarms.create('focus-garden-sync', { periodInMinutes: 0.5 });
}
ensureAlarm().catch(() => {});

chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === 'focus-garden-sync') ready.then(() => controller.sync()).catch(() => {});
});
chrome.runtime.onStartup.addListener(() => ensureAlarm().catch(() => {}));
chrome.runtime.onInstalled.addListener(() => ensureAlarm().catch(() => {}));

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (sender.id !== chrome.runtime.id) return false;
  (async () => {
    // Emergency clearing must remain available even if initialization failed.
    if (message?.action === 'unpair') {
      await ready.catch(() => {});
      return controller.unpair();
    }
    await ready;
    if (message?.action === 'pair') return controller.pair(message.token);
    if (message?.action === 'sync') return controller.sync();
    if (message?.action === 'status') return controller.getStatus();
    throw new Error('未知操作。');
  })().then(status => sendResponse({ ok: true, status }), error => sendResponse({ ok: false, error: error.message || '操作失败，请禁用扩展解除限制。' }));
  return true;
});
