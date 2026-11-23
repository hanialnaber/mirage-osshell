// Control center: volume, quick toggles, quick actions and wallpapers.
import { ic } from './icons.js';
import { call, on, esc, getSettings, saveSettings, onSettings, hideSelf } from './core.js';

const CSS = `
.cc{position:absolute;inset:0;border-radius:22px;padding:16px;display:flex;flex-direction:column;gap:14px}
.cc .row{display:flex;align-items:center;gap:12px}
.cc input[type=range]{flex:1;-webkit-appearance:none;appearance:none;height:30px;background:none}
.cc input[type=range]::-webkit-slider-runnable-track{height:6px;border-radius:4px;background:linear-gradient(90deg,var(--gold) var(--v,50%),rgba(244,239,230,.1) var(--v,50%))}
.cc input[type=range]::-webkit-slider-thumb{-webkit-appearance:none;width:18px;height:18px;border-radius:50%;background:var(--ivory);margin-top:-6px;box-shadow:0 2px 8px rgba(0,0,0,.4)}
.cc .tg{display:grid;grid-template-columns:1fr 1fr;gap:10px}
.cc .t{display:flex;align-items:center;gap:10px;padding:12px;border-radius:14px;background:rgba(244,239,230,.05);border:1px solid var(--line);text-align:left;font-size:13px;font-weight:550}
.cc .t small{display:block;font-weight:400;color:var(--mute);font-size:11.5px;margin-top:2px}
.cc .t .dot{width:30px;height:30px;border-radius:50%;display:grid;place-items:center;background:rgba(244,239,230,.08);flex:none}
.cc .t.on .dot{background:var(--gold);color:#1A1306}
.cc .qa{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}
.cc .qa button{display:flex;flex-direction:column;align-items:center;gap:6px;padding:10px 4px;border-radius:12px;font-size:11px;color:var(--mute)}
.cc .qa button:hover{background:rgba(244,239,230,.06);color:var(--ivory)}
.cc .qa svg{width:20px;height:20px;color:var(--ivory)}
.cc .wps{display:grid;grid-template-columns:repeat(4,1fr);gap:8px}
.cc .wps button{aspect-ratio:16/10;border-radius:9px;background:center/cover;border:1px solid var(--line2)}
.cc .wps button.on{box-shadow:0 0 0 2px var(--gold)}
`;

export function mountControl(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  root.innerHTML = `<div class="cc glass deep">
    <div class="row"><button class="vb">${ic('vol')}</button><input type="range" min="0" max="100" class="vol"><span class="mono vp" style="font-size:12px;width:34px;text-align:right">–</span></div>
    <div class="tg">
      <button class="t" data-t="focus"><span class="dot">${ic('focus')}</span><span>Focus<small>Hold Mirage alerts</small></span></button>
      <button class="t" data-t="dark"><span class="dot">${ic('moon')}</span><span>Dark mode<small>System</small></span></button>
      <button class="t" data-t="wake"><span class="dot">${ic('mic')}</span><span>Hey Mirage<small>Wake word</small></span></button>
      <button class="t" data-t="tinted"><span class="dot">${ic('image')}</span><span>Tinted icons<small>Gold theme</small></span></button>
    </div>
    <div class="qa">
      <button data-q="lock">${ic('lock')}Lock</button>
      <button data-q="display_off">${ic('display')}Display off</button>
      <button data-q="screenshot">${ic('shot')}Screenshot</button>
      <button data-v="ambient">${ic('moon')}Ambient</button>
      <button data-v="overview">${ic('overview')}Windows</button>
      <button data-v="assistant">${ic('spark')}Assistant</button>
      <button data-v="palette">${ic('search')}Search</button>
      <button data-v="settings">${ic('gear')}Settings</button>
    </div>
    <div class="label">Wallpaper</div>
    <div class="wps"></div>
  </div>`;
  const $ = (s) => root.querySelector(s);
  let s = null, focus = false, dark = true;
  const vol = $('.vol');
  function paint() {
    root.querySelector('[data-t=focus]').classList.toggle('on', focus);
    root.querySelector('[data-t=dark]').classList.toggle('on', dark);
    root.querySelector('[data-t=wake]').classList.toggle('on', !!s?.voice?.wake);
    root.querySelector('[data-t=tinted]').classList.toggle('on', !!s?.tinted);
    root.querySelectorAll('.wps button').forEach((b) => b.classList.toggle('on', b.dataset.w === s?.wallpaper));
  }
  async function load() {
    s = await getSettings();
    const st = (await call('control_state')) || {};
    focus = !!st.focus; dark = st.dark !== false;
    if (st.volume != null) { vol.value = st.volume; vol.style.setProperty('--v', st.volume + '%'); $('.vp').textContent = Math.round(st.volume) + '%'; }
    const wps = (await call('list_wallpapers')) || [];
    $('.wps').innerHTML = wps.map((w) => `<button data-w="${esc(w.id)}" title="${esc(w.name)}" style="background-image:url(${w.thumb})"></button>`).join('');
    paint();
  }
  vol.addEventListener('input', () => {
    vol.style.setProperty('--v', vol.value + '%'); $('.vp').textContent = vol.value + '%';
    call('set_volume', { level: +vol.value });
  });
  $('.vb').onclick = () => call('quick_action', { action: 'mute' });
  root.addEventListener('click', async (e) => {
    const t = e.target.closest('[data-t]')?.dataset.t;
    const q = e.target.closest('[data-q]')?.dataset.q;
    const v = e.target.closest('[data-v]')?.dataset.v;
    const w = e.target.closest('[data-w]')?.dataset.w;
    if (t === 'focus') { focus = !focus; call('quick_action', { action: focus ? 'focus' : 'focus_stop' }); }
    if (t === 'dark') { dark = !dark; call('quick_action', { action: dark ? 'dark_on' : 'dark_off' }); }
    if (t === 'wake') s = await saveSettings({ voice: { wake: !s.voice.wake } });
    if (t === 'tinted') s = await saveSettings({ tinted: !s.tinted });
    if (q) { await hideSelf(); call('quick_action', { action: q }); }
    if (v) { await hideSelf(); call('show_view', { view: v }); }
    if (w) { s = await saveSettings({ wallpaper: w }); call('set_wallpaper', { id: w }); }
    paint();
  });
  on('focus', (f) => { focus = !!f?.on; paint(); });
  onSettings((n) => { s = n; paint(); });
  on('control', load);
  load();
  return {};
}
