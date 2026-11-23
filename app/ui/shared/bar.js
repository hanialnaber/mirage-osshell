// Top bar: brand, active app, date and time, live machine stats, control
// center and the assistant orb.
import { ic, logo } from './icons.js';
import { call, on, now, every, hhmm, rate, esc, getSettings, onSettings } from './core.js';

const CSS = `
.bar{position:absolute;inset:0;display:flex;align-items:center;padding:0 12px 0 14px;gap:16px;font-size:13.5px;border-radius:0;border-width:0 0 1px 0;
  background:linear-gradient(180deg,rgba(4,6,11,.70),rgba(4,6,11,.46))}
.bar .brand{display:flex;align-items:center;gap:10px;font-weight:650;letter-spacing:.02em;padding:4px 8px;margin-left:-8px;border-radius:8px}
.bar .brand:hover,.bar .hit:hover{background:rgba(244,239,230,.07)}
.bar .app{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;max-width:34vw}
.bar .app small{color:var(--mute);font-weight:400;margin-left:10px}
.bar .center{position:absolute;left:50%;transform:translateX(-50%);display:flex;gap:10px;align-items:center;font-weight:600;padding:4px 10px;border-radius:8px}
.bar .center .d{color:var(--mute);font-weight:400}
.bar .center .c{color:var(--gold);opacity:var(--blink,1)}
.bar .right{margin-left:auto;display:flex;align-items:center;gap:14px}
.bar .stat{display:flex;align-items:center;gap:7px;font-family:var(--mono);font-size:11.5px;color:var(--mute);white-space:nowrap}
.bar .stat b{color:var(--ivory);font-weight:500;min-width:2.6ch;text-align:right}
.bar .pill{display:flex;align-items:center;gap:7px;padding:4px 9px;border-radius:8px;background:rgba(244,239,230,.05);border:1px solid var(--line);font-family:var(--mono);font-size:11.5px}
.bar button.hit{display:grid;place-items:center;width:28px;height:28px;border-radius:8px;color:var(--ivory)}
.bar .center,.bar .brand{white-space:nowrap;cursor:pointer}
.bar .focus{color:var(--gold);border-color:var(--gold-line);background:var(--gold-dim)}
.bar button.orbhit{width:34px;height:30px}
@media (max-width:1100px){.bar .gpu,.bar .net{display:none}}
@media (max-width:900px){.bar .app small{display:none}}
`;

export function mountBar(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  root.innerHTML = `
  <div class="bar glass">
    <div class="brand hit" data-a="palette">${logo}<span>Mirage</span></div>
    <div class="app"><span class="an"></span><small class="at"></small></div>
    <div class="center hit" data-a="ambient"><span class="d"></span><span class="tm"><span class="h"></span><span class="c">:</span><span class="m"></span></span></div>
    <div class="right">
      <div class="stat cpu">CPU <b>–</b><div class="spark"></div></div>
      <div class="stat mem">MEM <b>–</b></div>
      <div class="stat gpu">GPU <b>–</b></div>
      <div class="stat net">${ic('down', '', 'width:13px;height:13px')}<b>–</b></div>
      <div class="pill"><span class="fo" hidden>${ic('focus', '', 'width:14px;height:14px')}</span><span class="vo">${ic('vol', '', 'width:15px;height:15px')}</span><span class="vp"></span><span class="bt" hidden>${ic('batt', '', 'width:18px;height:15px')}</span><span class="bp"></span></div>
      <button class="hit" data-a="control" title="Control center">${ic('cc')}</button>
      <button class="hit orbhit" data-a="assistant" title="Mirage assistant"><div class="orb live"></div></button>
    </div>
  </div>`;
  const $ = (s) => root.querySelector(s);
  const spark = $('.spark');
  const hist = Array(14).fill(0);
  spark.innerHTML = hist.map(() => '<i style="height:2px"></i>').join('');
  let h24 = true;
  getSettings().then((s) => { h24 = s.clock24 !== false; });
  onSettings((s) => { h24 = s.clock24 !== false; });

  every(1000, () => {
    const d = now();
    const [h, m] = hhmm(d, h24);
    $('.h').textContent = h; $('.m').textContent = m;
    $('.d').textContent = d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
  });

  on('stats', (s) => {
    $('.cpu b').textContent = Math.round(s.cpu) + '%';
    $('.mem b').textContent = Math.round(s.mem) + '%';
    $('.gpu').hidden = s.gpu == null;
    if (s.gpu != null) $('.gpu b').textContent = Math.round(s.gpu) + '%';
    $('.net b').textContent = rate(s.down);
    hist.push(s.cpu); hist.shift();
    [...spark.children].forEach((i, k) => { i.style.height = Math.max(2, Math.min(14, hist[k] / 100 * 14 * 1.6 + 2)) + 'px'; });
    $('.vp').textContent = s.volume == null ? '' : Math.round(s.volume) + '%';
    $('.vo').innerHTML = ic(s.muted ? 'mute' : 'vol', '', 'width:15px;height:15px');
    const b = s.battery;
    $('.bt').hidden = !b; $('.bp').textContent = b ? Math.round(b.pct) + '%' + (b.charging ? '+' : '') : '';
  });
  on('foreground', (f) => {
    $('.an').textContent = f?.app || 'Desktop';
    $('.at').textContent = f?.title && f.title !== f.app ? f.title : '';
  });
  on('focus', (f) => { $('.fo').hidden = !f?.on; $('.pill').classList.toggle('focus', !!f?.on); });

  root.addEventListener('click', (e) => {
    const a = e.target.closest('[data-a]')?.dataset.a;
    if (a) call('toggle_view', { view: a });
  });
  return { root };
}
