const names = { unpaired: '尚未配对', active: '专注中', idle: '已连接', offline: '未连接', expired: '已解除' };
const byId = id => document.getElementById(id);
function render(status) {
  byId('pill').textContent = names[status.state] || '状态未知';
  byId('status').textContent = status.message;
  byId('domains').replaceChildren(...(status.domains || []).map(domain => {
    const li = document.createElement('li'); li.textContent = domain; return li;
  }));
  byId('updated').textContent = status.syncedAt ? `最近确认 ${new Date(status.syncedAt).toLocaleTimeString()}` : '';
  byId('token').placeholder = status.paired ? '已保存配对码，重新粘贴可更换' : '从桌面「使用限制」复制';
}
async function command(action, extra = {}) {
  byId('error').textContent = '';
  document.querySelectorAll('button').forEach(button => button.disabled = true);
  try {
    const result = await chrome.runtime.sendMessage({ action, ...extra });
    if (!result?.ok) throw new Error(result?.error || '扩展未响应，请禁用扩展解除限制。');
    render(result.status);
    return true;
  } catch (error) {
    byId('error').textContent = error.message;
    return false;
  } finally {
    document.querySelectorAll('button').forEach(button => button.disabled = false);
  }
}
byId('pair').addEventListener('click', async () => {
  if (await command('pair', { token: byId('token').value })) byId('token').value = '';
});
byId('sync').addEventListener('click', () => command('sync'));
byId('unpair').addEventListener('click', async () => {
  if (await command('unpair')) byId('token').value = '';
});
chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.focusStatus?.newValue) render(changes.focusStatus.newValue);
});
command('sync');
