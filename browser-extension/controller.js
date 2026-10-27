import { createRules, parseState } from './rules.js';

export const ENDPOINT = 'http://127.0.0.1:17839/state';
const TOKEN_KEY = 'focusPairToken';
const STATUS_KEY = 'focusStatus';

export function createController({ chrome, fetchImpl = fetch, now = Date.now, setTimer = setTimeout, clearTimer = clearTimeout }) {
  let token = null;
  let epoch = 0;
  let revision = 0;
  let queue = Promise.resolve();
  let inflight = null;
  let pollTimer;
  let leaseTimer;
  let disposed = false;
  let status = { paired: false, state: 'unpaired', message: '尚未配对，网站限制未启用。', domains: [], count: 0 };

  function serialize(operation) {
    const result = queue.then(operation);
    queue = result.catch(() => {});
    return result;
  }

  function stopTimers() {
    clearTimer(pollTimer);
    clearTimer(leaseTimer);
    pollTimer = leaseTimer = undefined;
  }

  async function replaceRules(domains) {
    const existing = await chrome.declarativeNetRequest.getSessionRules();
    await chrome.declarativeNetRequest.updateSessionRules({
      removeRuleIds: existing.map(rule => rule.id), addRules: createRules(domains)
    });
  }

  async function setStatus(next) {
    status = { ...next, paired: Boolean(token), syncedAt: new Date(now()).toISOString() };
    await chrome.storage.local.set({ [STATUS_KEY]: status });
    try {
      await chrome.action.setBadgeBackgroundColor({ color: '#287764' });
      await chrome.action.setBadgeText({ text: status.count ? String(status.count) : '' });
    } catch { /* The badge is informational; rule updates remain authoritative. */ }
  }

  async function applyState(expectedEpoch, next, domains = [], deadline = null) {
    return serialize(async () => {
      if (expectedEpoch !== epoch || disposed) return status;
      await replaceRules(domains);
      clearTimer(leaseTimer);
      const thisRevision = ++revision;
      await setStatus({ ...next, domains, count: domains.length, leaseExpiresAt: deadline ? new Date(deadline).toISOString() : null });
      if (deadline) {
        leaseTimer = setTimer(() => {
          if (expectedEpoch === epoch && thisRevision === revision) {
            applyState(expectedEpoch, { state: 'expired', message: '桌面确认已过期，网站限制已解除。', desktopActive: false }).catch(() => {});
          }
        }, Math.max(0, deadline - now()));
      }
      return status;
    });
  }

  function schedulePoll(expectedEpoch) {
    clearTimer(pollTimer);
    if (token && expectedEpoch === epoch && !disposed) {
      pollTimer = setTimer(() => sync().catch(() => {}), 5_000);
    }
  }

  function sync() {
    if (disposed) return Promise.resolve(status);
    if (inflight?.epoch === epoch) return inflight.promise;
    const expectedEpoch = epoch;
    const currentToken = token;
    if (!currentToken) {
      return applyState(expectedEpoch, { state: 'unpaired', message: '尚未配对，网站限制未启用。', desktopActive: false });
    }
    const aborter = new AbortController();
    const timeout = setTimer(() => aborter.abort(), 3_000);
    const operation = { epoch: expectedEpoch, aborter, promise: null };
    operation.promise = (async () => {
      try {
        const response = await fetchImpl(ENDPOINT, {
          method: 'GET', headers: { 'X-Focus-Token': currentToken },
          cache: 'no-store', credentials: 'omit', redirect: 'error', signal: aborter.signal
        });
        if (!response.ok) throw new Error(response.status === 401 ? '配对码不匹配，请重新配对。' : '桌面程序暂时无法连接。');
        const state = parseState(await response.json(), now());
        return await applyState(expectedEpoch, {
          state: state.active ? 'active' : state.expired ? 'expired' : 'idle',
          message: state.active
            ? (state.domains.length ? '专注进行中，以下网站的新页面访问已限制。' : '专注进行中，桌面未设置限制网站。')
            : (state.expired ? '桌面确认已过期，网站限制已解除。' : '桌面已连接，当前没有专注限制。'),
          desktopActive: state.active
        }, state.domains, state.deadline);
      } catch (error) {
        return await applyState(expectedEpoch, {
          state: 'offline', desktopActive: false,
          message: error?.message === '配对码不匹配，请重新配对。' ? error.message + ' 网站限制已解除。' : '桌面未连接或状态无效，网站限制已解除。'
        });
      } finally {
        clearTimer(timeout);
        if (inflight === operation) inflight = null;
        schedulePoll(expectedEpoch);
      }
    })();
    inflight = operation;
    return operation.promise;
  }

  async function initialize() {
    const expectedEpoch = ++epoch;
    // Clear session rules on every worker start. Never retain an unconfirmed lease.
    await serialize(() => replaceRules([]));
    const saved = await chrome.storage.local.get(TOKEN_KEY);
    if (expectedEpoch !== epoch) return status;
    token = typeof saved[TOKEN_KEY] === 'string' ? saved[TOKEN_KEY] : null;
    return sync();
  }

  async function pair(value) {
    const cleaned = typeof value === 'string' ? value.trim() : '';
    if (!/^[\x21-\x7E]{12,256}$/.test(cleaned)) throw new Error('请粘贴桌面程序提供的完整配对码。');
    const expectedEpoch = ++epoch;
    inflight?.aborter.abort();
    stopTimers();
    token = cleaned;
    await serialize(async () => {
      if (expectedEpoch !== epoch) return;
      await replaceRules([]);
      await chrome.storage.local.set({ [TOKEN_KEY]: cleaned });
    });
    if (expectedEpoch !== epoch) return status;
    return sync();
  }

  async function unpair() {
    const expectedEpoch = ++epoch;
    inflight?.aborter.abort();
    stopTimers();
    token = null;
    await serialize(async () => {
      if (expectedEpoch !== epoch) return;
      await replaceRules([]);
      await chrome.storage.local.remove(TOKEN_KEY);
    });
    return applyState(expectedEpoch, { state: 'unpaired', message: '已解除全部网站限制并清除配对。', desktopActive: false });
  }

  return {
    initialize, sync, pair, unpair,
    getStatus: () => ({ ...status }),
    dispose: () => { disposed = true; ++epoch; inflight?.aborter.abort(); stopTimers(); }
  };
}
