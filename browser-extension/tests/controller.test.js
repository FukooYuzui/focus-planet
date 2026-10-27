import test from 'node:test';
import assert from 'node:assert/strict';
import { createController, ENDPOINT } from '../controller.js';
import { createRules, normalizeDomain, parseState } from '../rules.js';

function harness(initialToken) {
  let current = Date.parse('2026-10-27T08:00:00Z');
  let rules = [];
  let calls = [];
  let timers = new Map();
  let serial = 0;
  const data = initialToken ? { focusPairToken: initialToken } : {};
  let fetchHandler = async () => ({ ok: true, status: 200, json: async () => ({ active: false }) });
  const chrome = {
    storage: { local: {
      get: async key => ({ [key]: data[key] }),
      set: async entries => Object.assign(data, structuredClone(entries)),
      remove: async key => { delete data[key]; }
    } },
    declarativeNetRequest: {
      getSessionRules: async () => structuredClone(rules),
      updateSessionRules: async ({ removeRuleIds, addRules }) => {
        rules = rules.filter(rule => !removeRuleIds.includes(rule.id)).concat(structuredClone(addRules));
      }
    },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} }
  };
  const controller = createController({
    chrome, now: () => current,
    fetchImpl: async (url, init) => { calls.push({ url, init }); return fetchHandler(url, init); },
    setTimer: (callback, delay) => { const id = ++serial; timers.set(id, { callback, delay }); return id; },
    clearTimer: id => timers.delete(id)
  });
  return {
    controller, data, chrome,
    get rules() { return rules; }, get calls() { return calls; }, get timers() { return timers; },
    setFetch: handler => { fetchHandler = handler; },
    active: (domains = ['example.com'], extra = {}) => {
      fetchHandler = async () => ({ ok: true, status: 200, json: async () => ({
        active: true, domains,
        leaseExpiresAt: new Date(current + 20_000).toISOString(),
        sessionEndsAt: new Date(current + 25 * 60_000).toISOString(), ...extra
      }) });
    },
    seed: value => { rules = value; },
    advance: ms => { current += ms; }
  };
}

test('domain rules use exact domain boundaries and include only main-frame navigations', () => {
  const [rule] = createRules(['Example.COM.', 'example.com', 'www.example.org']);
  assert.deepEqual(rule.condition.requestDomains, ['example.com', 'www.example.org']);
  assert.deepEqual(rule.condition.resourceTypes, ['main_frame']);
  assert.equal(rule.action.type, 'block');
  assert.equal('urlFilter' in rule.condition, false);
  const matches = host => rule.condition.requestDomains.some(domain => host === domain || host.endsWith('.' + domain));
  assert.equal(matches('example.com'), true);
  assert.equal(matches('a.example.com'), true);
  assert.equal(matches('notexample.com'), false);
  assert.equal(matches('example.com.evil.test'), false);
  assert.equal(normalizeDomain('例子.中国'), 'xn--fsqu00a.xn--fiqs8s');
});

test('malformed, wildcard, URL and loopback rules are rejected', () => {
  for (const value of ['localhost', '127.0.0.1', 'localhost.', '*.example.com', 'https://example.com', 'example.com/path', 'example.com@evil.test', '-bad.com', 'example..com', '::1', 'a.localhost']) {
    assert.throws(() => normalizeDomain(value), value);
  }
  assert.throws(() => createRules(Array(501).fill('example.com')));
});

test('unpaired initialization clears leftover session rules without contacting desktop', async () => {
  const h = harness(); h.seed(createRules(['example.com']));
  const status = await h.controller.initialize();
  assert.deepEqual(h.rules, []); assert.equal(status.state, 'unpaired'); assert.equal(h.calls.length, 0);
  h.controller.dispose();
});

test('pairing retrieves state using only the loopback endpoint and token header', async () => {
  const h = harness(); await h.controller.initialize(); h.active(['example.com']);
  const status = await h.controller.pair('0123456789abcdef0123456789abcdef');
  assert.equal(status.state, 'active'); assert.equal(status.count, 1);
  assert.deepEqual(h.rules[0].condition.requestDomains, ['example.com']);
  assert.equal(h.calls[0].url, ENDPOINT); assert.equal(h.calls[0].init.headers['X-Focus-Token'], '0123456789abcdef0123456789abcdef');
  assert.equal(h.calls[0].init.credentials, 'omit'); assert.equal(h.calls[0].init.redirect, 'error');
  assert.equal('token' in h.data.focusStatus, false);
  h.controller.dispose();
});

test('idle desktop clears active session rules', async () => {
  const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
  h.setFetch(async () => ({ ok: true, json: async () => ({ active: false }) }));
  assert.equal((await h.controller.sync()).state, 'idle'); assert.deepEqual(h.rules, []);
  h.controller.dispose();
});

test('401 and network failures both fail open and retain pairing for reconnection', async () => {
  for (const failure of [async () => ({ ok: false, status: 401 }), async () => { throw new Error('network failed'); }]) {
    const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
    assert.equal(h.rules.length, 1); h.setFetch(failure);
    const status = await h.controller.sync();
    assert.equal(status.state, 'offline'); assert.equal(status.paired, true); assert.deepEqual(h.rules, []);
    h.controller.dispose();
  }
});

test('lease expiry and session end prevent stale blocking', () => {
  const now = Date.parse('2026-10-27T08:00:00Z');
  const payload = { active: true, domains: ['example.com'], leaseExpiresAt: new Date(now - 1).toISOString(), sessionEndsAt: new Date(now + 10000).toISOString() };
  assert.equal(parseState(payload, now).expired, true);
  payload.leaseExpiresAt = new Date(now + 100_000).toISOString();
  assert.equal(parseState(payload, now).deadline, now + 10000);
  payload.sessionEndsAt = new Date(now + 120_000).toISOString();
  assert.equal(parseState(payload, now).deadline, now + 45_000);
  payload.sessionEndsAt = null;
  assert.throws(() => parseState(payload, now));
});

test('expired state clears rules rather than retaining previous restrictions', async () => {
  const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
  h.active(['example.com'], { leaseExpiresAt: '2026-10-27T07:59:00Z' });
  assert.equal((await h.controller.sync()).state, 'expired'); assert.deepEqual(h.rules, []);
  h.controller.dispose();
});

test('lease timer clears restrictions when no fresh response arrives', async () => {
  const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
  const leaseTimer = [...h.timers.values()].find(timer => timer.delay === 20_000);
  assert.ok(leaseTimer); h.advance(20_000); leaseTimer.callback();
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(h.controller.getStatus().state, 'expired'); assert.deepEqual(h.rules, []);
  h.controller.dispose();
});

test('repeated synchronization shares one request', async () => {
  const h = harness('1234567890123456'); await h.controller.initialize();
  let resolve; h.setFetch(() => new Promise(r => { resolve = r; }));
  const first = h.controller.sync(); const second = h.controller.sync();
  assert.equal(first, second); assert.equal(h.calls.length, 2);
  resolve({ ok: true, json: async () => ({ active: false }) });
  await first; h.controller.dispose();
});

test('unpair prevents a late active response from reinstating rules', async () => {
  const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
  let resolve; h.setFetch(() => new Promise(r => { resolve = r; }));
  const pending = h.controller.sync();
  await h.controller.unpair();
  assert.deepEqual(h.rules, []); assert.equal(h.data.focusPairToken, undefined);
  resolve({ ok: true, json: async () => ({ active: true, domains: ['evil.test'], leaseExpiresAt: '2026-10-27T08:00:30Z', sessionEndsAt: '2026-10-27T08:25:00Z' }) });
  await pending;
  assert.deepEqual(h.rules, []); assert.equal(h.controller.getStatus().state, 'unpaired');
  assert.equal(h.timers.size, 0); h.controller.dispose();
});

test('new pairing supersedes a late response from old desktop pairing', async () => {
  const h = harness('old-token-1234567'); await h.controller.initialize();
  let resolveOld; h.setFetch((url, init) => init.headers['X-Focus-Token'].startsWith('old')
    ? new Promise(resolve => { resolveOld = resolve; })
    : Promise.resolve({ ok: true, json: async () => ({ active: false }) }));
  const pending = h.controller.sync();
  await h.controller.pair('new-token-1234567');
  resolveOld({ ok: true, json: async () => ({ active: true, domains: ['evil.test'], leaseExpiresAt: '2026-10-27T08:00:30Z', sessionEndsAt: '2026-10-27T08:25:00Z' }) });
  await pending;
  assert.deepEqual(h.rules, []); assert.equal(h.data.focusPairToken, 'new-token-1234567');
  assert.equal(h.controller.getStatus().state, 'idle'); h.controller.dispose();
});

test('invalid desktop payload fails open', async () => {
  const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
  h.active(['bad.domain/path']); const status = await h.controller.sync();
  assert.equal(status.state, 'offline'); assert.deepEqual(h.rules, []); h.controller.dispose();
});

test('extension API failure does not claim that uncleared rules were cleared', async () => {
  const h = harness('1234567890123456'); h.active(); await h.controller.initialize();
  h.chrome.declarativeNetRequest.updateSessionRules = async () => { throw new Error('DNR unavailable'); };
  await assert.rejects(h.controller.unpair(), /DNR unavailable/);
  assert.equal(h.rules.length, 1); assert.equal(h.controller.getStatus().state, 'active');
  h.controller.dispose();
});
