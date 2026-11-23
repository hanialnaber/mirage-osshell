// Voice: speech to text on this computer with whisper.cpp (through
// whisper-rs), microphone through cpal. The microphone is open only while
// Mirage is listening for a command, or while "Hey Mirage" is turned on; the
// wake word is found by a cheap energy check followed by the tiny whisper
// model on short bursts of speech. Nothing leaves the computer except the
// final text, which goes to the AI the user chose.
use crate::state::emit;
use serde_json::{json, Value};
use std::sync::{LazyLock, Mutex};
use std::time::Instant;

static LAST_FINAL: LazyLock<Mutex<Option<(String, Instant)>>> = LazyLock::new(|| Mutex::new(None));

/// True when `text` is what voice just heard, so the reply is read aloud.
pub fn take_spoken(text: &str) -> bool {
    let mut l = LAST_FINAL.lock().unwrap();
    let hit = l.as_ref().map(|(t, at)| t.trim() == text.trim() && at.elapsed().as_secs() < 60).unwrap_or(false);
    if hit {
        *l = None;
    }
    hit
}

#[allow(dead_code)]
fn state(s: &str) {
    emit("voice", json!({ "state": s }));
}
fn error(msg: &str) {
    emit("voice", json!({ "state": "idle", "error": msg }));
}

#[cfg(feature = "voice")]
pub use imp::*;
#[cfg(not(feature = "voice"))]
pub use stub::*;

#[cfg(not(feature = "voice"))]
mod stub {
    use super::*;
    pub fn start() {}
    pub fn listen() {
        error("Voice isn't included in this build of Mirage.");
    }
    pub fn wake_changed() {}
    pub fn download() {}
    pub fn status() -> Value {
        json!({ "model_ready": false, "downloading": false, "progress": 0, "model_size": "not available in this build", "voices": crate::platform::voices() })
    }
}

#[cfg(feature = "voice")]
mod imp {
    use super::*;
    use cpal::traits::{DeviceTrait, HostTrait, StreamTrait};
    use cpal::{FromSample, SizedSample};
    use std::path::PathBuf;
    use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
    use std::sync::mpsc;
    use std::sync::Arc;
    use std::time::Duration;
    use whisper_rs::{FullParams, SamplingStrategy, WhisperContext, WhisperContextParameters};

    const RATE: usize = 16_000;
    const FRAME: usize = 480; // 30 ms
    const MODELS: [(&str, u64); 2] = [("tiny.en", 77_704_715), ("base.en", 147_964_211)];

    enum Cmd {
        Listen,
        Wake,
    }

    static TX: LazyLock<Mutex<Option<mpsc::Sender<Cmd>>>> = LazyLock::new(|| Mutex::new(None));
    static DOWNLOADING: AtomicBool = AtomicBool::new(false);
    static PROGRESS: AtomicU32 = AtomicU32::new(0); // per mille

    fn model_path(name: &str) -> PathBuf {
        let d = crate::settings::data_dir().join("models");
        let _ = std::fs::create_dir_all(&d);
        d.join(format!("ggml-{name}.bin"))
    }
    fn ready(name: &str) -> bool {
        model_path(name).metadata().map(|m| m.len() > 10_000_000).unwrap_or(false)
    }

    pub fn status() -> Value {
        json!({
            "model_ready": ready("tiny.en") && ready("base.en"),
            "downloading": DOWNLOADING.load(Ordering::SeqCst),
            "progress": PROGRESS.load(Ordering::SeqCst) as f64 / 1000.0,
            "model_size": "about 225 MB",
            "voices": crate::platform::voices(),
        })
    }

    pub fn download() {
        if DOWNLOADING.swap(true, Ordering::SeqCst) {
            return;
        }
        std::thread::spawn(|| {
            let r = fetch_models();
            DOWNLOADING.store(false, Ordering::SeqCst);
            match r {
                Ok(_) => {
                    crate::state::toast("Voice is ready", "Press the mic or say “Hey Mirage” if you turned it on.");
                    wake_changed();
                }
                Err(e) => crate::state::toast("Speech model download stopped", &e),
            }
        });
    }

    fn fetch_models() -> Result<(), String> {
        use std::io::{Read, Write};
        let total: u64 = MODELS.iter().map(|m| m.1).sum();
        let mut done: u64 = MODELS.iter().filter(|m| ready(m.0)).map(|m| m.1).sum();
        let client = reqwest::blocking::Client::builder().timeout(None).user_agent("Mirage/2").build().map_err(|e| e.to_string())?;
        for (name, _) in MODELS {
            if ready(name) {
                continue;
            }
            let url = format!("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{name}.bin");
            let mut r = client.get(url).send().map_err(|e| e.to_string())?;
            if !r.status().is_success() {
                return Err(format!("The model server answered {}", r.status()));
            }
            let part = model_path(name).with_extension("part");
            let mut f = std::fs::File::create(&part).map_err(|e| e.to_string())?;
            let mut buf = vec![0u8; 1 << 16];
            loop {
                let n = r.read(&mut buf).map_err(|e| e.to_string())?;
                if n == 0 {
                    break;
                }
                f.write_all(&buf[..n]).map_err(|e| e.to_string())?;
                done += n as u64;
                PROGRESS.store(((done as f64 / total as f64) * 1000.0).min(999.0) as u32, Ordering::SeqCst);
            }
            drop(f);
            std::fs::rename(&part, model_path(name)).map_err(|e| e.to_string())?;
        }
        PROGRESS.store(1000, Ordering::SeqCst);
        Ok(())
    }

    fn send(c: Cmd) {
        if let Some(tx) = TX.lock().unwrap().as_ref() {
            let _ = tx.send(c);
        }
    }
    pub fn listen() {
        if !ready("tiny.en") && !ready("base.en") {
            error("Download the speech model in Settings › Voice first.");
            return;
        }
        send(Cmd::Listen);
    }
    /// Re-reads the wake word setting.
    pub fn wake_changed() {
        send(Cmd::Wake);
    }

    pub fn start() {
        let (tx, rx) = mpsc::channel();
        *TX.lock().unwrap() = Some(tx);
        whisper_rs::install_logging_hooks();
        std::thread::Builder::new().name("mirage-voice".into()).spawn(move || engine(rx)).ok();
    }

    // ---------- microphone ----------
    struct Mic {
        _stream: cpal::Stream,
        buf: Arc<Mutex<Vec<f32>>>,
    }

    fn open_mic() -> Result<Mic, String> {
        let host = cpal::default_host();
        let dev = host.default_input_device().ok_or("No microphone found")?;
        let cfg = dev.default_input_config().map_err(|e| e.to_string())?;
        let buf = Arc::new(Mutex::new(Vec::<f32>::with_capacity(RATE * 2)));
        let sc: cpal::StreamConfig = cfg.clone().into();
        let stream = match cfg.sample_format() {
            cpal::SampleFormat::F32 => build::<f32>(&dev, &sc, buf.clone()),
            cpal::SampleFormat::I16 => build::<i16>(&dev, &sc, buf.clone()),
            cpal::SampleFormat::U16 => build::<u16>(&dev, &sc, buf.clone()),
            cpal::SampleFormat::I32 => build::<i32>(&dev, &sc, buf.clone()),
            f => return Err(format!("Unsupported microphone format {f:?}")),
        }?;
        stream.play().map_err(|e| e.to_string())?;
        Ok(Mic { _stream: stream, buf })
    }

    fn build<T>(dev: &cpal::Device, sc: &cpal::StreamConfig, out: Arc<Mutex<Vec<f32>>>) -> Result<cpal::Stream, String>
    where
        T: SizedSample,
        f32: FromSample<T>,
    {
        let ch = sc.channels.max(1) as usize;
        let step = RATE as f64 / sc.sample_rate.0 as f64;
        // Box-filter resampler to 16 kHz mono.
        let (mut phase, mut sum, mut n) = (0.0f64, 0.0f32, 0u32);
        dev.build_input_stream(
            sc,
            move |data: &[T], _: &cpal::InputCallbackInfo| {
                let mut local = Vec::with_capacity(data.len() / ch / 2 + 1);
                for fr in data.chunks(ch) {
                    let m = fr.iter().map(|s| f32::from_sample(*s)).sum::<f32>() / ch as f32;
                    sum += m;
                    n += 1;
                    phase += step;
                    if phase >= 1.0 {
                        phase -= 1.0;
                        local.push(sum / n as f32);
                        sum = 0.0;
                        n = 0;
                    }
                }
                let mut o = out.lock().unwrap();
                o.extend_from_slice(&local);
                if o.len() > RATE * 30 {
                    let cut = o.len() - RATE * 30;
                    o.drain(..cut);
                }
            },
            |e| eprintln!("mirage mic: {e}"),
            None,
        )
        .map_err(|e| e.to_string())
    }

    // ---------- speech to text ----------
    struct Models {
        tiny: Option<WhisperContext>,
        base: Option<(WhisperContext, Instant)>,
    }

    fn load(name: &str) -> Option<WhisperContext> {
        if !ready(name) {
            return None;
        }
        WhisperContext::new_with_params(&model_path(name).display().to_string(), WhisperContextParameters::default()).ok()
    }

    fn transcribe(ctx: &WhisperContext, pcm: &[f32], prompt: &str) -> String {
        let Ok(mut st) = ctx.create_state() else { return String::new() };
        let mut p = FullParams::new(SamplingStrategy::Greedy { best_of: 1 });
        let threads = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4);
        p.set_n_threads((threads / 2).clamp(1, 4) as i32);
        p.set_language(Some("en"));
        p.set_no_context(true);
        p.set_single_segment(true);
        p.set_no_timestamps(true);
        p.set_print_progress(false);
        p.set_print_realtime(false);
        p.set_print_special(false);
        p.set_print_timestamps(false);
        p.set_suppress_blank(true);
        if !prompt.is_empty() {
            p.set_initial_prompt(prompt);
        }
        // whisper wants at least a second of audio; pad short bursts.
        let mut buf = pcm.to_vec();
        if buf.len() < RATE + RATE / 4 {
            buf.resize(RATE + RATE / 4, 0.0);
        }
        if st.full(p, &buf).is_err() {
            return String::new();
        }
        let n = st.full_n_segments().unwrap_or(0);
        let mut t = String::new();
        for i in 0..n {
            if let Ok(s) = st.full_get_segment_text(i) {
                t.push_str(&s);
            }
        }
        clean(&t)
    }

    fn clean(t: &str) -> String {
        // Drop whisper's non-speech tags like [BLANK_AUDIO] or (wind blowing).
        let mut out = String::new();
        let mut depth = 0;
        for c in t.chars() {
            match c {
                '[' | '(' => depth += 1,
                ']' | ')' => depth = (depth - 1).max(0),
                _ if depth == 0 => out.push(c),
                _ => {}
            }
        }
        out.split_whitespace().collect::<Vec<_>>().join(" ")
    }

    fn lev(a: &str, b: &str) -> usize {
        let a: Vec<char> = a.chars().collect();
        let b: Vec<char> = b.chars().collect();
        let mut prev: Vec<usize> = (0..=b.len()).collect();
        for i in 1..=a.len() {
            let mut cur = vec![i; b.len() + 1];
            for j in 1..=b.len() {
                cur[j] = (prev[j] + 1).min(cur[j - 1] + 1).min(prev[j - 1] + if a[i - 1] == b[j - 1] { 0 } else { 1 });
            }
            prev = cur;
        }
        prev[b.len()]
    }

    /// Finds "Hey Mirage" in a transcript; returns what was said after it.
    pub(super) fn wake_match(text: &str) -> Option<String> {
        let words: Vec<String> = text.to_lowercase().split_whitespace().map(|w| w.chars().filter(|c| c.is_alphanumeric()).collect::<String>()).filter(|w| !w.is_empty()).collect();
        let near = |w: &str| (5..=8).contains(&w.chars().count()) && lev(w, "mirage") <= 2;
        for i in 0..words.len() {
            let one = near(&words[i]);
            let two = i + 1 < words.len() && near(&format!("{}{}", words[i], words[i + 1]));
            if one || two {
                let rest = i + if two && !one { 2 } else { 1 };
                return Some(words[rest.min(words.len())..].join(" "));
            }
        }
        None
    }

    fn rms(f: &[f32]) -> f32 {
        (f.iter().map(|x| x * x).sum::<f32>() / f.len().max(1) as f32).sqrt()
    }

    fn finish(text: String) {
        if text.trim().is_empty() {
            error("I didn't catch that.");
            return;
        }
        *LAST_FINAL.lock().unwrap() = Some((text.clone(), Instant::now()));
        emit("voice", json!({ "state": "idle", "final": text }));
    }

    enum Mode {
        Wake,
        Listen { start: Instant, heard: bool, silent: usize, pcm: Vec<f32> },
    }

    fn engine(rx: mpsc::Receiver<Cmd>) {
        let mut models = Models { tiny: None, base: None };
        let mut mic: Option<Mic> = None;
        let mut mode = Mode::Wake;
        let mut floor = 0.004f32;
        let mut pending: Vec<f32> = vec![];
        let mut burst: Vec<f32> = vec![];
        let mut burst_silent = 0usize;
        let mut tick = 0u64;
        loop {
            let wake_on = crate::state::settings().voice.wake && ready("tiny.en");
            // Handle commands; block when there is nothing to listen for.
            let cmd = if mic.is_none() && !wake_on && matches!(mode, Mode::Wake) {
                models.tiny = None;
                models.base = None;
                match rx.recv() {
                    Ok(c) => Some(c),
                    Err(_) => return,
                }
            } else {
                rx.try_recv().ok()
            };
            if let Some(Cmd::Listen) = cmd {
                mode = Mode::Listen { start: Instant::now(), heard: false, silent: 0, pcm: vec![] };
                state("listening");
            }
            let need = wake_on || matches!(mode, Mode::Listen { .. });
            if !need {
                mic = None;
                continue;
            }
            if mic.is_none() {
                match open_mic() {
                    Ok(m) => mic = Some(m),
                    Err(e) => {
                        error(&format!("Couldn't open the microphone: {e}"));
                        mode = Mode::Wake;
                        if !wake_on {
                            continue;
                        }
                        std::thread::sleep(Duration::from_secs(5));
                        continue;
                    }
                }
            }
            std::thread::sleep(Duration::from_millis(30));
            tick += 1;
            if let Some(m) = &mic {
                pending.extend(m.buf.lock().unwrap().drain(..));
            }
            // Unload the larger model after two quiet minutes.
            if models.base.as_ref().map(|b| b.1.elapsed() > Duration::from_secs(120)).unwrap_or(false) {
                models.base = None;
            }
            while pending.len() >= FRAME {
                let fr: Vec<f32> = pending.drain(..FRAME).collect();
                let r = rms(&fr);
                let speech = r > (floor * 3.0).max(0.01);
                if !speech {
                    floor = floor * 0.98 + r * 0.02;
                }
                match &mut mode {
                    Mode::Listen { start, heard, silent, pcm } => {
                        pcm.extend_from_slice(&fr);
                        if speech {
                            *heard = true;
                            *silent = 0;
                        } else {
                            *silent += 1;
                        }
                        if tick % 2 == 0 {
                            emit("voice", json!({ "state": "listening", "level": (r * 8.0).min(1.0) }));
                        }
                        let secs = start.elapsed().as_secs_f32();
                        let end = (*heard && *silent >= 33) || secs > 15.0;
                        if !*heard && secs > 6.0 {
                            state("idle");
                            mode = Mode::Wake;
                            pending.clear();
                            break;
                        }
                        if end {
                            state("thinking");
                            let audio = std::mem::take(pcm);
                            if models.base.is_none() {
                                models.base = load("base.en").map(|c| (c, Instant::now()));
                            }
                            let text = match &mut models.base {
                                Some((c, at)) => {
                                    *at = Instant::now();
                                    transcribe(c, &audio, "")
                                }
                                None => {
                                    if models.tiny.is_none() {
                                        models.tiny = load("tiny.en");
                                    }
                                    models.tiny.as_ref().map(|c| transcribe(c, &audio, "")).unwrap_or_default()
                                }
                            };
                            finish(text);
                            mode = Mode::Wake;
                            pending.clear();
                            if let Some(m) = &mic {
                                m.buf.lock().unwrap().clear();
                            }
                            break;
                        }
                    }
                    Mode::Wake => {
                        if !wake_on {
                            continue;
                        }
                        if speech || !burst.is_empty() {
                            burst.extend_from_slice(&fr);
                            burst_silent = if speech { 0 } else { burst_silent + 1 };
                        }
                        let len = burst.len() as f32 / RATE as f32;
                        if !burst.is_empty() && (burst_silent >= 17 || len > 3.5) {
                            let b = std::mem::take(&mut burst);
                            burst_silent = 0;
                            if len < 0.35 {
                                continue;
                            }
                            if models.tiny.is_none() {
                                models.tiny = load("tiny.en");
                            }
                            let Some(t) = models.tiny.as_ref() else { continue };
                            let heard = transcribe(t, &b, "Hey Mirage.");
                            if let Some(rest) = wake_match(&heard) {
                                crate::views::show_async("assistant");
                                if rest.split_whitespace().count() >= 2 {
                                    finish(rest);
                                } else {
                                    mode = Mode::Listen { start: Instant::now(), heard: false, silent: 0, pcm: vec![] };
                                    state("listening");
                                }
                            }
                            pending.clear();
                            if let Some(m) = &mic {
                                m.buf.lock().unwrap().clear();
                            }
                            break;
                        }
                    }
                }
            }
        }
    }
}

#[cfg(all(test, feature = "voice"))]
mod tests {
    #[test]
    fn wake() {
        assert_eq!(super::imp::wake_match("Hey Mirage, open Spotify").as_deref(), Some("open spotify"));
        assert_eq!(super::imp::wake_match("hey my rage").as_deref(), Some(""));
        assert!(super::imp::wake_match("the weather is nice").is_none());
    }
}
