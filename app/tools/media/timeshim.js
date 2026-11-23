// Injected into every frame of the demo stage before its scripts run, so the
// renderer can step time one video frame at a time: timers, requestAnimationFrame,
// Date, performance.now and CSS/Web animations all follow a virtual clock.
// Iframes share the top window's clock and timer queue.
(() => {
  const top = (() => { try { return window.top.__vclock ? window.top : window; } catch { return window; } })();
  if (top === window && !window.__vclock) {
    const RealDate = Date;
    const base = new RealDate('2026-11-24T07:41:00Z').getTime();
    const clock = { t: 0, base, timers: [], rafs: [], seq: 1, docs: new Set(), RealDate };
    clock.add = (fn, ms, args, every, win) => {
      const id = clock.seq++;
      clock.timers.push({ id, at: clock.t + Math.max(0, +ms || 0), fn, args, every: every ? Math.max(1, +ms || 1) : 0, win });
      return id;
    };
    clock.clear = (id) => { clock.timers = clock.timers.filter((x) => x.id !== id); };
    clock.seek = (docRoot) => {
      for (const doc of clock.docs) {
        if (!doc.defaultView) { clock.docs.delete(doc); continue; }
        for (const a of doc.getAnimations()) {
          if (a.__born == null) a.__born = clock.t;
          if (a.playState !== 'paused') a.pause();
          try { a.currentTime = clock.t - a.__born; } catch {}
        }
      }
    };
    // Runs every timer due up to `to` in order, then one animation frame.
    clock.advance = (to) => {
      for (let guard = 0; guard < 100000; guard++) {
        let next = null;
        for (const x of clock.timers) if (x.at <= to && (!next || x.at < next.at || (x.at === next.at && x.id < next.id))) next = x;
        if (!next) break;
        clock.t = Math.max(clock.t, next.at);
        if (next.every) next.at += next.every; else clock.timers = clock.timers.filter((x) => x !== next);
        try { typeof next.fn === 'function' ? next.fn(...(next.args || [])) : next.win.eval(next.fn); } catch (e) { console.error(e); }
      }
      clock.t = to;
      const rafs = clock.rafs; clock.rafs = [];
      for (const r of rafs) { try { r.fn(clock.t); } catch (e) { console.error(e); } }
      clock.seek();
    };
    window.__vclock = clock;
  }
  const clock = top.__vclock;
  clock.docs.add(document);
  const RealDate = clock.RealDate;
  class VDate extends RealDate {
    constructor(...a) { if (a.length) super(...a); else super(clock.base + clock.t); }
    static now() { return clock.base + clock.t; }
  }
  window.Date = VDate;
  performance.now = () => clock.t;
  window.setTimeout = (fn, ms, ...args) => clock.add(fn, ms, args, false, window);
  window.setInterval = (fn, ms, ...args) => clock.add(fn, ms, args, true, window);
  window.clearTimeout = window.clearInterval = (id) => clock.clear(id);
  window.requestAnimationFrame = (fn) => { const id = clock.seq++; clock.rafs.push({ id, fn }); return id; };
  window.cancelAnimationFrame = (id) => { clock.rafs = clock.rafs.filter((r) => r.id !== id); };
})();
