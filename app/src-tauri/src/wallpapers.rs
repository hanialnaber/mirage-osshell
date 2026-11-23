// Mirage's original wallpapers (bundled as resources, listed in
// wallpapers.json) and the downscaled copies the glass layers blur.
use crate::platform;
use crate::state::{app, emit, ST};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::path::PathBuf;
use tauri::Manager;

#[derive(Serialize, Deserialize, Clone)]
pub struct Wp {
    pub id: String,
    pub name: String,
    pub caption: String,
    #[serde(default)]
    pub thumb: String,
}

fn res_dir() -> PathBuf {
    app().path().resource_dir().unwrap_or_default().join("wallpapers")
}

pub fn list() -> Vec<Wp> {
    let t = std::fs::read_to_string(res_dir().join("wallpapers.json")).unwrap_or_default();
    let mut v: Vec<Wp> = serde_json::from_str(&t).unwrap_or_default();
    for w in v.iter_mut() {
        w.thumb = format!("wallpapers/{}.jpg", w.id);
    }
    v
}

fn file_for(id: &str) -> Option<PathBuf> {
    let src = res_dir().join(format!("{id}.jpg"));
    if !src.exists() {
        return None;
    }
    // Copy out of the app bundle so the OS keeps it after updates.
    let d = crate::settings::data_dir().join("wallpapers");
    let _ = std::fs::create_dir_all(&d);
    let dst = d.join(format!("{id}.jpg"));
    let same = std::fs::metadata(&dst).map(|m| m.len()).ok() == std::fs::metadata(&src).map(|m| m.len()).ok();
    if !same {
        std::fs::copy(&src, &dst).ok()?;
    }
    Some(dst)
}

fn original_file() -> PathBuf {
    crate::settings::data_dir().join("original-wallpaper.txt")
}

pub fn set(id: &str) -> Result<(), String> {
    if id.is_empty() {
        return restore_original();
    }
    let f = file_for(id).ok_or("That wallpaper is missing from this install")?;
    if !original_file().exists() {
        if let Some(cur) = platform::current_wallpaper() {
            let _ = std::fs::write(original_file(), cur.display().to_string());
        }
    }
    platform::set_wallpaper(&f.display().to_string())?;
    {
        let mut s = ST.settings.lock().unwrap();
        s.wallpaper = id.to_string();
        crate::settings::save(&s);
        emit("settings", s.clone());
    }
    changed();
    Ok(())
}

pub fn restore_original() -> Result<(), String> {
    if let Ok(p) = std::fs::read_to_string(original_file()) {
        if std::path::Path::new(p.trim()).exists() {
            platform::set_wallpaper(p.trim())?;
        }
    }
    changed();
    Ok(())
}

pub fn next() -> Result<(), String> {
    let all = list();
    if all.is_empty() {
        return Err("No wallpapers bundled".into());
    }
    let cur = crate::state::settings().wallpaper;
    let i = all.iter().position(|w| w.id == cur).map(|i| (i + 1) % all.len()).unwrap_or(0);
    set(&all[i].id)
}

/// Clears the cached copies and tells every glass layer to redraw.
pub fn changed() {
    *ST.wallpaper.lock().unwrap() = None;
    std::thread::spawn(|| {
        std::thread::sleep(std::time::Duration::from_millis(400));
        emit("wallpaper", Value::Null);
    });
}

fn current_path() -> Option<PathBuf> {
    let id = crate::state::settings().wallpaper;
    if !id.is_empty() {
        if let Some(p) = file_for(&id) {
            return Some(p);
        }
    }
    platform::current_wallpaper()
}

/// The current wallpaper as a small JPEG (for blurred glass) or a large one
/// (ambient screen and overview), plus where the primary screen sits.
pub fn info(full: bool) -> Option<Value> {
    let p = current_path();
    let modified = p.as_ref().and_then(|p| std::fs::metadata(p).ok()).and_then(|m| m.modified().ok()).and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_secs()).unwrap_or(0);
    let key = p.as_ref().map(|p| p.display().to_string()).unwrap_or_default();
    let url = {
        let mut c = ST.wallpaper.lock().unwrap();
        let fresh = c.as_ref().map(|c| c.path == key && c.modified == modified).unwrap_or(false);
        if !fresh {
            let img = p.as_ref().and_then(|p| platform::load_image(p));
            let small = img.as_ref().and_then(|i| platform::jpeg_data_url(&i.thumbnail(720, 720), 72)).unwrap_or_default();
            *c = Some(crate::state::WpCache { path: key.clone(), modified, small, full: None });
            if full {
                c.as_mut().unwrap().full = img.as_ref().and_then(|i| platform::jpeg_data_url(&i.resize(2560, 1600, image::imageops::FilterType::Triangle), 84));
            }
        } else if full && c.as_ref().unwrap().full.is_none() {
            let img = p.as_ref().and_then(|p| platform::load_image(p));
            c.as_mut().unwrap().full = img.as_ref().and_then(|i| platform::jpeg_data_url(&i.resize(2560, 1600, image::imageops::FilterType::Triangle), 84));
        }
        let c = c.as_ref().unwrap();
        if full { c.full.clone().unwrap_or_else(|| c.small.clone()) } else { c.small.clone() }
    };
    if url.is_empty() {
        return None;
    }
    let (sx, sy, sw, sh) = app()
        .primary_monitor()
        .ok()
        .flatten()
        .map(|m| (m.position().x, m.position().y, m.size().width as i32, m.size().height as i32))
        .unwrap_or((0, 0, 1920, 1080));
    Some(json!({ "data_url": url, "screen_x": sx, "screen_y": sy, "screen_w": sw, "screen_h": sh }))
}
