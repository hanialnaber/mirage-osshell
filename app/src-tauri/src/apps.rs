// Installed apps, their icons (cached on disk at 96 px) and the default dock.
use crate::platform::{self, AppEntry, WinInfo};
use crate::state::{emit, ST};
use std::collections::HashMap;
use std::hash::{Hash, Hasher};
use std::sync::{LazyLock, Mutex};

static ICONS: LazyLock<Mutex<HashMap<String, Option<String>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

fn icon_dir() -> std::path::PathBuf {
    let d = crate::settings::data_dir().join("icons");
    let _ = std::fs::create_dir_all(&d);
    d
}

/// Icon for a file or app bundle as a PNG data URL, or None for a letter tile.
pub fn icon(path: &str) -> Option<String> {
    if path.is_empty() {
        return None;
    }
    if let Some(v) = ICONS.lock().unwrap().get(path) {
        return v.clone();
    }
    let mut h = std::collections::hash_map::DefaultHasher::new();
    path.to_lowercase().hash(&mut h);
    let f = icon_dir().join(format!("{:016x}.png", h.finish()));
    let bytes = std::fs::read(&f).ok().or_else(|| {
        let raw = platform::icon_png(path)?;
        let img = image::load_from_memory(&raw).ok()?;
        let img = if img.width() > 96 { img.resize(96, 96, image::imageops::FilterType::Lanczos3) } else { img };
        let mut out = std::io::Cursor::new(vec![]);
        img.write_to(&mut out, image::ImageFormat::Png).ok()?;
        let b = out.into_inner();
        let _ = std::fs::write(&f, &b);
        Some(b)
    });
    let url = bytes.map(|b| platform::png_data_url(&b));
    ICONS.lock().unwrap().insert(path.to_string(), url.clone());
    url
}

/// Cached icon only, without rendering a missing one.
fn cached_icon(path: &str) -> Option<String> {
    if let Some(v) = ICONS.lock().unwrap().get(path) {
        return v.clone();
    }
    let mut h = std::collections::hash_map::DefaultHasher::new();
    path.to_lowercase().hash(&mut h);
    let f = icon_dir().join(format!("{:016x}.png", h.finish()));
    std::fs::read(&f).ok().map(|b| platform::png_data_url(&b))
}

fn icon_key(a: &AppEntry) -> String {
    a.target.as_deref().filter(|t| !t.is_empty()).unwrap_or(&a.path).to_string()
}

/// Lists installed apps. The list goes out at once with the icons already
/// cached, then again once any missing icons are drawn, so the dock and
/// command bar never wait on icon rendering.
pub fn refresh() {
    let mut apps = platform::list_apps();
    let mut missing = false;
    for a in apps.iter_mut() {
        a.icon = cached_icon(&icon_key(a));
        missing |= a.icon.is_none();
    }
    publish(&apps);
    if missing {
        for a in apps.iter_mut() {
            if a.icon.is_none() {
                a.icon = icon(&icon_key(a));
            }
        }
        publish(&apps);
    }
}

fn publish(apps: &[AppEntry]) {
    let first = ST.apps.read().unwrap().is_empty();
    *ST.apps.write().unwrap() = apps.to_vec();
    if first {
        init_pinned(apps);
    }
    emit("apps", apps.to_vec());
}

/// Installed apps. Before the first scan finishes this is a quick list
/// without icons, so callers never wait on the scan.
pub fn list() -> Vec<AppEntry> {
    let have = !ST.apps.read().unwrap().is_empty();
    if !have {
        return platform::list_apps();
    }
    ST.apps.read().unwrap().clone()
}

pub fn with_icons(mut wins: Vec<WinInfo>) -> Vec<WinInfo> {
    for w in wins.iter_mut() {
        if w.icon.is_none() {
            if let Some(p) = &w.path {
                w.icon = icon(p);
            }
        }
    }
    wins
}

pub fn find(id: &str) -> Option<AppEntry> {
    ST.apps.read().unwrap().iter().find(|a| a.id == id).cloned()
}

/// Fuzzy app lookup for the assistant ("open spotify").
pub fn find_by_name(q: &str) -> Option<AppEntry> {
    let q = q.trim().to_lowercase();
    let apps = list();
    apps.iter().find(|a| a.name.to_lowercase() == q).or_else(|| apps.iter().find(|a| a.name.to_lowercase().starts_with(&q))).or_else(|| apps.iter().find(|a| a.name.to_lowercase().contains(&q))).cloned()
}

pub fn launch(id: &str) -> Result<(), String> {
    let a = find(id).or_else(|| list().into_iter().find(|a| a.id == id)).ok_or("That app is no longer installed")?;
    platform::launch(&a.path)?;
    emit("launched", id);
    Ok(())
}

/// First run: pin the apps most people reach for, when they are installed.
fn init_pinned(apps: &[AppEntry]) {
    let mut s = ST.settings.lock().unwrap();
    if s.pinned_init || apps.is_empty() {
        return;
    }
    let wants: &[&str] = if cfg!(target_os = "macos") {
        &["Finder", "Safari", "Google Chrome", "Mail", "Calendar", "Notes", "Music", "Visual Studio Code", "System Settings"]
    } else {
        &["File Explorer", "Microsoft Edge", "Google Chrome", "Outlook", "Mail", "Calendar", "Visual Studio Code", "Spotify", "Settings"]
    };
    let mut pins = vec![];
    for w in wants {
        if let Some(a) = apps.iter().find(|a| a.name.eq_ignore_ascii_case(w)) {
            if pins.len() < 8 {
                pins.push(a.id.clone());
            }
        }
    }
    s.pinned = pins;
    s.pinned_init = true;
    crate::settings::save(&s);
    let snap = s.clone();
    drop(s);
    emit("settings", snap);
}
