// Desktop widgets: clock, today's events, weather, system, focus and the
// daily insight. Sits on the desktop layer, below app windows.
import { ic } from './icons.js';
import { call, on, now, every, hhmm, esc, bytes, getSettings, onSettings, DAYS, MONTHS } from './core.js';

const CSS = `
.wg{position:absolute;inset:0;padding:22px 24px;display:flex;flex-direction:column;gap:16px}
.wg .clock{font-size:112px;font-weight:300;letter-spacing:-.02em;line-height:.9;font-stretch:112%;font-variant-numeric:tabular-nums}
.wg .clock span{color:var(--gold)}
.wg .date{margin-top:12px;font-size:18px;color:var(--mute)}
.wg .date b{color:var(--ivory);font-weight:550}
.wg .row2{display:grid;grid-template-columns:1fr 1fr;gap:16px}
.wg .big{font-size:42px;font-weight:300;line-height:1;font-variant-numeric:tabular-nums}
.wg .big small{font-size:20px;color:var(--mute)}
.wg .insight{font-size:15px;line-height:1.5}
.wg .insight em{font-style:normal;color:var(--gold)}
.wg .ring{width:64px;height:64px;flex:none}
.wg .ring circle{transition:stroke-dasharray .8s var(--ease)}
.wg .sys{display:flex;align-items:center;gap:14px;margin-top:12px}
.wg .sys .mono{font-size:12px;color:var(--mute);line-height:1.75}
.wg .sys b{color:var(--ivory);font-weight:500}
.wg .fbtn{margin-top:10px;font-family:var(--mono);font-size:11px;letter-spacing:.1em;text-transform:uppercase;color:var(--gold);padding:5px 9px;border:1px solid var(--gold-line);border-radius:7px}
.wg .fbtn:hover{background:var(--gold-dim)}
.wg .hint{font-size:13px;color:var(--mute);line-height:1.5}
.wg .hint button{color:var(--gold);text-decoration:underline;text-underline-offset:3px}
.wg .card{transition:opacity .4s}
`;

export function mountWidgets(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  root.innerHTML = `
  <div class="wg">
    <div class="top"><div class="clock"><span class="h"></span><span>:</span><span class="m" style="color:inherit"></span></div><div class="date"></div></div>
    <div class="card glass today">
      <h4><span class="label">Today</span><span class="label gold count"></span></h4>
      <div class="evs"></div>
    </div>
    <div class="row2">
      <div class="card glass">
        <div class="label">System</div>
        <div class="sys">
          <svg class="ring" viewBox="0 0 64 64"><circle cx="32" cy="32" r="26" stroke="rgba(244,239,230,.08)" stroke-width="6" fill="none"/><circle class="rm" cx="32" cy="32" r="26" stroke="#F2C46B" stroke-width="6" fill="none" stroke-linecap="round" stroke-dasharray="0 164" transform="rotate(-90 32 32)"/><circle cx="32" cy="32" r="17" stroke="rgba(244,239,230,.08)" stroke-width="5" fill="none"/><circle class="rc" cx="32" cy="32" r="17" stroke="#7FA8F2" stroke-width="5" fill="none" stroke-linecap="round" stroke-dasharray="0 107" transform="rotate(-90 32 32)"/></svg>
          <div class="mono">MEM <b class="vm">–</b><br>CPU <b class="vc">–</b><br>Mirage <b class="gold vmi">–</b></div>
        </div>
      </div>
      <div class="card glass focus">
        <div class="label">Focus</div>
        <div class="big" style="margin-top:12px"><span class="fh">0</span><small>h</small> <span class="fm">00</span><small>m</small></div>
        <button class="fbtn">Start 50 min</button>
      </div>
    </div>
    <div class="card glass ins">
      <h4><span class="label">Daily insight</span><span class="chip src">wallpaper</span></h4>
      <div class="insight"></div>
    </div>
  </div>`;
  const $ = (s) => root.querySelector(s);
  let h24 = true, place = '';

  every(1000, () => {
    const d = now();
    const [h, m] = hhmm(d, h24);
    $('.h').textContent = h; $('.m').textContent = m;
    $('.date').innerHTML = `<b>${DAYS[d.getDay()]}</b> · ${d.getDate()} ${MONTHS[d.getMonth()]}${place}`;
    paintEvents();
    paintFocus();
  });

  let events = [], weather = null, focus = { on: false, today_minutes: 0 };
  function paintEvents() {
    const t = now();
    const upcoming = events.filter((e) => new Date(e.end || e.start) > t).slice(0, 3);
    $('.count').textContent = events.length ? `${events.length} event${events.length > 1 ? 's' : ''}` : '';
    if (!events.length) {
      $('.evs').innerHTML = `<div class="hint">${calHint ? `No events left today.` : `Add your calendar's iCal link in <button data-a="cal">Settings</button> to see your day here.`}</div>`;
      return;
    }
    const colors = ['var(--gold)', 'var(--blue)', 'var(--mute)'];
    $('.evs').innerHTML = (upcoming.length ? upcoming : events.slice(-1)).map((e, i) => {
      const s = new Date(e.start);
      const [h, m] = hhmm(s, h24);
      const mins = Math.round((s - t) / 60000);
      const when = e.all_day ? 'All day' : mins > 0 && mins < 120 ? `in ${mins} min` : '';
      const sub = [e.location, when].filter(Boolean).join(' · ');
      return `<div class="ev"><div class="t">${e.all_day ? 'all' : h + ':' + m}</div><div class="b" style="background:${colors[i % 3]}"></div><div><div class="n">${esc(e.title)}</div>${sub ? `<div class="s">${esc(sub)}</div>` : ''}</div></div>`;
    }).join('');
  }
  let calHint = false;
  function paintFocus() {
    let mins = focus.today_minutes || 0;
    if (focus.on && focus.started_at) mins += Math.max(0, (now() - new Date(focus.started_at)) / 60000);
    $('.fh').textContent = Math.floor(mins / 60);
    $('.fm').textContent = String(Math.floor(mins % 60)).padStart(2, '0');
    if (focus.on && focus.ends_at) {
      const left = Math.max(0, Math.round((new Date(focus.ends_at) - now()) / 60000));
      $('.fbtn').textContent = `Stop · ${left} min left`;
    } else $('.fbtn').textContent = 'Start 50 min';
  }

  on('stats', (s) => {
    $('.vm').textContent = Math.round(s.mem) + '%';
    $('.vc').textContent = Math.round(s.cpu) + '%';
    $('.vmi').textContent = s.mirage_mem ? bytes(s.mirage_mem) : '–';
    $('.rm').setAttribute('stroke-dasharray', `${(s.mem / 100) * 163.4} 164`);
    $('.rc').setAttribute('stroke-dasharray', `${(Math.min(100, s.cpu) / 100) * 106.8} 107`);
  });
  on('calendar', (c) => { const d = now().toDateString(); events = (c?.events || []).filter((e) => new Date(e.start).toDateString() === d); calHint = !!c?.configured; paintEvents(); });
  on('weather', (w) => { weather = w; place = w ? ` · ${esc(w.place)} ${Math.round(w.temp)}° ${esc(w.desc).toLowerCase()}` : ''; });
  on('focus', (f) => { focus = f || focus; paintFocus(); });
  on('insight', (i) => {
    if (!i) return;
    $('.insight').innerHTML = i.html || esc(i.text);
    $('.src').textContent = i.source || 'insight';
  });
  root.addEventListener('click', (e) => {
    if (e.target.closest('.fbtn')) call('quick_action', { action: focus.on ? 'focus_stop' : 'focus' });
    if (e.target.closest('[data-a=cal]')) call('show_view', { view: 'settings', section: 'widgets' });
  });

  getSettings().then((s) => { h24 = s.clock24 !== false; });
  onSettings((s) => { h24 = s.clock24 !== false; });
  call('widgets_data').then((d) => {
    if (!d) return;
    if (d.calendar) { const day = now().toDateString(); events = (d.calendar.events || []).filter((e) => new Date(e.start).toDateString() === day); calHint = !!d.calendar.configured; }
    if (d.weather) { weather = d.weather; place = ` · ${esc(d.weather.place)} ${Math.round(d.weather.temp)}° ${esc(d.weather.desc).toLowerCase()}`; }
    if (d.focus) focus = d.focus;
    if (d.insight) { $('.insight').innerHTML = d.insight.html || esc(d.insight.text); $('.src').textContent = d.insight.source || 'insight'; }
    paintEvents(); paintFocus();
  });
  return {};
}
