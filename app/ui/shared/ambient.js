// Ambient screen: Mirage's info screensaver. Comes up after the machine has
// been idle (10 minutes by default) and goes away on any key or mouse move.
import { call, on, now, every, hhmm, esc, bytes, DAYS, MONTHS, getSettings, inApp } from './core.js';
import { logo } from './icons.js';

const CSS = `
.amb{position:absolute;inset:0;overflow:hidden;background:#04060B}
.amb .wp{position:absolute;inset:-3%;background:var(--wpf) center/cover no-repeat;transform:scale(var(--z,1)) translate(var(--px,0),var(--py,0));transition:transform 8s linear}
.amb .shade{position:absolute;inset:0;background:linear-gradient(180deg,rgba(4,6,11,.30),rgba(4,6,11,0) 38%,rgba(4,6,11,.62))}
.amb .content{position:absolute;inset:0;transform:translate(var(--sx,0),var(--sy,0));transition:transform 2s var(--ease)}
.amb .tl{position:absolute;right:5vw;top:6vh;display:flex;align-items:center;gap:12px}
.amb .time{position:absolute;left:5vw;top:10vh;font-size:min(23vw,40vh);font-weight:200;letter-spacing:-.03em;line-height:.85;font-stretch:115%;font-variant-numeric:tabular-nums}
.amb .time span.c{color:var(--gold)}
.amb .day{position:absolute;left:5.4vw;top:calc(10vh + min(20vw,35vh));font-size:min(1.4vw,2.6vh);color:var(--mute)}
.amb .day b{color:var(--ivory);font-weight:500}
.amb .panels{position:absolute;left:5vw;right:4vw;bottom:7vh;display:flex;gap:24px;align-items:flex-end}
.amb .panel{border-radius:24px;padding:22px 24px;min-width:0}
.amb .wx{display:flex;gap:18px;margin-top:14px}
.amb .wx div{text-align:center;font-family:var(--mono);font-size:12px;color:var(--mute)}
.amb .wx b{display:block;font-family:var(--sans);font-size:18px;color:var(--ivory);font-weight:500;margin-top:6px}
.amb .kv{display:flex;justify-content:space-between;font-family:var(--mono);font-size:12px;color:var(--mute);margin-top:14px;gap:20px}
.amb .kv b{color:var(--ivory);font-weight:500}
.amb .meter{margin-top:8px}
.amb .quote{font-size:min(1.5vw,28px);line-height:1.35;font-weight:350;font-stretch:104%}
.amb .quote em{font-style:normal;color:var(--gold)}
.amb .big{font-size:56px;font-weight:300;line-height:1}
`;

export function mountAmbient(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  document.documentElement.classList.add('css-blur');
  root.innerHTML = `
  <div class="amb">
    <div class="wp"></div><div class="shade"></div>
    <div class="content">
      <div class="tl">${logo}<span class="label">Mirage Ambient</span></div>
      <div class="time"><span class="h"></span><span class="c">:</span><span class="m"></span></div>
      <div class="day"></div>
      <div class="panels">
        <div class="panel glass wxp" style="width:420px"><div class="label wpl">Weather</div><div style="display:flex;align-items:baseline;gap:12px;margin-top:10px"><span class="big wt">–</span><span class="muted wd"></span></div><div class="wx"></div></div>
        <div class="panel glass" style="width:380px"><div class="label evl">Next up</div><div class="evs" style="margin-top:8px"></div></div>
        <div class="panel glass" style="width:300px"><div class="label">This machine</div>
          <div class="kv">CPU<b class="ac">–</b></div><div class="meter"><i class="mc" style="width:0"></i></div>
          <div class="kv">Memory<b class="am">–</b></div><div class="meter"><i class="mm" style="width:0"></i></div>
          <div class="kv">Mirage<b class="ami">–</b></div></div>
        <div class="panel glass" style="flex:1;max-width:560px;margin-left:auto"><div class="label" style="display:flex;justify-content:space-between"><span class="il">Insight</span><span class="gold is"></span></div><div class="quote" style="margin-top:12px"></div></div>
      </div>
    </div>
  </div>`;
  const $ = (s) => root.querySelector(s);
  let h24 = true;
  getSettings().then((s) => { h24 = s.clock24 !== false; });

  every(1000, () => {
    const d = now();
    const [h, m] = hhmm(d, h24);
    $('.h').textContent = h; $('.m').textContent = m;
    $('.day').innerHTML = `<b>${DAYS[d.getDay()]}</b>, ${d.getDate()} ${MONTHS[d.getMonth()]}`;
    // drift a few pixels every minute so nothing sits still on the panel
    if (d.getSeconds() === 0 && inApp) {
      const r = root.querySelector('.amb').style;
      r.setProperty('--sx', (Math.random() * 16 - 8).toFixed(0) + 'px');
      r.setProperty('--sy', (Math.random() * 12 - 6).toFixed(0) + 'px');
      r.setProperty('--z', (1 + Math.random() * 0.04).toFixed(3));
    }
  });

  function weather(w) {
    if (!w) { $('.wxp').hidden = true; return; }
    $('.wxp').hidden = false;
    $('.wpl').textContent = w.place + ' · now';
    $('.wt').textContent = Math.round(w.temp) + '°';
    $('.wd').textContent = w.desc + (w.wind != null ? ` · wind ${Math.round(w.wind)} km/h` : '');
    $('.wx').innerHTML = (w.hourly || []).slice(0, 6).map((x) => { const [h, m] = hhmm(new Date(x.t), h24); return `<div>${h}:${m}<b>${Math.round(x.temp)}°</b></div>`; }).join('');
  }
  function events(c) {
    const t = now();
    const list = (c?.events || []).filter((e) => new Date(e.end || e.start) > t).slice(0, 2);
    $('.evl').textContent = list.length && new Date(list[0].start).getDate() !== t.getDate() ? 'Tomorrow' : 'Next up';
    $('.evs').innerHTML = list.length ? list.map((e, i) => { const [h, m] = hhmm(new Date(e.start), h24); return `<div class="ev"><div class="t">${e.all_day ? 'all' : h + ':' + m}</div><div class="b" style="background:${i ? 'var(--blue)' : 'var(--gold)'}"></div><div><div class="n">${esc(e.title)}</div>${e.location ? `<div class="s">${esc(e.location)}</div>` : ''}</div></div>`; }).join('') : '<div class="muted" style="font-size:14px;padding-top:6px">Nothing on your calendar.</div>';
  }
  on('weather', weather);
  on('calendar', events);
  on('insight', (i) => { if (i) { $('.quote').innerHTML = i.html || esc(i.text); $('.is').textContent = i.source || ''; } });
  on('stats', (s) => {
    $('.ac').textContent = Math.round(s.cpu) + '%'; $('.mc').style.width = Math.max(2, s.cpu) + '%';
    $('.am').textContent = `${bytes(s.mem_used)} / ${bytes(s.mem_total)}`; $('.mm').style.width = s.mem + '%';
    $('.ami').textContent = s.mirage_mem ? `${bytes(s.mirage_mem)} · ${s.mirage_cpu.toFixed(1)}% CPU` : '–';
  });
  async function load() {
    const d = await call('widgets_data');
    if (d) { weather(d.weather); events(d.calendar); if (d.insight) { $('.quote').innerHTML = d.insight.html || esc(d.insight.text); $('.is').textContent = d.insight.source || ''; } }
    const wp = await call('wallpaper_info', { full: true });
    if (wp) root.querySelector('.amb').style.setProperty('--wpf', `url(${wp.data_url})`);
  }
  on('ambient', load);
  load();

  if (inApp) {
    let start = null;
    const bye = () => call('hide_view', { view: 'ambient' });
    window.addEventListener('keydown', bye);
    window.addEventListener('mousedown', bye);
    window.addEventListener('mousemove', (e) => {
      if (!start) { start = [e.screenX, e.screenY]; return; }
      if (Math.hypot(e.screenX - start[0], e.screenY - start[1]) > 10) { start = null; bye(); }
    });
  }
  return {};
}
