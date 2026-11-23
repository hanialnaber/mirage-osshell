// User settings, stored as JSON in the user's config folder.
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Surfaces {
    pub bar: bool,
    pub dock: bool,
    pub widgets: bool,
    pub ambient: bool,
}
impl Default for Surfaces {
    fn default() -> Self {
        let mac = cfg!(target_os = "macos");
        Surfaces { bar: !mac, dock: !mac, widgets: true, ambient: true }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Ai {
    pub provider: String,
    pub model: String,
    pub ollama_url: String,
}
impl Default for Ai {
    fn default() -> Self {
        Ai { provider: "anthropic".into(), model: "claude-opus-5-5".into(), ollama_url: "http://localhost:11434".into() }
    }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Voice {
    pub wake: bool,
    pub speak: bool,
    pub voice_name: String,
}
impl Default for Voice {
    fn default() -> Self { Voice { wake: false, speak: true, voice_name: String::new() } }
}

#[derive(Serialize, Deserialize, Clone, Debug, Default)]
#[serde(default, rename_all = "camelCase")]
pub struct Location {
    pub name: String,
    pub lat: f64,
    pub lon: f64,
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Bridge {
    pub enabled: bool,
    pub port: u16,
}
impl Default for Bridge {
    fn default() -> Self { Bridge { enabled: false, port: 47811 } }
}

#[derive(Serialize, Deserialize, Clone, Debug)]
#[serde(default, rename_all = "camelCase")]
pub struct Settings {
    pub onboarded: bool,
    pub autostart: bool,
    pub clock24: bool,
    pub tinted: bool,
    pub glass: String,
    pub surfaces: Surfaces,
    pub widgets_side: String,
    pub reserve_space: bool,
    pub hide_taskbar: bool,
    pub ambient_minutes: u32,
    pub wallpaper: String,
    pub ai: Ai,
    pub voice: Voice,
    pub permissions: BTreeMap<String, String>,
    pub pinned: Vec<String>,
    pub pinned_init: bool,
    pub location: Option<Location>,
    pub calendar_ics: String,
    pub ai_insight: bool,
    pub bridge: Bridge,
}

impl Default for Settings {
    fn default() -> Self {
        let mac = cfg!(target_os = "macos");
        Settings {
            onboarded: false,
            autostart: true,
            clock24: true,
            tinted: true,
            glass: "os".into(),
            surfaces: Surfaces::default(),
            widgets_side: if mac { "left".into() } else { "right".into() },
            reserve_space: true,
            hide_taskbar: false,
            ambient_minutes: 10,
            wallpaper: String::new(),
            ai: Ai::default(),
            voice: Voice::default(),
            permissions: default_permissions(),
            pinned: vec![],
            pinned_init: false,
            location: None,
            calendar_ics: String::new(),
            ai_insight: false,
            bridge: Bridge::default(),
        }
    }
}

pub fn default_permissions() -> BTreeMap<String, String> {
    [
        ("open_app", "allow"), ("search_files", "allow"), ("open_path", "allow"), ("read_folder", "allow"),
        ("files_move", "ask"), ("create_note", "allow"), ("system_info", "allow"), ("wallpaper", "allow"),
        ("volume", "allow"), ("quick_actions", "ask"), ("open_url", "allow"), ("mail_compose", "ask"),
        ("delete", "deny"), ("run_command", "deny"),
    ]
    .iter()
    .map(|(k, v)| (k.to_string(), v.to_string()))
    .collect()
}

pub fn config_dir() -> PathBuf {
    let d = dirs::config_dir().unwrap_or_else(std::env::temp_dir).join("Mirage");
    let _ = std::fs::create_dir_all(&d);
    d
}
pub fn data_dir() -> PathBuf {
    let d = dirs::data_local_dir().unwrap_or_else(std::env::temp_dir).join("Mirage");
    let _ = std::fs::create_dir_all(&d);
    d
}

pub fn load() -> Settings {
    let p = config_dir().join("settings.json");
    let mut s: Settings = std::fs::read_to_string(&p).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default();
    // Fill in permissions added in newer versions.
    for (k, v) in default_permissions() {
        s.permissions.entry(k).or_insert(v);
    }
    s
}

pub fn save(s: &Settings) {
    let p = config_dir().join("settings.json");
    if let Ok(t) = serde_json::to_string_pretty(s) {
        let tmp = p.with_extension("json.tmp");
        if std::fs::write(&tmp, t).is_ok() {
            let _ = std::fs::rename(&tmp, &p);
        }
    }
}
