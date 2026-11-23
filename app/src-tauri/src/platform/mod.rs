// Platform layer. Each OS module exposes the same set of functions.
use base64::Engine;
use serde::Serialize;
use std::path::Path;

#[cfg(target_os = "macos")]
mod mac;
#[cfg(target_os = "macos")]
pub use mac::*;
#[cfg(windows)]
mod win;
#[cfg(windows)]
pub use win::*;
#[cfg(not(any(windows, target_os = "macos")))]
mod other;
#[cfg(not(any(windows, target_os = "macos")))]
pub use other::*;

#[derive(Serialize, Clone, Debug)]
pub struct AppEntry {
    pub id: String,
    pub name: String,
    pub path: String,
    pub target: Option<String>,
    pub icon: Option<String>,
}

#[derive(Serialize, Clone, Debug)]
pub struct WinInfo {
    pub id: String,
    pub app: String,
    pub title: String,
    pub path: Option<String>,
    pub icon: Option<String>,
    pub thumb: Option<String>,
}

#[derive(Serialize, Clone, Debug)]
pub struct FileHit {
    pub name: String,
    pub path: String,
    pub dir: String,
    pub is_dir: bool,
}
impl FileHit {
    pub fn from_path(p: &Path) -> FileHit {
        let home = dirs::home_dir().unwrap_or_default();
        let dir = p.parent().map(|d| {
            let s = d.display().to_string();
            let h = home.display().to_string();
            if !h.is_empty() && s.starts_with(&h) { format!("~{}", &s[h.len()..]) } else { s }
        });
        FileHit { name: p.file_name().unwrap_or_default().to_string_lossy().to_string(), path: p.display().to_string(), dir: dir.unwrap_or_default(), is_dir: p.is_dir() }
    }
}

pub fn jpeg_data_url(img: &image::DynamicImage, quality: u8) -> Option<String> {
    let mut buf = std::io::Cursor::new(vec![]);
    let enc = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut buf, quality);
    img.to_rgb8().write_with_encoder(enc).ok()?;
    Some(format!("data:image/jpeg;base64,{}", base64::engine::general_purpose::STANDARD.encode(buf.into_inner())))
}
pub fn png_data_url(bytes: &[u8]) -> String {
    format!("data:image/png;base64,{}", base64::engine::general_purpose::STANDARD.encode(bytes))
}

/// Decode any wallpaper the OS may hand us (JPEG, PNG, BMP; HEIC on macOS via sips).
pub fn load_image(p: &Path) -> Option<image::DynamicImage> {
    if let Ok(b) = std::fs::read(p) {
        if let Ok(i) = image::load_from_memory(&b) {
            return Some(i);
        }
    }
    #[cfg(target_os = "macos")]
    {
        let tmp = std::env::temp_dir().join("mirage-wallpaper.jpg");
        let ok = std::process::Command::new("sips").args(["-s", "format", "jpeg", "-Z", "2560"]).arg(p).arg("--out").arg(&tmp).output().map(|o| o.status.success()).unwrap_or(false);
        if ok {
            return image::open(&tmp).ok();
        }
    }
    None
}
