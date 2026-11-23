// File search for the command bar and the assistant. macOS asks Spotlight;
// elsewhere Mirage keeps a small in-memory index of the usual home folders,
// built in the background and refreshed every 20 minutes.
use crate::platform::{self, FileHit};
use std::path::PathBuf;
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant};

struct Index {
    built: Option<Instant>,
    building: bool,
    items: Vec<(String, PathBuf)>, // lowercased name, path
}

static INDEX: LazyLock<Mutex<Index>> = LazyLock::new(|| Mutex::new(Index { built: None, building: false, items: vec![] }));

const SKIP: &[&str] = &["node_modules", "target", "AppData", "Library", "__pycache__", "venv", ".venv", "site-packages", "$RECYCLE.BIN"];

fn roots() -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = [dirs::desktop_dir(), dirs::document_dir(), dirs::download_dir(), dirs::picture_dir(), dirs::audio_dir(), dirs::video_dir()].into_iter().flatten().collect();
    if let Some(h) = dirs::home_dir() {
        for extra in ["OneDrive", "Projects", "Code", "source", "Dropbox"] {
            let p = h.join(extra);
            if p.is_dir() {
                v.push(p);
            }
        }
    }
    v.sort();
    v.dedup();
    v
}

fn build() {
    let mut items = vec![];
    for r in roots() {
        let walker = walkdir::WalkDir::new(&r).max_depth(7).into_iter().filter_entry(|e| {
            let n = e.file_name().to_string_lossy();
            !(n.starts_with('.') || SKIP.contains(&n.as_ref()))
        });
        for e in walker.flatten() {
            if e.depth() == 0 {
                continue;
            }
            items.push((e.file_name().to_string_lossy().to_lowercase(), e.into_path()));
            if items.len() >= 200_000 {
                break;
            }
        }
    }
    let mut ix = INDEX.lock().unwrap();
    ix.items = items;
    ix.built = Some(Instant::now());
    ix.building = false;
}

/// Starts (or refreshes) the background index when it is missing or stale.
pub fn warm() {
    if cfg!(target_os = "macos") {
        return;
    }
    let mut ix = INDEX.lock().unwrap();
    let stale = ix.built.map(|t| t.elapsed() > Duration::from_secs(20 * 60)).unwrap_or(true);
    if stale && !ix.building {
        ix.building = true;
        std::thread::Builder::new().name("mirage-index".into()).spawn(build).ok();
    }
}

pub fn search(q: &str, limit: usize) -> Vec<FileHit> {
    let q = q.trim();
    if q.len() < 2 {
        return vec![];
    }
    if let Some(v) = platform::search_files_native(q, limit) {
        return v;
    }
    warm();
    let ql = q.to_lowercase();
    let words: Vec<&str> = ql.split_whitespace().collect();
    let ix = INDEX.lock().unwrap();
    let mut scored: Vec<(i32, &PathBuf)> = ix
        .items
        .iter()
        .filter(|(n, _)| words.iter().all(|w| n.contains(w)))
        .map(|(n, p)| {
            let mut s = 0;
            if *n == ql {
                s += 100;
            }
            if n.starts_with(&ql) {
                s += 50;
            }
            if n.split(|c: char| !c.is_alphanumeric()).any(|part| part.starts_with(words[0])) {
                s += 20;
            }
            s -= (p.components().count() as i32).min(20);
            s -= (n.len() as i32 / 8).min(10);
            (s, p)
        })
        .collect();
    scored.sort_by(|a, b| b.0.cmp(&a.0));
    scored.into_iter().take(limit).map(|(_, p)| FileHit::from_path(p)).collect()
}
