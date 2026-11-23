// Renders Mirage's promo videos and screenshots from the demo stage.
//
//   node render.mjs --os win|mac --out DIR [--chrome PATH] [--mem MB --cpu PCT] [--fps 30]
//
// Serves the app folder over http, opens ui/demo/stage.html in Chrome
// (through playwright-core), steps a virtual clock one frame at a time and
// pipes each frame into ffmpeg. Also saves PNG screenshots at set moments.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const here = path.dirname(fileURLToPath(import.meta.url));
const appDir = path.resolve(here, '../..');
const arg = (k, d) => { const i = process.argv.indexOf('--' + k); return i > 0 ? process.argv[i + 1] : d; };
const OS = arg('os', 'win');
const OUT = path.resolve(arg('out', 'media-out'));
const FPS = +arg('fps', 30);
const CHROME = arg('chrome', process.env.CHROME_PATH || '/usr/bin/google-chrome');
const MAX_S = +arg('max', 90);
fs.mkdirSync(OUT, { recursive: true });

const TYPES = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.jpg': 'image/jpeg', '.png': 'image/png', '.svg': 'image/svg+xml', '.ttf': 'font/ttf', '.woff2': 'font/woff2' };
const server = http.createServer((req, res) => {
  const p = path.normalize(decodeURIComponent(new URL(req.url, 'http://x').pathname)).replace(/^(\.\.[/\\])+/, '');
  const f = path.join(appDir, p);
  if (!f.startsWith(appDir) || !fs.existsSync(f) || fs.statSync(f).isDirectory()) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': TYPES[path.extname(f)] || 'application/octet-stream' });
  fs.createReadStream(f).pipe(res);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const port = server.address().port;

const browser = await chromium.launch({ executablePath: CHROME, args: ['--hide-scrollbars', '--force-color-profile=srgb', '--disable-gpu-vsync'] });
const ctx = await browser.newContext({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1, locale: 'en-US', timezoneId: 'Asia/Amman' });
await ctx.addInitScript({ path: path.join(here, 'timeshim.js') });
const page = await ctx.newPage();
page.on('console', (m) => { if (m.type() === 'error') console.log('[page]', m.text()); });
page.on('pageerror', (e) => console.log('[page error]', e.message));

const qs = new URLSearchParams({ os: OS });
if (arg('mem')) qs.set('mem', arg('mem'));
if (arg('cpu')) qs.set('cpu', arg('cpu'));
await page.goto(`http://127.0.0.1:${port}/ui/demo/stage.html?${qs}`, { waitUntil: 'load' });

// Let the views boot: advance virtual time in small steps until the stage is ready.
const advance = (ms) => page.evaluate((to) => window.__vclock.advance(to), ms);
let t = 0;
for (let i = 0; i < 400; i++) {
  t += 50; await advance(t);
  if (await page.evaluate(() => window.__stage?.ready)) break;
  await page.waitForTimeout(20);
}
// Fonts and wallpapers load in real time.
await page.evaluate(() => document.fonts.ready);
await page.waitForTimeout(1500);
for (let i = 0; i < 20; i++) { t += 50; await advance(t); await page.waitForTimeout(30); }

await page.evaluate(() => { window.__stage.end = () => { window.__stage.finished = true; }; window.__stage.play(); });

const ff = spawn('ffmpeg', ['-y', '-loglevel', 'error', '-f', 'image2pipe', '-framerate', String(FPS), '-c:v', 'mjpeg', '-i', '-',
  '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', path.join(OUT, `mirage-${OS}.mp4`)], { stdio: ['pipe', 'inherit', 'inherit'] });
const cdp = await ctx.newCDPSession(page);

const start = t;
const frames = MAX_S * FPS;
let tail = -1;
for (let f = 0; f < frames; f++) {
  await advance(start + (f * 1000) / FPS);
  // Images (wallpaper swaps) decode in real time; give the compositor a moment.
  const { data } = await cdp.send('Page.captureScreenshot', { format: 'jpeg', quality: 93, optimizeForSpeed: false });
  const buf = Buffer.from(data, 'base64');
  if (!ff.stdin.write(buf)) await new Promise((r) => ff.stdin.once('drain', r));
  // The script marks moments worth a still for the site.
  const st = await page.evaluate(() => { const m = window.__stage.mark; window.__stage.mark = null; return { m, fin: !!window.__stage.finished }; });
  if (st.m) {
    await page.evaluate(() => document.documentElement.classList.add('still'));
    const png = await cdp.send('Page.captureScreenshot', { format: 'png' });
    await page.evaluate(() => document.documentElement.classList.remove('still'));
    fs.writeFileSync(path.join(OUT, `${OS}-${st.m}.png`), Buffer.from(png.data, 'base64'));
  }
  if (tail < 0 && st.fin) tail = f + Math.round(FPS * 0.4);
  if (tail >= 0 && f >= tail) break;
  if (f % (FPS * 5) === 0) console.log(`${OS}: ${(f / FPS).toFixed(0)}s`);
}
ff.stdin.end();
await new Promise((r) => ff.on('close', r));
// A light poster frame and a shorter web version for the page.
await new Promise((r) => spawn('ffmpeg', ['-y', '-loglevel', 'error', '-i', path.join(OUT, `mirage-${OS}.mp4`), '-vf', 'scale=1280:-2', '-c:v', 'libx264', '-preset', 'slow', '-crf', '24', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', '-an', path.join(OUT, `mirage-${OS}-720p.mp4`)], { stdio: 'inherit' }).on('close', r));
console.log('done', OS);
await browser.close();
server.close();
