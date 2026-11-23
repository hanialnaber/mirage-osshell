// Mirage notifications: a short card in the corner, queued, held during Focus.
import { ic } from './icons.js';
import { call, on, esc } from './core.js';

const CSS = `
.toast{position:absolute;inset:0;border-radius:18px;display:flex;gap:14px;align-items:flex-start;padding:16px 18px;opacity:0;transform:translateY(-8px);transition:opacity .25s var(--ease),transform .25s var(--ease);cursor:pointer}
.toast.show{opacity:1;transform:none}
.toast .orb{--s:22px;margin-top:2px}
.toast b{display:block;font-size:14px;font-weight:600}
.toast p{font-size:13px;color:var(--mute);line-height:1.45;margin-top:3px;display:-webkit-box;-webkit-line-clamp:3;-webkit-box-orient:vertical;overflow:hidden}
`;
export function mountToast(root) {
  const st = document.createElement('style'); st.textContent = CSS; document.head.appendChild(st);
  root.innerHTML = `<div class="toast glass deep"><div class="orb"></div><div><b></b><p></p></div></div>`;
  const t = root.querySelector('.toast');
  let timer = 0, action = null;
  on('toast', (n) => {
    t.querySelector('b').textContent = n.title || 'Mirage';
    t.querySelector('p').textContent = n.body || '';
    action = n.action || null;
    requestAnimationFrame(() => t.classList.add('show'));
    clearTimeout(timer);
    timer = setTimeout(() => { t.classList.remove('show'); setTimeout(() => call('hide_view', { view: 'toast' }), 300); }, n.ms || 5000);
  });
  t.onclick = () => { if (action) call('show_view', { view: action }); t.classList.remove('show'); call('hide_view', { view: 'toast' }); };
}
