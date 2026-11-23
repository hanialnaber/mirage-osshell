// Command bar and assistant. Type to find apps, files, actions and settings,
// or ask Mirage to do something. Mirage shows each step it takes and asks
// before anything set to "Ask".
import { ic } from './icons.js';
import { call, on, os, mod, esc, tile, getSettings, onSettings, hideSelf, inApp } from './core.js';

const CSS = `
.pal{position:absolute;inset:0;display:flex;flex-direction:column;border-radius:26px;overflow:hidden}
.pal .in{display:flex;align-items:center;gap:16px;padding:20px 24px;border-bottom:1px solid var(--line);flex:none}
.pal .in .orb{--s:30px}
.pal input{flex:1;background:none;border:0;outline:0;font-size:24px;font-weight:400;caret-color:var(--gold);min-width:0}
.pal input::placeholder{color:var(--faint)}
.pal .mic{width:34px;height:34px;border-radius:10px;display:grid;place-items:center;color:var(--mute)}
.pal .mic:hover{background:rgba(244,239,230,.07);color:var(--ivory)}
.pal .mic.on{color:var(--gold);background:var(--gold-dim)}
.wave{display:flex;gap:3px;align-items:center;height:22px}
.wave i{width:3px;border-radius:2px;background:var(--gold);height:4px;transition:height .08s}
.pal .cols{flex:1;display:grid;grid-template-columns:1fr;min-height:0}
.pal.ai .cols{grid-template-columns:400px 1fr}
.pal .res{padding:12px;overflow:auto;min-height:0}
.pal.ai .res{border-right:1px solid var(--line)}
.pal .res .label{padding:10px 12px 6px}
.it{display:flex;align-items:center;gap:12px;padding:9px 12px;border-radius:12px;font-size:14.5px;min-height:52px}
.it .tile{width:34px;height:34px;border-radius:9px}
.it .tile img{width:80%;height:80%}
.it .tile.letter b{font-size:15px}
.it .tx{min-width:0;flex:1}
.it .tx div{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.it small{display:block;color:var(--mute);font-size:12px;margin-top:2px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.it.sel{background:var(--gold-dim);box-shadow:inset 0 0 0 1px rgba(242,196,107,.3)}
.it .k{margin-left:auto}
.it b.hl{color:var(--gold);font-weight:inherit}
.pal .empty{padding:28px 14px;color:var(--mute);font-size:14px;line-height:1.6}
.ai{display:none;flex-direction:column;min-height:0}
.pal.ai .ai{display:flex}
.ai .who{display:flex;align-items:center;gap:10px;padding:18px 24px 12px}
.ai .log{flex:1;overflow:auto;padding:4px 24px 18px;display:flex;flex-direction:column;gap:12px}
.ai p{font-size:15.5px;line-height:1.55;white-space:pre-wrap}
.ai .me{align-self:flex-end;max-width:85%;padding:9px 14px;border-radius:14px 14px 4px 14px;background:rgba(244,239,230,.07);font-size:14.5px}
.step{display:flex;align-items:center;gap:12px;padding:11px 14px;border-radius:14px;background:rgba(244,239,230,.04);border:1px solid var(--line);font-size:14px}
.step .sx{flex:1;min-width:0}
.step small{display:block;color:var(--mute);font-size:12.5px;margin-top:3px;white-space:pre-wrap;word-break:break-word}
.st{font-family:var(--mono);font-size:10.5px;letter-spacing:.1em;text-transform:uppercase;padding:4px 8px;border-radius:6px;flex:none}
.st.done,.st.allowed{color:var(--ok);background:rgba(127,212,168,.1)}
.st.ask,.st.running{color:var(--gold);background:var(--gold-dim)}
.st.denied,.st.error{color:var(--bad);background:rgba(240,138,122,.1)}
.step .btns{display:flex;gap:8px}
.step .btn{padding:7px 13px;font-size:13px}
.ai .foot{display:flex;gap:8px;padding:12px 24px;border-top:1px solid var(--line);flex-wrap:wrap;align-items:center}
.thinking{display:flex;gap:5px;padding:6px 0}
.thinking i{width:6px;height:6px;border-radius:50%;background:var(--gold);opacity:.3;animation:th 1.2s infinite}
.thinking i:nth-child(2){animation-delay:.15s}.thinking i:nth-child(3){animation-delay:.3s}
@keyframes th{50%{opacity:1}}
`;

const ACTIONS = [
  { id: 'assist', name: 'Ask Mirage', sub: 'Plan it and run it, with your approval where you set Ask', glyph: ic('spark'), tone: 'gold' },
  { id: 'overview', name: 'Show all windows', sub: 'Overview', glyph: ic('overview'), keys: 'windows overview mission control expose' },
  { id: 'ambient', name: 'Ambient screen', sub: 'Time, weather, your day and an insight', glyph: ic('moon'), keys: 'screensaver ambient clock' },
  { id: 'focus', name: 'Start focus for 50 minutes', sub: 'Mirage holds its notifications', glyph: ic('focus'), keys: 'focus dnd do not disturb deep work' },
  { id: 'lock', name: 'Lock screen', sub: 'Quick action', glyph: ic('lock'), keys: 'lock' },
  { id: 'display_off', name: 'Turn display off', sub: 'Quick action', glyph: ic('display'), keys: 'display off sleep screen monitor' },
  { id: 'screenshot', name: 'Take a screenshot', sub: 'Quick action', glyph: ic('shot'), keys: 'screenshot capture snip' },
  { id: 'dark', name: 'Switch light or dark mode', sub: 'System appearance', glyph: ic('sun'), keys: 'dark light mode theme appearance' },
  { id: 'organize', name: 'Organize Downloads', sub: 'Sort files into folders by type', glyph: ic('broom'), keys: 'tidy clean downloads organize sort' },
  { id: 'wallpaper', name: 'Next wallpaper', sub: 'Mirage collection', glyph: ic('image'), keys: 'wallpaper background' },
  { id: 'settings', name: 'Mirage settings', sub: 'AI, voice, permissions, look', glyph: ic('gear'), keys: 'settings preferences options api key' },
  { id: 'quit', name: 'Quit Mirage', sub: 'Your desktop goes back to normal', glyph: ic('power'), keys: 'quit exit close mirage' },
];

function score(text, q) {
  text = (text || '').toLowerCase();
  if (!q) return 0;
  if (text.startsWith(q)) return 100 - text.length / 100;
  const words = text.split(/[\s\-_.]+/);
  if (words.some((w) => w.startsWith(q))) return 80 - text.length / 100;
  if (text.includes(q)) return 60;
  let i = 0; for (const c of text) if (c === q[i]) i++;
  return i === q.length ? 30 : -1;
}
function hl(name, q) {
  const i = name.toLowerCase().indexOf(q);
  if (!q || i < 0) return esc(name);
  return esc(name.slice(0, i)) + '<b class="hl">' + esc(name.slice(i, i + q.length)) + '</b>' + esc(name.slice(i + q.length));
}
function calc(q) {
  const s = q.replace(/\s+/g, '').replace(/×/g, '*').replace(/÷/g, '/').replace(/,/g, '');
  if (!/^[\d.+\-*/()%^]+$/.test(s) || !/\d[+\-*/%^]/.test(s)) return null;
  try {
    // Small safe evaluator: numbers, + - * / % ^ and brackets only.
    let p = 0;
    const num = () => { const m = /^\d*\.?\d+/.exec(s.slice(p)); if (!m) throw 0; p += m[0].length; return parseFloat(m[0]); };
    const atom = () => { if (s[p] === '(') { p++; const v = expr(); if (s[p++] !== ')') throw 0; return v; } if (s[p] === '-') { p++; return -atom(); } return num(); };
    const pow = () => { let v = atom(); while (s[p] === '^') { p++; v = v ** atom(); } return v; };
    const term = () => { let v = pow(); while ('*/%'.includes(s[p]) && s[p]) { const o = s[p++]; const r = pow(); v = o === '*' ? v * r : o === '/' ? v / r : v % r; } return v; };
    const expr = () => { let v = term(); while ((s[p] === '+' || s[p] === '-')) { const o = s[p++]; const r = term(); v = o === '+' ? v + r : v - r; } return v; };
    const v = expr(); if (p !== s.length || !isFinite(v)) return null;
    return Math.round(v * 1e10) / 1e10;
  } catch { return null; }
}

export function mountPalette(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  root.innerHTML = `
  <div class="pal glass deep">
    <div class="in">
      <div class="orb live"></div>
      <input placeholder="Search, or ask Mirage" spellcheck="false" autocomplete="off">
      <div class="wave" hidden>${'<i></i>'.repeat(7)}</div>
      <button class="mic" title="Speak (${mod}+M)">${ic('mic')}</button>
      <span class="kbd">${mod} Space</span>
    </div>
    <div class="cols">
      <div class="res"></div>
      <div class="ai">
        <div class="who"><div class="orb" style="--s:18px"></div><b>Mirage</b><span class="chip prov"></span><span class="chip steps" style="margin-left:auto" hidden></span></div>
        <div class="log"></div>
        <div class="foot"></div>
      </div>
    </div>
  </div>`;
  const $ = (s) => root.querySelector(s);
  const pal = $('.pal'), input = $('input'), res = $('.res'), log = $('.log');
  let items = [], sel = 0, apps = [], settings = null, seq = 0, busy = false, stepCount = 0;

  async function init() {
    settings = await getSettings();
    apps = (await call('list_apps')) || [];
    paintFoot();
  }
  onSettings((s) => { settings = s; paintFoot(); });
  on('apps', (l) => { apps = l || []; });

  function paintFoot() {
    if (!settings) return;
    const prov = { anthropic: 'Claude', openai: 'OpenAI', gemini: 'Gemini', ollama: 'Ollama (local)', none: 'not set' }[settings.ai?.provider] || settings.ai?.provider;
    $('.prov').textContent = 'your AI: ' + prov;
    const p = settings.permissions || {};
    const pick = [['files_move', 'Files'], ['mail_compose', 'Mail'], ['delete', 'Delete'], ['run_command', 'Commands']];
    $('.foot').innerHTML = pick.map(([k, n]) => `<span class="chip ${p[k] === 'allow' ? 'on' : ''}">${n}: ${({ allow: 'Allow', ask: 'Ask', deny: 'Deny' })[p[k] || 'ask']}</span>`).join('') + `<button class="chip" data-x="perm" style="margin-left:auto">Permissions</button><button class="chip" data-x="new">New chat</button>`;
  }

  async function search() {
    const q = input.value.trim().toLowerCase();
    const my = ++seq;
    const out = [];
    if (!q) {
      out.push({ sec: 'Suggestions' });
      ACTIONS.slice(0, 6).forEach((a) => out.push({ kind: 'action', ...a }));
      return paint(out);
    }
    const c = calc(q);
    if (c != null) out.push({ sec: 'Calculator' }, { kind: 'calc', id: 'calc', name: String(c), sub: q + ' =', glyph: ic('calc'), value: c });
    const askFirst = q.split(/\s+/).length >= 3 || /[?]$/.test(q);
    const ask = { kind: 'action', ...ACTIONS[0], sub: `“${input.value.trim()}”` };
    const appHits = apps.map((a) => ({ a, s: score(a.name, q) })).filter((x) => x.s >= 0).sort((x, y) => y.s - x.s).slice(0, 6);
    const actHits = ACTIONS.slice(1).map((a) => ({ a, s: Math.max(score(a.name, q), score(a.keys, q) - 10) })).filter((x) => x.s >= 30).sort((x, y) => y.s - x.s).slice(0, 4);
    out.push({ sec: 'Top hits' });
    if (askFirst) out.push(ask);
    appHits.slice(0, askFirst ? 2 : 3).forEach((x) => out.push({ kind: 'app', ...x.a, sub: 'Application' }));
    if (!askFirst) out.push(ask);
    if (actHits.length) { out.push({ sec: 'Actions' }); actHits.forEach((x) => out.push({ kind: 'action', ...x.a })); }
    paint(out, q);
    const files = (await call('search_files', { q, limit: 8 })) || [];
    if (my !== seq) return;
    if (files.length) {
      out.push({ sec: 'Files' });
      files.forEach((f) => out.push({ kind: 'file', id: f.path, name: f.name, sub: f.dir, glyph: ic(f.is_dir ? 'folder' : 'file'), path: f.path }));
      if (appHits.length > 3) { out.push({ sec: 'More apps' }); appHits.slice(3).forEach((x) => out.push({ kind: 'app', ...x.a, sub: 'Application' })); }
      paint(out, q);
    }
  }

  function paint(list, q = '') {
    items = list.filter((x) => !x.sec);
    sel = Math.min(sel, Math.max(0, items.length - 1));
    let n = 0;
    res.innerHTML = list.map((x) => {
      if (x.sec) return `<div class="label">${esc(x.sec)}</div>`;
      const i = n++;
      const k = i === 0 ? '↵' : i < 9 ? (os === 'mac' ? '⌘' : 'Ctrl ') + (i + 1) : '';
      return `<div class="it ${i === sel ? 'sel' : ''}" data-i="${i}">${tile(x)}<div class="tx"><div>${hl(x.name, q)}</div><small>${esc(x.sub || '')}</small></div>${k ? `<span class="kbd k">${k}</span>` : ''}</div>`;
    }).join('') || `<div class="empty">Nothing found. Press Tab to ask Mirage.</div>`;
  }
  function move(d) {
    if (!items.length) return;
    sel = (sel + d + items.length) % items.length;
    res.querySelectorAll('.it').forEach((n) => n.classList.toggle('sel', +n.dataset.i === sel));
    res.querySelector('.it.sel')?.scrollIntoView({ block: 'nearest' });
  }

  async function run(x, alt) {
    if (!x) return;
    if (x.kind === 'app') { call('launch', { id: x.id }); return done(); }
    if (x.kind === 'file') { call(alt ? 'reveal_path' : 'open_path', { path: x.path }); return done(); }
    if (x.kind === 'calc') { navigator.clipboard?.writeText(String(x.value)); input.value = String(x.value); return search(); }
    switch (x.id) {
      case 'assist': return ask(input.value.trim());
      case 'overview': case 'ambient': case 'settings': call('show_view', { view: x.id }); return done();
      case 'quit': return call('quit');
      case 'organize': return ask('Organize my Downloads folder into subfolders by file type.');
      case 'wallpaper': call('next_wallpaper'); return done();
      default: call('quick_action', { action: x.id }); return done();
    }
  }
  function done() { input.value = ''; search(); hideSelf(); }

  // ---------- assistant ----------
  const steps = new Map();
  function setMode(ai) { pal.classList.toggle('ai', ai); }
  function addMe(text) { log.insertAdjacentHTML('beforeend', `<div class="me">${esc(text)}</div>`); scroll(); }
  function addThinking() { log.querySelector('.thinking')?.remove(); log.insertAdjacentHTML('beforeend', '<div class="thinking"><i></i><i></i><i></i></div>'); scroll(); }
  function scroll() { log.scrollTop = log.scrollHeight; }
  async function ask(text) {
    if (!text || busy) return;
    setMode(true); busy = true;
    addMe(text); addThinking();
    input.value = ''; search();
    try { await call('assistant_send', { text }); } catch (e) { onAssist({ type: 'error', text: String(e) }); }
  }
  function onAssist(ev) {
    setMode(true);
    const th = log.querySelector('.thinking');
    if (ev.type === 'message') {
      th?.remove();
      log.insertAdjacentHTML('beforeend', `<p>${esc(ev.text)}</p>`);
      if (busy) addThinking();
    } else if (ev.type === 'step') {
      let n = steps.get(ev.id);
      if (!n) {
        n = document.createElement('div'); n.className = 'step'; steps.set(ev.id, n);
        (th ? th.before(n) : log.appendChild(n));
        stepCount++; $('.steps').hidden = false; $('.steps').textContent = stepCount + (stepCount === 1 ? ' step' : ' steps');
      }
      const icon = { done: ic('check', '', 'color:var(--ok)'), allowed: ic('check', '', 'color:var(--ok)'), ask: ic('send', '', 'color:var(--gold)'), denied: ic('x', '', 'color:var(--bad)'), error: ic('x', '', 'color:var(--bad)'), running: ic('clock', '', 'color:var(--gold)') }[ev.status] || '';
      const label = { done: 'Done', allowed: 'Allowed', ask: 'Ask', denied: 'Denied', error: 'Failed', running: 'Running' }[ev.status];
      n.innerHTML = `${icon}<div class="sx">${esc(ev.title)}${ev.detail ? `<small>${esc(ev.detail)}</small>` : ''}</div>` +
        (ev.status === 'ask' ? `<div class="btns"><button class="btn" data-ans="0">Deny</button><button class="btn p" data-ans="1">${esc(ev.allow_label || 'Allow')}</button></div>` : `<span class="st ${ev.status}">${label}</span>`);
      n.dataset.id = ev.id;
    } else if (ev.type === 'done' || ev.type === 'error') {
      th?.remove(); busy = false;
      if (ev.type === 'error') log.insertAdjacentHTML('beforeend', `<p style="color:var(--bad)">${esc(ev.text)}</p>`);
    }
    scroll();
  }
  on('assistant', onAssist);
  log.addEventListener('click', (e) => {
    const b = e.target.closest('[data-ans]'); if (!b) return;
    const id = b.closest('.step').dataset.id;
    call('assistant_answer', { id, allow: b.dataset.ans === '1' });
  });
  $('.foot').addEventListener('click', (e) => {
    const x = e.target.closest('[data-x]')?.dataset.x;
    if (x === 'perm') call('show_view', { view: 'settings', section: 'permissions' });
    if (x === 'new') { call('assistant_reset'); log.innerHTML = ''; steps.clear(); stepCount = 0; $('.steps').hidden = true; busy = false; }
  });

  // ---------- voice ----------
  const wave = $('.wave'), mic = $('.mic'), orb = $('.in .orb');
  on('voice', (v) => {
    const listening = v.state === 'listening';
    wave.hidden = !listening; mic.classList.toggle('on', listening); orb.classList.toggle('listening', listening);
    if (listening) [...wave.children].forEach((i, k) => { i.style.height = Math.max(4, Math.min(22, (v.level || 0) * 60 * (0.5 + Math.abs(Math.sin(k * 1.7 + Date.now() / 120))))) + 'px'; });
    if (v.partial) input.value = v.partial;
    if (v.state === 'thinking') input.placeholder = 'Transcribing…';
    if (v.state === 'idle') input.placeholder = 'Search, or ask Mirage';
    if (v.final) { input.value = v.final; ask(v.final); }
    if (v.error) onAssist({ type: 'error', text: v.error });
  });
  mic.onclick = () => call('voice_listen');

  // ---------- keys ----------
  let t = 0;
  input.addEventListener('input', () => { clearTimeout(t); t = setTimeout(search, 50); });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
    else if (e.key === 'Enter') { e.preventDefault(); run(items[sel], e.altKey); }
    else if (e.key === 'Tab') { e.preventDefault(); ask(input.value.trim()); }
    else if (e.key === 'Escape') { e.preventDefault(); if (input.value) { input.value = ''; search(); } else hideSelf(); }
    else if ((e.ctrlKey || e.metaKey) && /^[1-9]$/.test(e.key)) { e.preventDefault(); run(items[+e.key - 1]); }
  });
  res.addEventListener('mousemove', (e) => { const n = e.target.closest('.it'); if (n && +n.dataset.i !== sel) { sel = +n.dataset.i; res.querySelectorAll('.it').forEach((m) => m.classList.toggle('sel', m === n)); } });
  res.addEventListener('click', (e) => { const n = e.target.closest('.it'); if (n) run(items[+n.dataset.i]); });

  on('palette', (p) => {
    setMode(p?.mode === 'assistant' || p?.mode === 'voice' || pal.classList.contains('ai') && busy);
    if (p?.mode === 'voice') call('voice_listen');
    setTimeout(() => input.focus(), 30);
    search();
  });
  if (inApp) window.addEventListener('focus', () => input.focus());
  init().then(search);
  return { input, search, ask, onAssist, setMode, paint, items: () => items };
}
