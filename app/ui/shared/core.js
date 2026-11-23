// Bridge between the UI and Mirage's Rust core. Outside the app (the demo stage
// used for the site and the videos) the same calls go to window.__mirageDemo.
import { injectDefs } from './icons.js';

const T = window.__TAURI__;
export const inApp = !!(T && T.core);
const q = new URLSearchParams(location.search);
// On the demo stage each view is an iframe and the stage owns __mirageDemo.
const stageDemo = () => {
  if (window.__mirageDemo) return window.__mirageDemo;
  try { return window.parent !== window ? window.parent.__mirageDemo : null; } catch { return null; }
};
export const os = inApp
  ? (/Mac/.test(navigator.userAgent) ? 'mac' : 'win')
  : (q.get('os') || stageDemo()?.os || 'win');
export const mod = os === 'mac' ? '⌥' : 'Alt';

const demo = stageDemo;

export async function call(cmd, args = {}) {
  if (inApp) return T.core.invoke(cmd, args);
  const d = demo();
  return d && d.call ? d.call(cmd, args) : null;
}
export function on(evt, cb) {
  if (inApp) return T.event.listen(evt, (e) => cb(e.payload));
  const d = demo();
  if (d) d.on(evt, cb);
  return Promise.resolve(() => {});
}
export function emit(evt, payload) {
  if (inApp) return T.event.emit(evt, payload);
  demo()?.emit(evt, payload);
}
export const win = inApp ? T.window.getCurrentWindow() : null;
export async function hideSelf() { if (win) await call('hide_view', { view: win.label }); }

// Settings are read once and kept in sync through the "settings" event.
let settings = null;
const subs = new Set();
export async function getSettings() {
  if (!settings) settings = await call('get_settings');
  return settings;
}
export async function saveSettings(patch) {
  settings = deepMerge(structuredClone(await getSettings()), patch);
  await call('set_settings', { settings });
  return settings;
}
export function onSettings(cb) { subs.add(cb); }
on('settings', (s) => { settings = s; subs.forEach((f) => f(s)); applyGlobal(s); });
function deepMerge(a, b) {
  for (const k in b) {
    if (b[k] && typeof b[k] === 'object' && !Array.isArray(b[k]) && a[k] && typeof a[k] === 'object') deepMerge(a[k], b[k]);
    else a[k] = b[k];
  }
  return a;
}

export function applyGlobal(s) {
  document.documentElement.classList.toggle('tinted', !!s?.tinted);
  document.documentElement.dataset.os = os;
}

// Desktop-layer windows (bar, dock, widgets) sit where only the wallpaper is
// behind them, so they draw the wallpaper themselves and blur it with CSS.
export async function deskLayer() {
  document.documentElement.classList.add('css-blur', 'desk');
  const st = document.createElement('style');
  st.textContent = `html.desk body::before{content:"";position:fixed;z-index:-1;left:var(--wp-x,0);top:var(--wp-y,0);width:var(--wp-w,100vw);height:var(--wp-h,100vh);background:var(--wp) center/cover no-repeat,#04060B}`;
  document.head.appendChild(st);
  if (!inApp) return;
  const place = async () => {
    const info = await call('wallpaper_info');
    if (!info) return;
    const pos = await win.outerPosition();
    const sf = await win.scaleFactor();
    const r = document.documentElement.style;
    r.setProperty('--wp', `url(${info.data_url})`);
    r.setProperty('--wp-x', `${(info.screen_x - pos.x) / sf}px`);
    r.setProperty('--wp-y', `${(info.screen_y - pos.y) / sf}px`);
    r.setProperty('--wp-w', `${info.screen_w / sf}px`);
    r.setProperty('--wp-h', `${info.screen_h / sf}px`);
  };
  await place();
  on('wallpaper', place);
  win.onMoved(place);
  win.onResized(place);
}

export async function boot() {
  injectDefs();
  const h = document.documentElement;
  if (window.__mirageVib) h.classList.add('vib');
  if (window.__mirageSolid) h.classList.add('solid');
  const s = await getSettings();
  applyGlobal(s);
  return s;
}
// Tells the core this view has mounted, so events meant for it are not lost.
export function ready() {
  if (inApp) setTimeout(() => emit('view-ready', win.label), 60);
}

// ---------- formatting ----------
export function hhmm(d = new Date(), h24 = true) {
  let h = d.getHours();
  const m = String(d.getMinutes()).padStart(2, '0');
  if (!h24) { h = h % 12 || 12; }
  return [String(h).padStart(h24 ? 2 : 1, '0'), m];
}
export const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
export const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
export function bytes(n) {
  if (n == null) return '–';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (n >= 100 || i === 0 ? n.toFixed(0) : n.toFixed(1)) + ' ' + u[i];
}
export function rate(n) {
  if (n == null) return '–';
  return n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB/s' : (n / 1024).toFixed(0) + ' KB/s';
}
export function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
export function el(html) {
  const t = document.createElement('template');
  t.innerHTML = html.trim();
  return t.content.firstElementChild;
}

// App icon tile: real icon (tinted through CSS when the setting is on), or a
// letter tile when the OS gave no icon.
export function tile(app, cls = '') {
  if (app.glyph) return `<div class="tile ${cls} ${app.tone || ''}">${app.glyph}</div>`;
  if (app.icon) return `<div class="tile ${cls}"><img src="${app.icon}" alt=""></div>`;
  return `<div class="tile letter ${cls}"><b>${esc((app.name || '?').trim()[0] || '?')}</b></div>`;
}

// Current time; the demo stage can drive it frame by frame.
export const now = () => (!inApp && demo()?.now ? demo().now() : new Date());
export function every(ms, fn) {
  fn();
  if (!inApp && demo()?.tick) { demo().tick(fn); return; }
  setInterval(fn, ms);
}
