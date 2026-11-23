// Mirage demo stage. Each Mirage view runs unchanged in an iframe, placed the
// way the app places its window on a 1920x1080 screen, and talks to this file
// through window.__mirageDemo instead of the Rust core. The script at the
// bottom plays the walkthrough used for the site's Windows and Mac videos.
import { ic } from '../shared/icons.js';

const q = new URLSearchParams(location.search);
const OS = q.get('os') === 'mac' ? 'mac' : 'win';
const MAC = OS === 'mac';
const W = 1920, H = 1080;
document.documentElement.dataset.os = OS;

// ---------------------------------------------------------------- clock
// Tuesday 24 November 2026, 09:41, moving with the page's clock (which the
// video renderer controls frame by frame).
const BASE = new Date(2026, 10, 24, 9, 41, 12).getTime();
const T0 = performance.now();
const now = () => new Date(BASE + (performance.now() - T0));
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- data
const WALL = (id, full) => full ? `/src-tauri/wallpapers/${id}.jpg` : `/ui/wallpapers/${id}.jpg`;
const WALLPAPERS = [
  ['first-light', 'First Light', 'The day is quiet before it is yours. Begin with one thing.'],
  ['connected-world', 'Connected World', 'Every line is someone reaching out. Answer the ones that matter.'],
  ['dune-sea', 'Dune Sea', 'Wind moves mountains a grain at a time. So does steady work.'],
  ['orbits', 'Orbits', 'Everything you care about keeps its own orbit. Give each its time.'],
];
let wallpaper = 'first-light';

const app = (id, name, g, tone = '') => ({ id: 'demo:' + id, name, glyph: ic(g), tone, target: id });
const APPS = [
  app('files', MAC ? 'Files' : 'File Explorer', 'folder'), app('mail', 'Mail', 'mail'), app('calendar', 'Calendar', 'cal'),
  app('notes', 'Notes', 'note'), app('browser', 'Browser', 'globe'), app('music', 'Music', 'music'),
  app('photos', 'Photos', 'photo'), app('code', 'Code', 'code'), app('terminal', 'Terminal', 'term'), app('settings', 'System Settings', 'gear'),
];

const settings = {
  onboarded: true, autostart: true, clock24: !MAC ? true : false, tinted: true, glass: 'os',
  surfaces: { bar: !MAC, dock: !MAC, widgets: true, ambient: true },
  widgetsSide: MAC ? 'left' : 'right', reserveSpace: true, hideTaskbar: true, ambientMinutes: 10,
  wallpaper, ai: { provider: 'anthropic', model: 'claude-opus-5-5', ollamaUrl: 'http://localhost:11434' },
  voice: { wake: true, speak: true, voiceName: '' },
  permissions: { files_move: 'ask', mail_compose: 'ask', delete: 'ask', run_command: 'ask', open_app: 'allow', search_files: 'allow' },
  pinned: ['files', 'mail', 'calendar', 'notes', 'browser', 'music', 'photos', 'code'].map((x) => 'demo:' + x),
  location: { name: 'Amman', lat: 31.95, lon: 35.93 }, calendarIcs: 'demo', aiInsight: true, bridge: { enabled: false, port: 47811 },
};

const thumb = (title, accent) => 'data:image/svg+xml,' + encodeURIComponent(
  `<svg xmlns="http://www.w3.org/2000/svg" width="640" height="400"><rect width="640" height="400" rx="10" fill="#0D1424"/><rect width="640" height="34" fill="#111A2E"/>` +
  `<text x="18" y="22" font-family="Arial" font-size="13" fill="#9EA4B3">${title}</text><rect x="28" y="62" width="300" height="20" rx="5" fill="#F4EFE6" opacity=".85"/>` +
  [0, 1, 2, 3, 4, 5].map((i) => `<rect x="28" y="${104 + i * 30}" width="${560 - (i % 3) * 90}" height="10" rx="5" fill="#F4EFE6" opacity=".09"/>`).join('') +
  `<rect x="28" y="300" width="160" height="60" rx="8" fill="${accent}" opacity=".25"/></svg>`);
const WINDOWS = [
  { id: 'w1', app: 'Notes', title: 'Launch plan', path: 'notes', thumb: thumb('Launch plan · Notes', '#F2C46B') },
  { id: 'w2', app: 'Mail', title: 'Inbox (3)', path: 'mail', thumb: thumb('Inbox · Mail', '#7FA8F2') },
  { id: 'w3', app: 'Browser', title: 'Quarterly numbers', path: 'browser', thumb: thumb('Quarterly numbers · Browser', '#7FD4A8') },
  { id: 'w4', app: 'Code', title: 'mirage-osshell', path: 'code', thumb: thumb('mirage-osshell · Code', '#B9772E') },
];
const FILES = [
  { name: 'Launch plan.docx', dir: 'Documents/Work', is_dir: false },
  { name: 'Launch photos', dir: 'Pictures', is_dir: true },
  { name: 'Lunch receipts.pdf', dir: 'Downloads', is_dir: false },
  { name: 'Invoice 2041.pdf', dir: 'Downloads', is_dir: false },
  { name: 'Invoice 2042.pdf', dir: 'Downloads', is_dir: false },
  { name: 'Quarterly numbers.xlsx', dir: 'Documents/Finance', is_dir: false },
];

const day = (h, m) => new Date(2026, 10, 24, h, m).toISOString();
const widgets = () => ({
  calendar: { configured: true, events: [
    { title: 'Design review', start: day(10, 0), end: day(10, 45), location: 'Room 4' },
    { title: 'Lunch with Sara', start: day(13, 0), end: day(14, 0) },
    { title: 'Launch sync', start: day(16, 30), end: day(17, 0), location: 'Video call' },
  ] },
  weather: { place: 'Amman', temp: 17, desc: 'Clear', wind: 9, hourly: [10, 11, 12, 13, 14, 15].map((h, i) => ({ t: day(h, 0), temp: 17 + i })) },
  focus: { on: false, today_minutes: 95 },
  insight: { html: 'You have <em>two clear hours</em> before your design review. A good window for the launch plan.', source: 'your calendar' },
});

// Usage figures: the renderer passes in what CI measured on a real machine.
const SMOKE = { mem: +q.get('mem') || null, cpu: q.has('cpu') ? +q.get('cpu') : null };

// ---------------------------------------------------------------- bridge
const subs = {};
const emit = (evt, payload) => (subs[evt] || []).forEach((f) => { try { f(payload); } catch (e) { console.error(evt, e); } });
const ticks = [];
let pending = null; // assistant step waiting for Allow
const replies = [];

window.__mirageDemo = {
  os: OS,
  now,
  tick: (fn) => ticks.push(fn),
  on: (evt, cb) => (subs[evt] ||= []).push(cb),
  emit,
  async call(cmd, a = {}) {
    switch (cmd) {
      case 'get_settings': return structuredClone(settings);
      case 'set_settings': Object.assign(settings, a.settings); emit('settings', structuredClone(settings)); return settings;
      case 'list_apps': return APPS;
      case 'list_windows': return WINDOWS;
      case 'search_files': {
        const s = (a.q || '').toLowerCase();
        return FILES.filter((f) => f.name.toLowerCase().includes(s)).map((f) => ({ ...f, path: f.dir + '/' + f.name }));
      }
      case 'wallpaper_info': return { id: wallpaper, data_url: WALL(wallpaper, true) };
      case 'list_wallpapers': return WALLPAPERS.map(([id, name, caption]) => ({ id, name, caption, thumb: WALL(id) }));
      case 'set_wallpaper': setWallpaper(a.id); return null;
      case 'next_wallpaper': setWallpaper(WALLPAPERS[(WALLPAPERS.findIndex((w) => w[0] === wallpaper) + 1) % WALLPAPERS.length][0]); return null;
      case 'widgets_data': return widgets();
      case 'control_state': return { focus: false, dark: true, volume: 62 };
      case 'show_view': show(a.view, a.section); return null;
      case 'toggle_view': toggle(a.view); return null;
      case 'hide_view': hide(a.view); return null;
      case 'assistant_send': assistant(a.text); return null;
      case 'assistant_answer': if (pending && pending.id === a.id) pending.resolve(a.allow); return null;
      case 'about': return { version: '2.0.0', usage: 'Measured in CI', data_dir: '' };
      default: return null;
    }
  },
};

// ---------------------------------------------------------------- views
const GEOM = {
  bar: [0, 0, W, 34],
  dock: [0, H - 150, W, 150],
  widgets: MAC ? [8, 35, 372, H - 25 - 20] : [W - 372 - 8, 44, 372, H - 34 - 96 - 20],
  palette: [(W - 880) / 2, H * 0.16, 880, 560],
  control: [W - 380 - 12, (MAC ? 25 : 34) + 8, 380, 540],
  toast: [W - 380 - 12, (MAC ? 25 : 34) + 12, 380, 96],
  overview: [0, 0, W, H],
  ambient: [0, 0, W, H],
};
const RADIUS = { palette: 26, control: 22, toast: 18 };
const DESK = ['bar', 'dock', 'widgets'];
const frames = {};

function mount(view) {
  const f = document.createElement('iframe');
  const [x, y, w, h] = GEOM[view];
  Object.assign(f.style, { left: x + 'px', top: y + 'px', width: w + 'px', height: h + 'px' });
  f.src = `../${view}.html?os=${OS}`;
  f.dataset.view = view;
  if (!DESK.includes(view)) f.className = 'float' + (GEOM[view][2] === W ? ' full' : ' shadow');
  if (RADIUS[view]) f.style.borderRadius = RADIUS[view] + 'px';
  f.style.zIndex = { widgets: 1, bar: 5, dock: 6, palette: 20, control: 21, toast: 22, overview: 40, ambient: 50 }[view];
  document.getElementById('views').appendChild(f);
  frames[view] = f;
  return new Promise((r) => f.addEventListener('load', () => { if (DESK.includes(view)) placeDesk(f); r(); }));
}
// Desk views draw the wallpaper behind their own glass (as deskLayer does in the app).
function placeDesk(f) {
  const [x, y] = GEOM[f.dataset.view];
  const r = f.contentDocument?.documentElement.style;
  if (!r) return;
  r.setProperty('--wp', `url(${WALL(wallpaper, true)})`);
  r.setProperty('--wp-x', -x + 'px'); r.setProperty('--wp-y', -y + 'px');
  r.setProperty('--wp-w', W + 'px'); r.setProperty('--wp-h', H + 'px');
}
const open = new Set();
function show(view, section) {
  if (view === 'assistant' || view === 'voice') { show('palette'); emit('palette', { mode: view === 'voice' ? 'voice' : 'assistant' }); return; }
  const f = frames[view]; if (!f) return;
  f.classList.add('on'); open.add(view);
  const evt = { palette: 'palette', overview: 'overview', ambient: 'ambient', control: 'control', settings: 'settings-open' }[view];
  if (evt) emit(evt, view === 'palette' ? { mode: 'search' } : { section });
}
function hide(view) { if (view === 'assistant' || view === 'voice') view = 'palette'; frames[view]?.classList.remove('on'); open.delete(view); }
function toggle(view) { const v = view === 'assistant' ? 'palette' : view; open.has(v) ? hide(v) : show(view); }

function setWallpaper(id) {
  wallpaper = id; settings.wallpaper = id;
  const a = document.getElementById('wp'), b = document.getElementById('wp2');
  b.src = WALL(id, true); b.style.opacity = 1;
  setTimeout(() => { a.src = b.src; b.style.opacity = 0; }, 1300);
  DESK.forEach((v) => frames[v] && placeDesk(frames[v]));
  emit('wallpaper', { id });
}

// ---------------------------------------------------------------- live stats
let tickN = 0;
function stats() {
  const t = tickN++;
  const cpu = 9 + 6 * Math.abs(Math.sin(t * 0.7)) + (t % 5 === 0 ? 7 : 0);
  return {
    cpu, mem: 41 + Math.sin(t * 0.13) * 1.5, gpu: MAC ? null : 6 + 3 * Math.abs(Math.sin(t * 0.4)),
    down: 180000 + 90000 * Math.abs(Math.sin(t * 0.9)), up: 24000, volume: 62, muted: false,
    battery: { pct: 84, charging: false }, mem_used: 13.1 * 2 ** 30, mem_total: 32 * 2 ** 30,
    mirage_mem: SMOKE.mem ? SMOKE.mem * 2 ** 20 : null, mirage_cpu: SMOKE.cpu ?? 0,
  };
}
function everySecond() {
  const s = stats();
  emit('stats', s);
  ticks.forEach((f) => { try { f(); } catch {} });
  if (MAC) {
    const d = now();
    document.querySelector('#sysbar .clk').textContent = d.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' }) + '  ' + d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' });
    document.querySelector('#sysbar .cpu').textContent = Math.round(s.cpu) + '%';
  }
}

// ---------------------------------------------------------------- assistant
async function assistant(text) {
  const t = text.toLowerCase();
  const step = (id, status, title, detail, allow_label) => emit('assistant', { type: 'step', id, status, title, detail, allow_label });
  const say = (s) => emit('assistant', { type: 'message', text: s });
  await wait(900);
  if (t.includes('invoice')) {
    step('s1', 'running', 'Search files', '“invoice” in Downloads'); await wait(700);
    step('s1', 'done', 'Search files', 'Found 4 invoices in Downloads'); await wait(500);
    step('s2', 'ask', 'Move 4 files', 'Downloads → Documents/Finance/Invoices', 'Move them');
    const ok = await new Promise((resolve) => { pending = { id: 's2', resolve }; });
    pending = null;
    step('s2', ok ? 'running' : 'denied', 'Move 4 files', 'Downloads → Documents/Finance/Invoices'); await wait(600);
    if (ok) step('s2', 'done', 'Move 4 files', 'Downloads → Documents/Finance/Invoices');
    await wait(500);
    say(ok ? 'Done. Your 4 invoices are in Documents/Finance/Invoices, and nothing was overwritten.' : 'Okay, I left them where they are.');
  } else if (t.includes('calendar') || t.includes('today')) {
    step('c1', 'done', 'Read today’s calendar', '3 events'); await wait(700);
    say('Three things today: the design review at 10:00, lunch with Sara at 13:00 and the launch sync at 16:30. You’re free from 10:45 to 13:00.');
  } else {
    say('On it.');
  }
  await wait(200);
  emit('assistant', { type: 'done' });
}

// ---------------------------------------------------------------- stage helpers
const $ = (s) => document.querySelector(s);
const cursor = $('#cursor');
let cx = 1200, cy = 700;
cursor.style.transform = `translate(${cx}px,${cy}px)`;
function frameOf(view) { return frames[view]; }
function inView(view, sel) {
  const f = frameOf(view);
  const n = f.contentDocument.querySelector(sel);
  if (!n) return null;
  const fr = f.getBoundingClientRect(), r = n.getBoundingClientRect();
  return { n, x: fr.left + r.left + r.width / 2, y: fr.top + r.top + r.height / 2, f };
}
async function moveTo(x, y, ms = 700) {
  cursor.style.transitionDuration = ms + 'ms';
  cx = x; cy = y; cursor.style.transform = `translate(${x}px,${y}px)`;
  await wait(ms);
}
async function clickOn(view, sel, ms = 700) {
  const t = inView(view, sel); if (!t) return;
  await moveTo(t.x, t.y, ms);
  cursor.classList.remove('click'); void cursor.offsetWidth; cursor.classList.add('click');
  await wait(120);
  t.n.click();
  await wait(300);
}
// Sweep the pointer along the dock so it magnifies as in the app.
async function sweepDock(x0, x1, ms) {
  const f = frameOf('dock'), d = f.contentDocument.querySelector('.dock');
  const fr = f.getBoundingClientRect();
  const steps = Math.round(ms / 33);
  cursor.style.transitionDuration = '0ms';
  for (let i = 0; i <= steps; i++) {
    const x = x0 + (x1 - x0) * (i / steps), y = fr.top + 100;
    cursor.style.transform = `translate(${x}px,${y}px)`;
    d.dispatchEvent(new MouseEvent('mousemove', { clientX: x - fr.left, clientY: y - fr.top, bubbles: true }));
    await wait(33);
  }
  d.dispatchEvent(new MouseEvent('mouseleave'));
}
async function type(view, sel, text, cps = 18) {
  const f = frameOf(view), inp = f.contentDocument.querySelector(sel);
  for (const ch of text) {
    inp.value += ch;
    inp.dispatchEvent(new Event('input', { bubbles: true }));
    await wait(1000 / cps * (ch === ' ' ? 1.4 : 1));
  }
}
function key(view, sel, k) {
  const n = frameOf(view).contentDocument.querySelector(sel);
  n.dispatchEvent(new KeyboardEvent('keydown', { key: k, bubbles: true }));
}
async function keys(list, ms = 1100) {
  const k = $('#keys');
  k.innerHTML = list.map((x) => `<kbd>${x}</kbd>`).join('');
  k.classList.add('on'); await wait(ms); k.classList.remove('on');
}
function caption(title, sub = '') {
  const c = $('#cap');
  if (!title) { c.classList.remove('on'); return; }
  c.classList.remove('on');
  setTimeout(() => { c.querySelector('b').textContent = title; c.querySelector('span').textContent = sub; c.classList.add('on'); }, title && c.classList.contains('on') ? 0 : 0);
}
function card(html) { const c = $('#card'); if (!html) return c.classList.remove('on'); c.innerHTML = html; c.classList.add('on'); }
const shot = (name) => { window.__stage.mark = name; };
const MARK = '<div class="mark">' + '<i></i>'.repeat(9) + '</div>';

// ---------------------------------------------------------------- stand-in apps
function apps() {
  const dots = MAC ? '<span class="dots"><i></i><i></i><i></i></span>' : '';
  const ctl = MAC ? '' : '<span class="ctl"><span>—</span><span>☐</span><span>✕</span></span>';
  const top = MAC ? 25 : 34;
  $('#apps').innerHTML = `
  <div class="win" style="left:${MAC ? 520 : 150}px;top:${top + 70}px;width:860px;height:600px">
    <div class="tb">${dots}<b>Launch plan</b><span>Notes</span>${ctl}</div>
    <div class="body"><h1>Launch plan</h1><div class="sub">Edited 9:32</div>
      <div class="ln" style="width:92%"></div><div class="ln" style="width:84%"></div><div class="ln" style="width:88%"></div>
      <div class="ln" style="width:52%;margin-bottom:26px"></div><div class="ln" style="width:90%"></div><div class="ln" style="width:76%"></div><div class="ln" style="width:81%"></div></div>
  </div>
  <div class="win" style="left:${MAC ? 900 : 560}px;top:${top + 230}px;width:820px;height:520px">
    <div class="tb">${dots}<b>Inbox</b><span>Mail</span>${ctl}</div>
    <div class="side"><b>Inbox</b> 3<br>Drafts<br>Sent<br>Archive</div>
    <div class="body" style="margin-left:190px;padding-top:12px">
      <div class="row"><div class="av"></div><div><b>Sara</b><small>Lunch at 1? I booked the usual place.</small></div></div>
      <div class="row"><div class="av"></div><div><b>Design team</b><small>Review notes for today</small></div></div>
      <div class="row"><div class="av"></div><div><b>Finance</b><small>Please file this week’s invoices</small></div></div>
    </div>
  </div>`;
  $('#sysbar').innerHTML = `<span class="sq"></span><b>Notes</b><span>File</span><span>Edit</span><span>View</span><span>Window</span><span>Help</span>
    <span class="r"><span class="mi"><i>${'<s></s>'.repeat(9)}</i><span class="cpu">12%</span></span><span class="clk"></span></span>`;
}

// ---------------------------------------------------------------- script
const SPACE = MAC ? ['⌥', 'Space'] : ['Alt', 'Space'];
const UP = MAC ? ['⌥', '↑'] : ['Alt', '↑'];

async function play() {
  const done = window.__stage;
  card(`${MARK}<h1>Mirage</h1><p>A calm, fast layer over ${MAC ? 'macOS' : 'Windows'}.</p>`);
  await wait(2600);
  card('');
  emit('foreground', { app: 'Notes', title: 'Launch plan' });
  await wait(900);
  caption(MAC ? 'Mirage on your Mac' : 'Your desktop, rebuilt', MAC ? 'Widgets, a command bar and an assistant beside the menu bar you know.' : 'A live top bar, a dock, widgets and original 4K wallpapers. Windows stays underneath.');
  if (!MAC) { await wait(900); await sweepDock(560, 960, 1300); shot('desktop'); await sweepDock(960, 1360, 1300); } else { await wait(1500); shot('desktop'); await wait(1500); }
  await wait(600);

  caption('One bar for everything', 'Apps, files, actions and answers.');
  await keys(SPACE, 900);
  show('palette'); await wait(500);
  await type('palette', 'input', 'launch', 9);
  await wait(900); shot('palette'); await wait(800);
  frameOf('palette').contentDocument.querySelector('input').value = '';
  frameOf('palette').contentDocument.querySelector('input').dispatchEvent(new Event('input'));
  await wait(300);

  caption('Ask Mirage to do it', 'It shows every step and asks before it moves, sends or deletes.');
  await type('palette', 'input', 'Move this week’s invoices from Downloads into Finance', 24);
  await wait(400);
  key('palette', 'input', 'Tab');
  for (let i = 0; i < 60 && !inView('palette', '.step [data-ans="1"]'); i++) await wait(100);
  await wait(700);
  await clickOn('palette', '.step [data-ans="1"]', 800);
  await wait(2600); shot('assistant'); await wait(600);
  hide('palette');
  await wait(500);

  caption('“Hey Mirage”', 'Wake word and speech run on your computer. Nothing is sent until you ask.');
  show('voice'); await wait(200);
  for (let i = 0; i < 40; i++) { emit('voice', { state: 'listening', level: 0.12 + 0.25 * Math.abs(Math.sin(i * 0.8)) }); await wait(60); }
  emit('voice', { state: 'thinking' }); await wait(500);
  emit('voice', { state: 'idle', final: 'What’s on my calendar today?' });
  await wait(3800); shot('voice'); await wait(800);
  hide('palette'); await wait(400);

  caption('Every window at a glance', 'Overview and snapping, from the keyboard.');
  await keys(UP, 800);
  show('overview'); await wait(1500); shot('overview'); await wait(1500);
  hide('overview'); await wait(500);

  caption('Control center', 'Focus, volume, dark mode and wallpapers in one place.');
  const cc = inView(MAC ? 'widgets' : 'bar', MAC ? '.fbtn' : '[data-a=control]');
  if (!MAC && cc) { await moveTo(cc.x, cc.y, 700); }
  show('control'); await wait(1400);
  const wpb = inView('control', '.wps button[data-w="connected-world"]');
  if (wpb) { await clickOn('control', '.wps button[data-w="connected-world"]', 700); }
  else setWallpaper('connected-world');
  await wait(1600);
  hide('control'); await wait(400);
  caption('Wallpapers that mean something', 'Original 4K art, each with a line to start the day.');
  await wait(1400);
  shot('wallpaper'); await wait(300);
  show('toast'); emit('toast', { title: 'Design review in 15 minutes', body: 'Room 4 · Your notes are in Launch plan.', ms: 3200 });
  await wait(3200);

  caption('');
  await wait(300);
  show('ambient'); await wait(2500); shot('ambient'); await wait(1700);
  hide('ambient'); await wait(300);

  card(`${MARK}<h1>Mirage</h1><p>Free for Windows and macOS.</p><small>Your AI · your rules · on your machine</small>`);
  await wait(3600);
  done?.end?.();
}

// ---------------------------------------------------------------- boot
apps();
document.getElementById('wp').src = WALL(wallpaper, true);
const views = [...(MAC ? [] : ['bar', 'dock']), 'widgets', 'palette', 'control', 'toast', 'overview', 'ambient'];
await Promise.all(views.map(mount));
setInterval(everySecond, 1000);
everySecond();
window.__stage = window.__stage || {};
window.__stage.ready = true;
window.__stage.play = play;
window.__stage.show = show;
window.__stage.hide = hide;
window.__stage.setWallpaper = setWallpaper;
if (q.get('auto') === '1') play();
