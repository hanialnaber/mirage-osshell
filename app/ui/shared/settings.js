// Settings, and the short welcome flow on first run.
import { ic, logo } from './icons.js';
import { call, on, os, mod, esc, getSettings, saveSettings, inApp, hideSelf } from './core.js';

const CSS = `
.set{position:absolute;inset:0;display:grid;grid-template-columns:230px 1fr;border-radius:18px;overflow:hidden}
.set nav{padding:18px 12px;border-right:1px solid var(--line);display:flex;flex-direction:column;gap:2px}
.set nav .brand{display:flex;align-items:center;gap:10px;font-weight:650;padding:6px 10px 18px}
.set nav button{display:flex;align-items:center;gap:10px;padding:9px 10px;border-radius:9px;text-align:left;font-size:13.5px;color:var(--mute)}
.set nav button:hover{color:var(--ivory);background:rgba(244,239,230,.05)}
.set nav button.on{color:var(--gold);background:var(--gold-dim)}
.set main{overflow:auto;padding:28px 34px 40px;position:relative}
.set .x{position:absolute;top:14px;right:14px;width:32px;height:32px;border-radius:9px;display:grid;place-items:center;color:var(--mute);z-index:2}
.set .x:hover{background:rgba(244,239,230,.07);color:var(--ivory)}
.set h2{font-size:26px;font-weight:550;font-stretch:110%;margin-bottom:6px}
.set .lead{color:var(--mute);font-size:14px;line-height:1.55;margin-bottom:22px;max-width:640px}
.set .grp{border-radius:16px;border:1px solid var(--line);background:rgba(244,239,230,.025);margin-bottom:18px}
.set .r{display:flex;align-items:center;gap:16px;padding:14px 18px;border-top:1px solid var(--line)}
.set .r:first-child{border-top:0}
.set .r .t{flex:1;min-width:0}
.set .r .t div{font-size:14px;font-weight:550}
.set .r .t small{display:block;color:var(--mute);font-size:12.5px;margin-top:3px;line-height:1.45}
.sw{width:42px;height:24px;border-radius:14px;background:rgba(244,239,230,.14);position:relative;flex:none;transition:background .15s}
.sw::after{content:"";position:absolute;top:3px;left:3px;width:18px;height:18px;border-radius:50%;background:var(--ivory);transition:transform .15s var(--ease)}
.sw.on{background:var(--gold)}.sw.on::after{transform:translateX(18px);background:#1A1306}
.seg{display:flex;padding:3px;border-radius:10px;background:rgba(244,239,230,.05);border:1px solid var(--line);flex:none}
.seg button{padding:5px 11px;border-radius:7px;font-size:12.5px;color:var(--mute)}
.seg button.on{background:var(--gold-dim);color:var(--gold)}
.seg.deny button.on[data-v=deny]{background:rgba(240,138,122,.12);color:var(--bad)}
.set input[type=text],.set input[type=password],.set input[type=number],.set select{background:rgba(4,6,11,.5);border:1px solid var(--line2);border-radius:9px;padding:8px 11px;font-size:13.5px;outline:0;min-width:0;width:280px}
.set input:focus,.set select:focus{border-color:var(--gold-line)}
.set .wpg{display:grid;grid-template-columns:repeat(2,1fr);gap:14px;padding:14px}
.set .wpg button{border-radius:14px;overflow:hidden;text-align:left;border:1px solid var(--line);background:rgba(4,6,11,.4)}
.set .wpg button.on{box-shadow:0 0 0 2px var(--gold)}
.set .wpg .im{aspect-ratio:16/9;background:center/cover}
.set .wpg .cap{padding:10px 12px}
.set .wpg .cap b{font-size:14px}
.set .wpg .cap small{display:block;color:var(--mute);font-size:12px;margin-top:3px;line-height:1.45}
.set .note{font-size:12.5px;color:var(--mute);line-height:1.55;padding:12px 18px}
.set .ok{color:var(--ok)}.set .bad{color:var(--bad)}
.welcome{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;padding:40px;gap:18px;border-radius:18px}
.welcome .orb{--s:64px;margin-bottom:12px}
.welcome h1{font-size:40px;font-weight:500;font-stretch:112%}
.welcome p{color:var(--mute);font-size:15px;line-height:1.6;max-width:560px}
.welcome .wpg{grid-template-columns:repeat(3,1fr);width:100%;max-width:820px}
.welcome .acts{display:flex;gap:10px;margin-top:8px}
.welcome .dots{display:flex;gap:6px;margin-top:6px}.welcome .dots i{width:6px;height:6px;border-radius:50%;background:var(--line2)}.welcome .dots i.on{background:var(--gold)}
`;

const PERMS = [
  ['open_app', 'Open apps', 'allow'],
  ['search_files', 'Search your files', 'allow'],
  ['open_path', 'Open files and folders', 'allow'],
  ['read_folder', 'List what is in a folder', 'allow'],
  ['files_move', 'Move, rename and organize files', 'ask'],
  ['create_note', 'Write notes to Documents/Mirage Notes', 'allow'],
  ['system_info', 'Read machine stats', 'allow'],
  ['wallpaper', 'Change the wallpaper', 'allow'],
  ['volume', 'Change the volume', 'allow'],
  ['quick_actions', 'Lock, display off, appearance, focus', 'ask'],
  ['open_url', 'Open web pages and searches', 'allow'],
  ['mail_compose', 'Draft an email in your mail app', 'ask'],
  ['delete', 'Move files to the Trash', 'deny'],
  ['run_command', 'Run shell commands', 'deny'],
];
const SECTIONS = [
  ['general', 'General', 'gear'], ['look', 'Look', 'image'], ['assistant', 'Assistant', 'spark'], ['voice', 'Voice', 'mic'],
  ['permissions', 'Permissions', 'key'], ['widgets', 'Widgets', 'grid'], ['bridge', 'Agent bridge', 'link'], ['shortcuts', 'Shortcuts', 'bolt'], ['about', 'About', 'eye'],
];
const MODELS = { anthropic: 'claude-opus-5-5', openai: 'gpt-5-mini', gemini: 'gemini-2.5-flash', ollama: 'llama3.2' };

export function mountSettings(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  let s = null, sec = 'general', wps = [], keyState = {};

  const sw = (path, on) => `<button class="sw ${on ? 'on' : ''}" data-sw="${path}"></button>`;
  const row = (t, sub, ctl) => `<div class="r"><div class="t"><div>${t}</div>${sub ? `<small>${sub}</small>` : ''}</div>${ctl}</div>`;
  const get = (p) => p.split('.').reduce((o, k) => o?.[k], s);

  const views = {
    general: () => `<h2>General</h2><p class="lead">Mirage sits on top of ${os === 'mac' ? 'macOS' : 'Windows'}. It changes no system files, and quitting it puts your desktop back as it was.</p>
      <div class="grp">
        ${row('Start Mirage when I sign in', '', sw('autostart', s.autostart))}
        ${row('24-hour clock', '', sw('clock24', s.clock24 !== false))}
      </div>
      <div class="grp">
        ${row('Top bar', os === 'mac' ? 'macOS keeps its own menu bar. Mirage shows CPU in it instead.' : 'Time, live stats, control center and the assistant', sw('surfaces.bar', s.surfaces.bar))}
        ${row('Dock', os === 'mac' ? 'Off by default, since macOS has its own Dock' : 'Pinned and running apps, with the assistant in the middle', sw('surfaces.dock', s.surfaces.dock))}
        ${row('Desktop widgets', 'Clock, today, system, focus and the daily insight', sw('surfaces.widgets', s.surfaces.widgets))}
        ${row('Widgets side', '', `<div class="seg" data-seg="widgetsSide">${['left', 'right'].map((v) => `<button data-v="${v}" class="${s.widgetsSide === v ? 'on' : ''}">${v[0].toUpperCase() + v.slice(1)}</button>`).join('')}</div>`)}
        ${os === 'win' ? row('Keep windows clear of the bar and dock', 'Maximized windows stop at the bar and dock instead of going under them', sw('reserveSpace', s.reserveSpace)) : ''}
        ${os === 'win' ? row('Hide the Windows taskbar', 'Uses the taskbar’s own auto-hide. Turn this off to bring it back', sw('hideTaskbar', s.hideTaskbar)) : ''}
      </div>
      <div class="grp">
        ${row('Ambient screen', 'Shows after the machine has been idle', sw('surfaces.ambient', s.surfaces.ambient))}
        ${row('Idle minutes before it shows', '', `<input type="number" min="1" max="120" data-num="ambientMinutes" value="${s.ambientMinutes}" style="width:90px">`)}
      </div>`,
    look: () => `<h2>Look</h2><p class="lead">Original wallpapers made for Mirage, each about something real. Pick one, or keep your own: the bar, dock and widgets blur whatever wallpaper you have.</p>
      <div class="grp"><div class="wpg">${wps.map((w) => `<button data-w="${esc(w.id)}" class="${s.wallpaper === w.id ? 'on' : ''}"><div class="im" style="background-image:url(${w.thumb})"></div><div class="cap"><b>${esc(w.name)}</b><small>${esc(w.caption)}</small></div></button>`).join('')}</div>
      ${row('Keep my own wallpaper', 'Mirage won’t change it', `<button class="btn" data-a="ownwp">Use mine</button>`)}</div>
      <div class="grp">
        ${row('Tinted icons', 'Shows every app icon in gold and navy to match the theme. Off shows the original colors.', sw('tinted', s.tinted))}
        ${row('Glass', 'Live blur of what is behind floating panels. Solid uses a little less GPU.', `<div class="seg" data-seg="glass">${[['os', 'Live blur'], ['solid', 'Solid']].map(([v, n]) => `<button data-v="${v}" class="${s.glass === v ? 'on' : ''}">${n}</button>`).join('')}</div>`)}
      </div>`,
    assistant: () => `<h2>Assistant</h2><p class="lead">Mirage’s brain is the AI you choose. Bring your own key, or run a local model with Ollama so nothing leaves this computer. Keys are kept in ${os === 'mac' ? 'the macOS Keychain' : 'Windows Credential Manager'}, never in a file.</p>
      <div class="grp">
        ${row('AI provider', '', `<select data-sel="ai.provider">${[['anthropic', 'Claude (Anthropic)'], ['openai', 'OpenAI'], ['gemini', 'Gemini (Google)'], ['ollama', 'Ollama (local)']].map(([v, n]) => `<option value="${v}" ${s.ai.provider === v ? 'selected' : ''}>${n}</option>`).join('')}</select>`)}
        ${row('Model', 'Any model name your provider accepts', `<input type="text" data-txt="ai.model" value="${esc(s.ai.model)}">`)}
        ${s.ai.provider === 'ollama'
          ? row('Ollama address', 'Install Ollama, then run a model such as llama3.2', `<input type="text" data-txt="ai.ollamaUrl" value="${esc(s.ai.ollamaUrl)}">`)
          : row('API key', keyState[s.ai.provider] ? '<span class="ok">Saved in your keychain</span>' : 'Paste a key from your provider’s console', `<input type="password" data-key placeholder="${keyState[s.ai.provider] ? '••••••••••••' : 'Paste key'}"><button class="btn" data-a="savekey">Save</button>`)}
        ${row('Check the connection', '<span class="testres"></span>', `<button class="btn" data-a="test">Test</button>`)}
      </div>
      <div class="note">What the AI sees: your request, the steps it has taken, and the results of the tools it used (for example a list of file names). Mirage sends nothing else, and nothing at all until you ask it something.</div>`,
    voice: () => `<h2>Voice</h2><p class="lead">Press ${mod}+M, or the mic in the command bar, and speak. Speech is turned into text on this computer with whisper.cpp; only the text goes to your AI.</p>
      <div class="grp">
        ${row('Listen for “Hey Mirage”', 'The microphone stays on and listens on-device for the wake word. Off by default.', sw('voice.wake', s.voice.wake))}
        ${row('Speak replies', 'Reads short answers aloud when you asked by voice', sw('voice.speak', s.voice.speak))}
        ${row('Voice', 'Uses a voice installed on your system', `<select data-sel="voice.voiceName"><option value="">System default</option></select>`)}
        ${row('Speech model', '<span class="vmodel">Checking…</span>', `<button class="btn" data-a="dlmodel">Download</button>`)}
      </div>`,
    permissions: () => `<h2>Permissions</h2><p class="lead">What Mirage may do on its own. Allow runs it, Ask shows you the step first, Deny blocks it. These apply to the assistant and to any agent using the bridge.</p>
      <div class="grp">${PERMS.map(([k, n, d]) => row(n, '', `<div class="seg deny" data-perm="${k}">${['allow', 'ask', 'deny'].map((v) => `<button data-v="${v}" class="${(s.permissions[k] || d) === v ? 'on' : ''}">${v[0].toUpperCase() + v.slice(1)}</button>`).join('')}</div>`)).join('')}</div>`,
    widgets: () => `<h2>Widgets</h2><p class="lead">Weather comes from Open-Meteo, with no account. Your calendar comes from its private iCal link, which only Mirage reads.</p>
      <div class="grp">
        ${row('Weather location', s.location?.name ? `Now: ${esc(s.location.name)}` : 'Type a city', `<input type="text" data-loc placeholder="City" value=""><button class="btn" data-a="loc">Set</button>`)}
        ${row('Calendar iCal link', 'Google Calendar: Settings, your calendar, “Secret address in iCal format”. Outlook: Calendar settings, Shared calendars, Publish.', `<input type="password" data-txt="calendarIcs" placeholder="https://…ics" value="${esc(s.calendarIcs || '')}">`)}
        ${row('Daily insight from your AI', 'Once a day, one sentence from your focus time and calendar. Uses your AI provider.', sw('aiInsight', s.aiInsight))}
      </div>`,
    bridge: () => `<h2>Agent bridge</h2><p class="lead">Lets other AI agents on this computer use Mirage’s tools through a local web API, with the same permissions as above. It listens on this computer only, and every call needs the token.</p>
      <div class="grp">
        ${row('Turn on the bridge', '', sw('bridge.enabled', s.bridge.enabled))}
        ${row('Address', '', `<span class="mono" style="font-size:12.5px">http://127.0.0.1:${s.bridge.port}</span>`)}
        ${row('Token', 'Send as “Authorization: Bearer …”', `<button class="btn" data-a="copytoken">Copy token</button>`)}
      </div>
      <div class="note mono">GET /tools lists tools. POST /tools/&lt;name&gt; with a JSON body runs one.</div>`,
    shortcuts: () => `<h2>Shortcuts</h2><p class="lead">Work from anywhere, in any app.</p>
      <div class="grp">${[
        [`${mod} Space`, 'Command bar'], [`${mod} M`, 'Talk to Mirage'], [`${mod} ↑`, 'Show all windows'], [`${mod} Shift A`, 'Ambient screen'],
        ...(os === 'win' ? [['Ctrl Alt ← / →', 'Snap to left or right half'], ['Ctrl Alt ↑', 'Fill the screen'], ['Ctrl Alt C', 'Center'], ['Ctrl Alt U I J K', 'Snap to a quarter']]
          : [['⌃⌥ ← / →', 'Snap to left or right half'], ['⌃⌥ ↑', 'Fill the screen'], ['⌃⌥ C', 'Center']]),
      ].map(([k, n]) => row(n, '', `<span class="kbd">${k}</span>`)).join('')}</div>`,
    about: () => `<h2>About Mirage</h2><p class="lead">A desktop layer for ${os === 'mac' ? 'macOS' : 'Windows'} by Hani Al-Naber. Open source.</p>
      <div class="grp">
        ${row('Version', '', `<span class="mono ver">–</span>`)}
        ${row('Memory and CPU right now', '<span class="use">–</span>', '')}
        ${row('Source code', 'github.com/hanialnaber/mirage-launcher', `<button class="btn" data-a="repo">Open</button>`)}
      </div>
      <div class="note">Privacy: Mirage has no account and no analytics. The only network calls are to the AI you chose, Open-Meteo for weather, your calendar link, and a one-time download of the speech model if you turn voice on.</div>`,
  };

  function render() {
    root.innerHTML = `<div class="set glass deep">
      <nav class="drag">${'<div class="brand">' + logo + 'Mirage</div>'}${SECTIONS.map(([k, n, i]) => `<button data-sec="${k}" class="${k === sec ? 'on' : ''}" style="-webkit-app-region:no-drag">${ic(i)}${n}</button>`).join('')}</nav>
      <main class="scroll"><button class="x" data-a="close">${ic('x')}</button>${views[sec]()}</main></div>`;
    after();
  }
  async function after() {
    if (sec === 'voice') {
      const v = await call('voice_status');
      const m = root.querySelector('.vmodel');
      if (v && m) m.innerHTML = v.model_ready ? '<span class="ok">Ready on this computer</span>' : v.downloading ? `Downloading… ${Math.round(v.progress * 100)}%` : `Not downloaded yet (${v.model_size})`;
      const sel = root.querySelector('[data-sel="voice.voiceName"]');
      (v?.voices || []).forEach((n) => sel.insertAdjacentHTML('beforeend', `<option ${s.voice.voiceName === n ? 'selected' : ''}>${esc(n)}</option>`));
    }
    if (sec === 'about') {
      const a = await call('about');
      if (a) { root.querySelector('.ver').textContent = a.version; root.querySelector('.use').textContent = a.usage; }
    }
  }
  async function set(path, val) {
    const patch = {}; const ks = path.split('.'); let o = patch;
    ks.slice(0, -1).forEach((k) => { o = o[k] = {}; }); o[ks.at(-1)] = val;
    s = await saveSettings(patch);
  }

  root.addEventListener('click', async (e) => {
    const t = e.target;
    const secB = t.closest('[data-sec]'); if (secB) { sec = secB.dataset.sec; return render(); }
    const swB = t.closest('[data-sw]'); if (swB) { await set(swB.dataset.sw, !get(swB.dataset.sw)); return render(); }
    const seg = t.closest('[data-seg] button,[data-perm] button');
    if (seg) {
      const p = seg.parentElement;
      if (p.dataset.perm) await set('permissions.' + p.dataset.perm, seg.dataset.v); else await set(p.dataset.seg, seg.dataset.v);
      return render();
    }
    const w = t.closest('[data-w]'); if (w) { await set('wallpaper', w.dataset.w); call('set_wallpaper', { id: w.dataset.w }); return render(); }
    const a = t.closest('[data-a]')?.dataset.a;
    if (a === 'close') return hideSelf();
    if (a === 'ownwp') { await set('wallpaper', ''); return render(); }
    if (a === 'savekey') {
      const v = root.querySelector('[data-key]').value.trim(); if (!v) return;
      await call('set_api_key', { provider: s.ai.provider, key: v }); keyState[s.ai.provider] = true; return render();
    }
    if (a === 'test') {
      const r = root.querySelector('.testres'); r.textContent = 'Checking…';
      try { const m = await call('assistant_test'); r.innerHTML = `<span class="ok">${esc(m)}</span>`; } catch (err) { r.innerHTML = `<span class="bad">${esc(err)}</span>`; }
    }
    if (a === 'dlmodel') { call('voice_download'); setTimeout(after, 500); }
    if (a === 'loc') {
      const q = root.querySelector('[data-loc]').value.trim(); if (!q) return;
      try { const loc = await call('geocode', { q }); await set('location', loc); render(); } catch (err) { alert(err); }
    }
    if (a === 'copytoken') { const tok = await call('bridge_token'); navigator.clipboard?.writeText(tok); t.textContent = 'Copied'; }
    if (a === 'repo') call('open_url', { url: 'https://github.com/hanialnaber/mirage-launcher' });
  });
  root.addEventListener('change', async (e) => {
    const t = e.target;
    if (t.dataset.sel) {
      await set(t.dataset.sel, t.value);
      if (t.dataset.sel === 'ai.provider') await set('ai.model', MODELS[t.value]);
      render();
    }
    if (t.dataset.txt) await set(t.dataset.txt, t.value.trim());
    if (t.dataset.num) await set(t.dataset.num, Math.max(1, +t.value || 10));
  });

  // ---------- first run ----------
  let step = 0;
  function welcome() {
    const dots = `<div class="dots">${[0, 1, 2].map((i) => `<i class="${i === step ? 'on' : ''}"></i>`).join('')}</div>`;
    const body = [
      () => `<div class="orb live"></div><h1>Hello, I’m Mirage.</h1><p>I sit on top of ${os === 'mac' ? 'macOS' : 'Windows'} and add a command bar, an assistant you can talk to, desktop widgets and an ambient screen. Nothing in your system changes, and quitting me puts everything back.</p><div class="acts"><button class="btn p" data-wz="next">Get started</button></div>`,
      () => `<h1>Pick a wallpaper</h1><p>Each one is original and about something real. You can keep yours too.</p><div class="wpg">${wps.map((w) => `<button data-wzw="${esc(w.id)}" class="${s.wallpaper === w.id ? 'on' : ''}"><div class="im" style="background-image:url(${w.thumb})"></div><div class="cap"><b>${esc(w.name)}</b></div></button>`).join('')}</div><div class="acts"><button class="btn" data-wz="own">Keep mine</button><button class="btn p" data-wz="next">Continue</button></div>`,
      () => `<h1>Give me a brain</h1><p>Choose the AI I think with. Add a key now or later in Settings. Until then, search, widgets and everything else work without it.</p><div class="acts"><button class="btn" data-wz="ai">Set up AI</button><button class="btn p" data-wz="done">Start using Mirage</button></div><p style="font-size:13px">Press <span class="kbd">${mod} Space</span> anytime to search or ask.</p>`,
    ][step]();
    root.innerHTML = `<div class="welcome glass deep drag" style="-webkit-app-region:drag">${body}${dots}</div>`;
    root.querySelectorAll('button').forEach((b) => (b.style.webkitAppRegion = 'no-drag'));
  }
  root.addEventListener('click', async (e) => {
    const wz = e.target.closest('[data-wz]')?.dataset.wz;
    const wzw = e.target.closest('[data-wzw]')?.dataset.wzw;
    if (wzw) { await set('wallpaper', wzw); call('set_wallpaper', { id: wzw }); return welcome(); }
    if (!wz) return;
    if (wz === 'next') { step++; return welcome(); }
    if (wz === 'own') { await set('wallpaper', ''); step++; return welcome(); }
    if (wz === 'ai') { await set('onboarded', true); sec = 'assistant'; return render(); }
    if (wz === 'done') { await set('onboarded', true); hideSelf(); sec = 'general'; render(); }
  });

  async function load(p = {}) {
    s = await getSettings();
    wps = (await call('list_wallpapers')) || [];
    keyState = (await call('api_key_status')) || {};
    if (p.section) sec = p.section;
    if (!s.onboarded && !p.section) { step = 0; welcome(); } else render();
  }
  on('settings-open', load);
  load();
  return { load };
}
