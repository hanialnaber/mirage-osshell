// Dock: pinned apps with the assistant in the middle, running apps, then
// Downloads and Trash. Icons magnify under the pointer.
import { ic } from './icons.js';
import { call, on, inApp, os, esc, tile, getSettings, saveSettings, onSettings } from './core.js';

const CSS = `
.dockwrap{position:absolute;left:0;right:0;bottom:12px;display:flex;justify-content:center;pointer-events:none}
.dock{pointer-events:auto;display:flex;align-items:flex-end;gap:10px;padding:11px 14px;border-radius:24px;position:relative}
.dock .tile{--k:1;width:calc(56px*var(--k));height:calc(56px*var(--k));border-radius:calc(15px*var(--k));transition:width .12s var(--ease),height .12s var(--ease)}
.dock .tile:active{filter:brightness(1.25)}
.dock .tile.bounce{animation:bounce .9s var(--ease) 2}
@keyframes bounce{0%,100%{transform:translateY(0)}35%{transform:translateY(-14px)}}
.dock .sep{width:1px;height:44px;background:var(--line2);margin:0 3px;align-self:center}
.dtip{position:absolute;bottom:calc(100% + 12px);transform:translateX(-50%);padding:5px 10px;border-radius:8px;font-size:12px;white-space:nowrap;pointer-events:none;opacity:0;transition:opacity .12s}
.dtip.show{opacity:1}
.dmenu{position:absolute;bottom:calc(100% + 10px);min-width:180px;border-radius:12px;padding:6px;font-size:13px;z-index:5}
.dmenu button{display:flex;width:100%;gap:10px;align-items:center;padding:7px 10px;border-radius:8px;text-align:left}
.dmenu button:hover{background:var(--gold-dim);color:var(--gold)}
`;

const ASSIST = { id: 'mirage:assistant', name: 'Mirage assistant', glyph: ic('spark'), tone: 'gold' };

export function mountDock(root, opts = {}) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  root.innerHTML = `<div class="dockwrap"><div class="dock glass"><div class="dtip glass deep"></div></div></div>`;
  const dock = root.querySelector('.dock');
  const tip = root.querySelector('.dtip');
  let apps = [], wins = [], pinned = [];

  async function load() {
    const s = await getSettings();
    pinned = s.pinned || [];
    apps = (await call('list_apps')) || [];
    render();
  }
  const byId = (id) => apps.find((a) => a.id === id);
  const runningFor = (a) => wins.some((w) => (a.target && w.path && w.path.toLowerCase() === a.target.toLowerCase()) || (w.app && a.name && w.app.toLowerCase() === a.name.toLowerCase()));

  function render() {
    const pins = pinned.map(byId).filter(Boolean);
    const mid = Math.ceil(pins.length / 2);
    const left = pins.slice(0, mid), right = pins.slice(mid);
    // running apps that are not pinned, one per app
    const seen = new Set();
    const extra = wins.filter((w) => {
      const k = (w.path || w.app || '').toLowerCase();
      if (!k || seen.has(k)) return false;
      seen.add(k);
      return !pins.some((a) => ((a.target && w.path && a.target.toLowerCase() === w.path.toLowerCase()) || (a.name && w.app && a.name.toLowerCase() === w.app.toLowerCase())));
    }).slice(0, 8);
    const t = (a, extraCls = '') => {
      const el = document.createElement('div');
      el.innerHTML = tile(a, extraCls);
      const n = el.firstElementChild; n.dataset.id = a.id; n.dataset.name = a.name;
      return n;
    };
    [...dock.querySelectorAll('.tile,.sep')].forEach((n) => n.remove());
    const add = (n) => dock.appendChild(n);
    left.forEach((a) => add(t(a, runningFor(a) ? 'run' : '')));
    add(t(ASSIST, 'gold'));
    right.forEach((a) => add(t(a, runningFor(a) ? 'run' : '')));
    if (extra.length) {
      add(Object.assign(document.createElement('div'), { className: 'sep' }));
      extra.forEach((w) => add(t({ id: 'win:' + w.id, name: w.app, icon: w.icon, wid: w.id }, 'run')));
    }
    add(Object.assign(document.createElement('div'), { className: 'sep' }));
    add(t({ id: 'mirage:downloads', name: 'Downloads', glyph: ic('dl') }));
    add(t({ id: 'mirage:trash', name: os === 'mac' ? 'Trash' : 'Recycle Bin', glyph: ic('trash') }));
    reportRect();
  }

  // Tell the core where the dock is, so the rest of its window lets clicks through.
  function reportRect() {
    if (!inApp) return;
    requestAnimationFrame(() => {
      const r = dock.getBoundingClientRect();
      call('dock_rect', { x: r.left, y: r.top - 48, w: r.width, h: r.height + 48 });
    });
  }

  // magnification
  dock.addEventListener('mousemove', (e) => {
    let hot = null;
    dock.querySelectorAll('.tile').forEach((n) => {
      const r = n.getBoundingClientRect();
      const d = Math.abs(e.clientX - (r.left + r.width / 2));
      const k = 1 + 0.26 * Math.max(0, 1 - d / 110) ** 2;
      n.style.setProperty('--k', k.toFixed(3));
      if (d < r.width / 2 + 5) hot = n;
    });
    if (hot) {
      const r = hot.getBoundingClientRect(), dr = dock.getBoundingClientRect();
      tip.textContent = hot.dataset.name;
      tip.style.left = (r.left - dr.left + r.width / 2) + 'px';
      tip.classList.add('show');
    } else tip.classList.remove('show');
  });
  dock.addEventListener('mouseleave', () => {
    dock.querySelectorAll('.tile').forEach((n) => n.style.setProperty('--k', 1));
    tip.classList.remove('show');
  });

  dock.addEventListener('click', async (e) => {
    const n = e.target.closest('.tile'); if (!n) return;
    closeMenu();
    const id = n.dataset.id;
    if (id === 'mirage:assistant') return call('toggle_view', { view: 'assistant' });
    if (id === 'mirage:downloads') return call('open_special', { which: 'downloads' });
    if (id === 'mirage:trash') return call('open_special', { which: 'trash' });
    if (id.startsWith('win:')) return call('focus_window', { id: id.slice(4) });
    const a = byId(id);
    const w = a && wins.find((w) => (a.target && w.path && w.path.toLowerCase() === a.target.toLowerCase()) || (w.app && a.name && w.app.toLowerCase() === a.name.toLowerCase()));
    if (w) return call('focus_window', { id: w.id });
    n.classList.add('bounce'); setTimeout(() => n.classList.remove('bounce'), 1800);
    call('launch', { id });
  });

  let menu = null;
  function closeMenu() { menu?.remove(); menu = null; }
  dock.addEventListener('contextmenu', async (e) => {
    e.preventDefault(); closeMenu();
    const n = e.target.closest('.tile'); if (!n || n.dataset.id.startsWith('mirage:')) return;
    const id = n.dataset.id, isPinned = pinned.includes(id);
    menu = document.createElement('div'); menu.className = 'dmenu glass deep';
    const dr = dock.getBoundingClientRect(), r = n.getBoundingClientRect();
    menu.style.left = (r.left - dr.left) + 'px';
    menu.innerHTML = id.startsWith('win:') ? `<button data-m="focus">${ic('eye')}Show window</button>` :
      `<button data-m="open">${ic('app')}Open</button><button data-m="pin">${ic('pin')}${isPinned ? 'Remove from dock' : 'Keep in dock'}</button><button data-m="left">${ic('chevron', '', 'transform:rotate(180deg)')}Move left</button><button data-m="right">${ic('chevron')}Move right</button>`;
    dock.appendChild(menu);
    menu.onclick = async (ev) => {
      const m = ev.target.closest('button')?.dataset.m; closeMenu();
      if (m === 'focus') call('focus_window', { id: id.slice(4) });
      if (m === 'open') call('launch', { id });
      if (m === 'pin') await saveSettings({ pinned: isPinned ? pinned.filter((x) => x !== id) : [...pinned, id] });
      if (m === 'left' || m === 'right') {
        const p = [...pinned], i = p.indexOf(id), j = i + (m === 'left' ? -1 : 1);
        if (i >= 0 && j >= 0 && j < p.length) { [p[i], p[j]] = [p[j], p[i]]; await saveSettings({ pinned: p }); }
      }
    };
  });
  document.addEventListener('click', (e) => { if (menu && !menu.contains(e.target)) closeMenu(); });

  on('windows', (list) => { wins = list || []; render(); });
  on('apps', (list) => { apps = list || []; render(); });
  on('launched', (id) => { const n = dock.querySelector(`[data-id="${window.CSS.escape(id)}"]`); n?.classList.add('bounce'); });
  onSettings((s) => { pinned = s.pinned || []; render(); });
  load();
  return { render, setWindows: (l) => { wins = l; render(); } };
}
