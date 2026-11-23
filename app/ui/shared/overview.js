// Overview: every open window as a card. Click to switch, or snap it into
// a layout. Type to filter.
import { ic } from './icons.js';
import { call, on, os, mod, esc, tile, inApp, hideSelf } from './core.js';

const CSS = `
.ov{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;padding:6vh 5vw 4vh;gap:28px;background:rgba(4,6,11,.55)}
.ov .bg{position:absolute;inset:0;z-index:-1;background:var(--wpf,#04060B) center/cover no-repeat;filter:blur(22px) brightness(.55) saturate(1.2);transform:scale(1.08)}
.ov .head{display:flex;align-items:center;gap:16px;width:min(640px,90%);padding:12px 18px;border-radius:16px}
.ov .head input{flex:1;background:none;border:0;outline:0;font-size:18px;caret-color:var(--gold)}
.ov .grid{flex:1;width:100%;display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:26px;align-content:center;overflow:auto;padding:6px}
.ov .w{border-radius:18px;padding:12px;display:flex;flex-direction:column;gap:10px;cursor:pointer;transition:transform .18s var(--ease),box-shadow .18s;position:relative}
.ov .w:hover,.ov .w.sel{transform:translateY(-3px);box-shadow:0 0 0 1.5px var(--gold),0 20px 50px rgba(0,0,0,.45)}
.ov .th{aspect-ratio:16/10;border-radius:10px;background:rgba(4,6,11,.5) center/contain no-repeat;display:grid;place-items:center;overflow:hidden}
.ov .th .tile{width:72px;height:72px;border-radius:18px}
.ov .meta{display:flex;align-items:center;gap:10px;min-width:0}
.ov .meta .tile{width:28px;height:28px;border-radius:8px}
.ov .meta .tile.letter b{font-size:13px}
.ov .meta div{min-width:0}
.ov .meta b{display:block;font-size:14px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ov .meta small{display:block;font-size:12px;color:var(--mute);white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ov .acts{position:absolute;top:18px;right:18px;display:flex;gap:6px;opacity:0;transition:opacity .15s}
.ov .w:hover .acts{opacity:1}
.ov .acts button{width:30px;height:30px;border-radius:9px;display:grid;place-items:center;background:rgba(4,6,11,.75);border:1px solid var(--line2)}
.ov .acts button:hover{color:var(--gold);border-color:var(--gold-line)}
.ov .foot{display:flex;gap:10px;flex-wrap:wrap;justify-content:center}
.ov .none{color:var(--mute);font-size:16px;text-align:center;grid-column:1/-1}
`;

const LAYOUTS = os === 'mac'
  ? [['left', 'Left half', '⌃⌥←'], ['right', 'Right half', '⌃⌥→'], ['max', 'Fill', '⌃⌥↑'], ['center', 'Center', '⌃⌥C']]
  : [['left', 'Left half', 'Ctrl Alt ←'], ['right', 'Right half', 'Ctrl Alt →'], ['max', 'Fill', 'Ctrl Alt ↑'], ['center', 'Center', 'Ctrl Alt C'], ['tl', 'Quarters', 'Ctrl Alt U I J K']];

export function mountOverview(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  document.documentElement.classList.add('css-blur');
  root.innerHTML = `<div class="ov"><div class="bg"></div>
    <div class="head glass">${ic('search')}<input placeholder="Filter windows" spellcheck="false"><span class="kbd">${mod} ↑</span></div>
    <div class="grid"></div>
    <div class="foot">${LAYOUTS.map(([, n, k]) => `<span class="chip">${n} <span style="color:var(--ivory);margin-left:6px">${k}</span></span>`).join('')}</div></div>`;
  const $ = (s) => root.querySelector(s);
  const grid = $('.grid'), input = $('input');
  let wins = [], sel = 0, shown = [];

  function paint() {
    const q = input.value.trim().toLowerCase();
    shown = wins.filter((w) => !q || (w.app + ' ' + w.title).toLowerCase().includes(q));
    sel = Math.min(sel, Math.max(0, shown.length - 1));
    grid.innerHTML = shown.map((w, i) => `<div class="w glass ${i === sel ? 'sel' : ''}" data-i="${i}">
      <div class="th" ${w.thumb ? `style="background-image:url(${w.thumb})"` : ''}>${w.thumb ? '' : tile({ name: w.app, icon: w.icon })}</div>
      <div class="meta">${tile({ name: w.app, icon: w.icon })}<div><b>${esc(w.title || w.app)}</b><small>${esc(w.app)}</small></div></div>
      <div class="acts">${os === 'win' ? `<button data-s="left" title="Left half">${ic('layout', '', 'transform:scaleX(-1)')}</button><button data-s="right" title="Right half">${ic('layout')}</button><button data-s="max" title="Fill">${ic('display')}</button>` : ''}<button data-s="close" title="Close window">${ic('x')}</button></div>
    </div>`).join('') || `<div class="none">No open windows.</div>`;
  }
  async function load() {
    wins = (await call('list_windows', { thumbs: true })) || [];
    sel = 0; input.value = ''; paint();
    const wp = await call('wallpaper_info', {});
    if (wp) root.querySelector('.ov').style.setProperty('--wpf', `url(${wp.data_url})`);
    setTimeout(() => input.focus(), 30);
  }
  grid.addEventListener('click', async (e) => {
    const card = e.target.closest('.w'); if (!card) return;
    const w = shown[+card.dataset.i];
    const s = e.target.closest('[data-s]')?.dataset.s;
    if (s === 'close') { await call('close_window', { id: w.id }); wins = wins.filter((x) => x !== w); return paint(); }
    await hideSelf();
    if (s) call('snap_window', { id: w.id, layout: s }); else call('focus_window', { id: w.id });
  });
  input.addEventListener('input', paint);
  window.addEventListener('keydown', async (e) => {
    const cols = Math.max(1, Math.floor(grid.clientWidth / 326));
    if (e.key === 'Escape') return hideSelf();
    if (e.key === 'ArrowRight') sel = Math.min(shown.length - 1, sel + 1);
    else if (e.key === 'ArrowLeft') sel = Math.max(0, sel - 1);
    else if (e.key === 'ArrowDown') sel = Math.min(shown.length - 1, sel + cols);
    else if (e.key === 'ArrowUp') sel = Math.max(0, sel - cols);
    else if (e.key === 'Enter' && shown[sel]) { await hideSelf(); return call('focus_window', { id: shown[sel].id }); }
    else return;
    e.preventDefault(); paint();
  });
  grid.addEventListener('mousedown', (e) => { if (e.target === grid) hideSelf(); });
  on('overview', load);
  if (!inApp) load();
  return { load, setWindows: (l) => { wins = l; paint(); } };
}
