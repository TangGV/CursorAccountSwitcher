// ---------------------------------------------------------------- 与 C# 通信
// 通过 WebView2 的 postMessage 走异步请求/响应；C# 侧用 ExecuteScriptAsync 回调。

const pending = new Map();
let seq = 0;

function host(method, arg) {
  return new Promise((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve, reject });
    // app.js 里的 nativeCall 由 C# 注入（AddHostObjectToScript 或 ExecuteScript 回填）
    if (typeof window.__native !== 'function') {
      pending.delete(id);
      reject(new Error('native 桥未就绪'));
      return;
    }
    try {
      window.__native(JSON.stringify({ id, method, arg }));
    } catch (e) {
      pending.delete(id);
      reject(e);
    }
  });
}

// 定时状态推送：C# 每 5 秒推一次，只更新卡片上的状态文字，不重建 DOM。
window.__updateList = function (json) {
  let incoming;
  try { incoming = JSON.parse(json); } catch { return; }
  if (!Array.isArray(incoming) || incoming.length === 0) return;

  // 账号集合变了（增/删）就整体重绘，否则只改状态。
  const sameShape =
    incoming.length === accounts.length &&
    incoming.every((a, i) => accounts[i] && accounts[i].id === a.id);

  accounts = incoming;

  if (!sameShape || document.querySelectorAll('#list .card').length !== accounts.length) {
    render();
    return;
  }

  for (const a of accounts) {
    const card = document.querySelector(`.card[data-id="${CSS.escape(a.id)}"]`);
    if (!card) { render(); return; }

    card.classList.toggle('running', !!a.isRunning);
    card.classList.toggle('unlogged', !!a.isRunning && !a.hasLogin);

    const statusEl = card.querySelector('.status span:last-child');
    if (statusEl && statusEl.textContent !== a.statusText) statusEl.textContent = a.statusText;

    const emailEl = card.querySelector('.email');
    if (emailEl && emailEl.textContent !== a.displayEmail) {
      emailEl.textContent = a.displayEmail;
      emailEl.title = a.displayEmail;
    }

    const launchBtn = card.querySelector('[data-act="launch"]');
    if (launchBtn) {
      const want = a.isRunning ? '切换过去' : '启动';
      if (launchBtn.textContent.trim() !== want) launchBtn.textContent = want;
    }
  }

  renderStats();
};

// C# 调用这个函数把结果回填
window.__resolve = function (payloadJson) {
  let payload;
  try { payload = JSON.parse(payloadJson); } catch { return; }
  const item = pending.get(payload.id);
  if (!item) return;
  pending.delete(payload.id);
  if (payload.ok) item.resolve(payload.data);
  else item.reject(new Error(payload.error || '未知错误'));
};

// ---------------------------------------------------------------- 状态
let accounts = [];
let prefs = {};
let settings = { activeApp: 'cursor', useClassicWindow: false, syncSettings: true, syncSession: true };
let lastMenuAnchor = null;
let menuAccountId = null;

// ---------------------------------------------------------------- 工具
const $ = (id) => document.getElementById(id);

const AVATAR_COLORS = [
  ['#5A8DFF', '#8DB2FF'],
  ['#8B6DF2', '#B09BFF'],
  ['#2FB38A', '#5FD6AE'],
  ['#E08B3C', '#F0AB6A'],
  ['#D45C7A', '#EE8CA4'],
];

function colorFor(name) {
  let h = 0;
  for (const ch of String(name || '')) h = (h * 31 + ch.charCodeAt(0)) & 0x7fffffff;
  return AVATAR_COLORS[h % AVATAR_COLORS.length];
}

function escapeHtml(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[c]));
}

function initial(name) {
  const t = String(name || '').trim();
  return t ? t[0].toUpperCase() : '?';
}

let toastTimer = null;
function toast(msg, isErr = false) {
  const el = $('toast');
  el.textContent = msg;
  el.classList.toggle('err', isErr);
  el.hidden = false;
  requestAnimationFrame(() => el.classList.add('show'));
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => {
    el.classList.remove('show');
    setTimeout(() => { el.hidden = true; }, 220);
  }, 2600);
}

// ---------------------------------------------------------------- 渲染
function renderSubtitle() {
  const el = $('subtitle');
  const cursorVer = settings.cursorVersion || settings.installVersion;
  const parts = [
    cursorVer ? `Cursor v${cursorVer}` : '未找到 Cursor',
    settings.antigravityPath ? `Antigravity v${settings.antigravityVersion}` : '未找到 Antigravity',
    settings.codexPath ? `Codex v${settings.codexVersion}` : '未找到 Codex',
  ];
  if (!settings.installPath || !settings.antigravityPath || !settings.codexPath) el.classList.add('warn');
  else el.classList.remove('warn');
  if (settings.otherVersions && settings.otherVersions.length) {
    parts.push(`另检测到 Cursor ${settings.otherVersions.join(' / ')}，已忽略`);
    el.classList.add('warn');
  }
  parts.push(settings.useClassicWindow ? '编辑器模式' : 'Agent 面板模式');
  el.textContent = parts.join('  ·  ');
}

function renderStats() {
  const running = accounts.filter((a) => a.isRunning).length;
  const logged = accounts.filter((a) => a.hasLogin).length;
  const cursorN = accounts.filter((a) => (a.app || 'cursor') === 'cursor').length;
  const agN = accounts.filter((a) => a.app === 'antigravity').length;
  const codexN = accounts.filter((a) => a.app === 'codex').length;
  const parts = [`Cursor ${cursorN}`, `Antigravity ${agN}`, `Codex ${codexN}`, `${logged} 个已登录`];
  if (running) parts.push(`${running} 个实例运行中`);
  $('stats').textContent = parts.join(' · ');
}

function cardHtml(a) {
  const [bg, fg] = colorFor(a.name);
  const cls = ['card'];
  if (a.isRunning) cls.push('running');
  if (a.isRunning && !a.hasLogin) cls.push('unlogged');

  const planTag = a.membershipText
    ? `<span class="tag plan">${escapeHtml(a.membershipText)}</span>`
    : '';
  const defTag = a.isMain ? '<span class="tag default">默认</span>' : '';

  return `
    <div class="${cls.join(' ')}" data-id="${escapeHtml(a.id)}">
      <div class="avatar" style="background:${bg}2e;color:${fg}">
        ${escapeHtml(initial(a.name))}
        <span class="ring"></span>
      </div>
      <div class="info">
        <div class="title-row">
          <span class="name" title="${escapeHtml(a.name)}">${escapeHtml(a.name)}</span>
          ${defTag}${planTag}
        </div>
        <div class="email" title="${escapeHtml(a.displayEmail)}">${escapeHtml(a.displayEmail)}</div>
        <div class="status"><span class="dot"></span><span>${escapeHtml(a.statusText)}</span></div>
      </div>
      <div class="actions">
        <button class="btn mini primary btn-launch" data-act="launch" data-id="${escapeHtml(a.id)}">
          ${a.isRunning ? '切换过去' : '启动'}
        </button>
        <button class="btn mini ghost icon" data-act="more" data-id="${escapeHtml(a.id)}" title="更多操作">⋯</button>
      </div>
    </div>`;
}

function groupHtml(app, title, hint) {
  const items = accounts.filter((a) => (a.app || 'cursor') === app);
  const note = items.length <= 1 ? `<div class="group-hint">${escapeHtml(hint)}</div>` : '';
  return `
    <section class="group">
      <div class="group-head">
        <h2>${escapeHtml(title)}</h2>
        <button class="btn mini ghost" data-act="addApp" data-app="${app}">添加</button>
      </div>
      ${items.map(cardHtml).join('')}
      ${note}
    </section>`;
}

function render() {
  const list = $('list');
  const scrollTop = list.scrollTop;
  list.innerHTML = [
    groupHtml('cursor', 'Cursor', '再添加一个账号，就会单独开一个 Cursor 进程。'),
    groupHtml('antigravity', 'Antigravity', '主账号是现在正在用的登录。再添加一个账号，会另开进程，不会顶掉主账号。'),
    groupHtml('codex', 'Codex', '主账号是现在正在用的登录。再添加一个账号会另开窗口，用独立的登录目录，不会顶掉主账号。'),
  ].join('');
  list.scrollTop = scrollTop;
  renderSubtitle();
  renderStats();
}

function renderSkeleton() {
  $('list').innerHTML = '<div class="skeleton"></div><div class="skeleton"></div>';
}

// ---------------------------------------------------------------- 数据
async function refresh(deep = false) {
  accounts = await host('listAccounts', { deep });
  render();
}

async function loadSettings() {
  settings = await host('getSettings');
  $('prefSyncSettings').checked = !!settings.syncSettings;
  $('prefSyncSession').checked = !!settings.syncSession;
  $('prefEditorMode').checked = !!settings.useClassicWindow;
  renderSubtitle();
}

// ---------------------------------------------------------------- 菜单
function openMenu(accountId, anchorEl) {
  const menu = $('menu');
  const a = accounts.find((x) => x.id === accountId);
  if (!a) return;

  menuAccountId = accountId;
  lastMenuAnchor = anchorEl;

  // 只显示适用的项
  const states = { running: a.isRunning, idle: !a.isRunning, removable: !a.isMain };
  menu.querySelectorAll('[data-when]').forEach((el) => {
    el.hidden = !states[el.dataset.when];
  });

  menu.hidden = false;
  const r = anchorEl.getBoundingClientRect();
  const mw = menu.offsetWidth || 200;
  const mh = menu.offsetHeight || 260;
  let x = r.right - mw;
  let y = r.bottom + 6;
  if (y + mh > window.innerHeight - 8) y = Math.max(8, r.top - mh - 6);
  if (x < 8) x = 8;
  menu.style.left = `${x}px`;
  menu.style.top = `${y}px`;
}

function closeMenu() {
  $('menu').hidden = true;
  menuAccountId = null;
  lastMenuAnchor = null;
}

// ---------------------------------------------------------------- 弹窗
let modalResolve = null;

function showModal({ title, desc, value, placeholder, okText = '确定', choices }) {
  return new Promise((resolve) => {
    modalResolve = resolve;
    $('modalTitle').textContent = title;

    const descEl = $('modalDesc');
    descEl.hidden = !desc;
    descEl.textContent = desc || '';

    const input = $('modalInput');
    const bodyEl = $('modalBody');
    $('modalOk').textContent = okText;

    if (choices) {
      input.hidden = true;
      bodyEl.hidden = false;
      bodyEl.innerHTML = `<div class="choice">${choices
        .map((c, i) => `<button data-i="${i}" class="${i === 0 ? 'sel' : ''}">${escapeHtml(c.label)}</button>`)
        .join('')}</div>`;
      bodyEl.querySelectorAll('button').forEach((b) => {
        b.addEventListener('click', () => {
          bodyEl.querySelectorAll('button').forEach((x) => x.classList.remove('sel'));
          b.classList.add('sel');
        });
      });
    } else {
      input.hidden = false;
      bodyEl.hidden = true;
      bodyEl.innerHTML = '';
      input.value = value || '';
      input.placeholder = placeholder || '';
    }

    $('modal').hidden = false;

    if (!choices) {
      setTimeout(() => { input.focus(); input.select(); }, 30);
    }
  });
}

function closeModal(result) {
  $('modal').hidden = true;
  const r = modalResolve;
  modalResolve = null;
  if (r) r(result);
}

function modalChoice() {
  const sel = $('modalBody').querySelector('button.sel');
  return sel ? Number(sel.dataset.i) : 0;
}

// ---------------------------------------------------------------- 事件
document.addEventListener('click', async (e) => {
  const t = e.target.closest('[data-act]');
  if (!t) {
    if (!$('menu').contains(e.target)) closeMenu();
    return;
  }

  const act = t.dataset.act;
  const id = t.dataset.id || menuAccountId;
  const account = accounts.find((a) => a.id === id);

  switch (act) {
    case 'addApp':
      closeMenu();
      await doAdd(t.dataset.app);
      break;

    case 'launch':
      closeMenu();
      await doLaunch(account);
      break;

    case 'more':
      if ($('menu').hidden || menuAccountId !== id) {
        openMenu(id, t);
      } else {
        closeMenu();
      }
      break;

    case 'activate':
      closeMenu();
      await run('activate', () => host('activate', { id }), '已切换到该窗口');
      break;

    case 'close':
      closeMenu();
      await doClose(account);
      break;

    case 'startLogin':
      closeMenu();
      await doLaunch(account);
      break;

    case 'openProject':
      closeMenu();
      await run('openProject', () => host('openProject', { id }), '已发出打开请求');
      break;

    case 'rename':
      closeMenu();
      await doRename(account);
      break;

    case 'openDir':
      closeMenu();
      await run('openDir', () => host('openDir', { id }), '已打开数据目录');
      break;

    case 'copyDir':
      closeMenu();
      await run('copyDir', () => host('copyDir', { id }), '路径已复制到剪贴板');
      break;

    case 'remove':
      closeMenu();
      await doRemove(account);
      break;
  }
});

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    if (!$('modal').hidden) { closeModal(null); return; }
    if (!$('menu').hidden) { closeMenu(); return; }
  }
  if (e.key === 'Enter' && !$('modal').hidden) {
    const input = $('modalInput');
    if (!input.hidden) { $('modalOk').click(); }
  }
});

document.addEventListener('contextmenu', (e) => e.preventDefault());

window.addEventListener('resize', closeMenu);
window.addEventListener('blur', closeMenu);

$('btnRefresh').addEventListener('click', async () => {
  $('btnRefresh').disabled = true;
  try {
    await run('refresh', async () => {
      await loadSettings();
      await refresh(true);
    }, '状态已刷新');
  } finally {
    $('btnRefresh').disabled = false;
  }
});

$('btnAdd').addEventListener('click', () => doAdd());
$('btnPrefs').addEventListener('click', () => run('prefs', () => host('openPrefs'), '已打开设置目录'));

$('prefSyncSettings').addEventListener('change', async (e) => {
  settings.syncSettings = e.target.checked;
  await host('setSettings', { syncSettings: settings.syncSettings, syncSession: settings.syncSession });
});

$('prefSyncSession').addEventListener('change', async (e) => {
  settings.syncSession = e.target.checked;
  await host('setSettings', { syncSettings: settings.syncSettings, syncSession: settings.syncSession });
});

$('prefEditorMode').addEventListener('change', async (e) => {
  settings.useClassicWindow = e.target.checked;
  await host('setSettings', {
    syncSettings: settings.syncSettings,
    syncSession: settings.syncSession,
    useClassicWindow: settings.useClassicWindow,
  });
  renderSubtitle();
  toast(e.target.checked ? '下次启动用编辑器窗口' : '下次启动用 Agent 面板');
});

$('modalCancel').addEventListener('click', () => closeModal(null));
$('modalOk').addEventListener('click', () => {
  const input = $('modalInput');
  if (input.hidden) { closeModal({ choice: modalChoice() }); return; }
  const v = input.value.trim();
  if (!v) { input.focus(); return; }
  closeModal({ text: v });
});
$('modal').addEventListener('click', (e) => {
  if (e.target === $('modal')) closeModal(null);
});

// ---------------------------------------------------------------- 业务动作
async function run(label, fn, okMsg) {
  try {
    await fn();
    if (okMsg) toast(okMsg);
  } catch (err) {
    const msg = String(err.message || err);
    if (msg) toast(msg, true);
  }
}

async function doLaunch(account) {
  if (!account) return;
  try {
    const res = await host('launch', { id: account.id });
    if (res && res.message) toast(res.message);
    else if (!account.isRunning) toast(`正在启动「${account.name}」…`);
    // 稍后刷新，等进程真正起来
    setTimeout(() => refresh(false).catch(() => {}), 2600);
    setTimeout(() => refresh(true).catch(() => {}), 6000);
  } catch (err) {
    toast(String(err.message || err), true);
  }
}

async function doClose(account) {
  if (!account) return;
  const r = await showModal({
    title: `关闭「${account.name}」的实例？`,
    desc: '未保存的编辑器内容会提示你保存，不会强制丢弃。',
    choices: [{ label: '关闭实例' }, { label: '取消' }],
    okText: '继续',
  });
  if (!r || r.choice !== 0) return;
  try {
    await host('close', { id: account.id });
    toast(`正在关闭「${account.name}」…`);
    setTimeout(() => refresh(false).catch(() => {}), 1800);
  } catch (err) {
    toast(String(err.message || err), true);
  }
}

async function doAdd(presetApp) {
  let app = ['antigravity', 'cursor', 'codex'].includes(presetApp) ? presetApp : '';
  if (!app) {
    const pick = await showModal({
      title: '添加到哪个应用？',
      choices: [{ label: 'Cursor' }, { label: 'Antigravity' }, { label: 'Codex' }, { label: '取消' }],
      okText: '继续',
    });
    if (!pick || pick.choice === 3) return;
    app = pick.choice === 1 ? 'antigravity' : pick.choice === 2 ? 'codex' : 'cursor';
  }
  const r = await showModal({
    title: '给这个账号起个名字',
    value: suggestName(),
    placeholder: '例如：公司号 / 个人号',
    okText: '创建',
  });
  if (!r) return;
  try {
    const created = await host('add', { name: r.text, app });
    await refresh(false);
    const hints = {
      antigravity: '现在启动它并登录另一个 Google 账号吗？\n这是独立进程，登录不会顶掉主账号。',
      codex: '现在启动它并登录另一个账号吗？\n会用独立的登录目录，不会顶掉主账号。',
      cursor: '现在启动它并登录第二个账号吗？\n启动后在 Cursor 里点右上角头像 → Sign In 完成登录。',
    };
    const ask = await showModal({
      title: `已创建「${created.name}」`,
      desc: hints[app],
      choices: [{ label: '立即启动' }, { label: '稍后' }],
      okText: '继续',
    });
    if (ask && ask.choice === 0) {
      const full = accounts.find((a) => a.id === created.id) || created;
      await doLaunch(full);
    }
  } catch (err) {
    toast(String(err.message || err), true);
  }
}

async function doRename(account) {
  if (!account) return;
  const r = await showModal({
    title: '重命名账号',
    value: account.name,
    okText: '保存',
  });
  if (!r) return;
  try {
    await host('rename', { id: account.id, name: r.text });
    await refresh(false);
    toast('已重命名');
  } catch (err) {
    toast(String(err.message || err), true);
  }
}

async function doRemove(account) {
  if (!account) return;

  const hasData = account.dataDirExists;
  const choices = hasData
    ? [{ label: '仅从列表移除' }, { label: '同时删除数据目录' }, { label: '取消' }]
    : [{ label: '移除' }, { label: '取消' }];

  const r = await showModal({
    title: `移除「${account.name}」？`,
    desc: hasData
      ? '删除数据目录会连带删掉这个账号的聊天记录和登录状态，无法恢复。'
      : '仅从这个列表里移除。',
    choices,
    okText: '继续',
  });
  if (!r) return;

  const cancelIdx = choices.length - 1;
  if (r.choice === cancelIdx) return;

  const deleteData = hasData && r.choice === 1;
  try {
    await host('remove', { id: account.id, deleteData });
    await refresh(false);
    toast(deleteData ? '已移除账号并删除数据' : '已移除账号');
  } catch (err) {
    toast(String(err.message || err), true);
  }
}

function suggestName() {
  const used = new Set(accounts.map((a) => a.name));
  for (let i = 2; i < 100; i++) {
    const c = `账号 ${i}`;
    if (!used.has(c)) return c;
  }
  return '新账号';
}

// ---------------------------------------------------------------- 启动
renderSkeleton();
(async () => {
  try {
    await loadSettings();
    await refresh(true);
  } catch (err) {
    toast('初始化失败：' + (err.message || err), true);
  }
})();
