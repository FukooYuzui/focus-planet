// requestDomains uses exact domain boundaries and includes subdomains.
// Unlike a substring URL filter, example.com cannot match example.com.evil.test.
export function normalizeDomain(value) {
  if (typeof value !== 'string') throw new Error('网站域名必须是文本。');
  let domain = value.trim().toLowerCase().replace(/\.$/, '');
  if (!domain || domain.length > 253 || /[\s/:?#@*\\]/.test(domain)) {
    throw new Error('网站列表包含无效域名。');
  }
  domain = new URL(`https://${domain}`).hostname;
  if (!domain.includes('.') || /^\d+(\.\d+){3}$/.test(domain) || domain.endsWith('.localhost') ||
      !domain.split('.').every(label => /^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(label))) {
    throw new Error('网站列表包含无效域名。');
  }
  return domain;
}

export function normalizeDomains(values) {
  if (!Array.isArray(values) || values.length > 500) throw new Error('网站列表格式无效或超过 500 项。');
  return [...new Set(values.map(normalizeDomain))].sort();
}

export function createRules(domains) {
  const normalized = normalizeDomains(domains);
  return normalized.length ? [{
    id: 1,
    priority: 1,
    action: { type: 'block' },
    condition: { requestDomains: normalized, resourceTypes: ['main_frame'] }
  }] : [];
}

export function parseState(payload, now) {
  if (!payload || typeof payload.active !== 'boolean') throw new Error('桌面状态格式无效。');
  if (!payload.active) return { active: false, domains: [], deadline: null };
  const domains = normalizeDomains(payload.domains);
  const lease = Date.parse(payload.leaseExpiresAt);
  const end = Date.parse(payload.sessionEndsAt);
  if (!Number.isFinite(lease) || !Number.isFinite(end)) throw new Error('桌面状态缺少有效的结束时间。');
  const deadline = Math.min(lease, end, now + 45_000);
  if (deadline <= now) return { active: false, domains: [], deadline: null, expired: true };
  return { active: true, domains, deadline };
}
