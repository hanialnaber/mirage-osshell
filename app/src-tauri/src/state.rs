// Shared state and small helpers.
use crate::platform::{AppEntry, WinInfo};
use crate::settings::Settings;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;
use std::sync::{mpsc, LazyLock, Mutex, OnceLock, RwLock};
use tauri::{AppHandle, Emitter};

pub static APP: OnceLock<AppHandle> = OnceLock::new();
pub fn app() -> &'static AppHandle {
    APP.get().expect("app handle")
}
pub fn emit<S: Serialize + Clone>(evt: &str, payload: S) {
    if let Some(a) = APP.get() {
        let _ = a.emit(evt, payload);
    }
}

#[derive(Serialize, Deserialize, Clone, Default, Debug)]
pub struct Focus {
    pub on: bool,
    pub started_at: Option<String>,
    pub ends_at: Option<String>,
    pub today_minutes: f64,
    pub day: String,
}

pub struct WpCache {
    pub path: String,
    pub modified: u64,
    pub small: String,
    pub full: Option<String>,
}

pub struct St {
    pub settings: Mutex<Settings>,
    pub apps: RwLock<Vec<AppEntry>>,
    pub windows: RwLock<Vec<WinInfo>>,
    pub stats: Mutex<Value>,
    pub focus: Mutex<Focus>,
    pub dock_rect: Mutex<(f64, f64, f64, f64)>,
    pub wallpaper: Mutex<Option<WpCache>>,
    pub weather: Mutex<Option<Value>>,
    pub calendar: Mutex<Option<Value>>,
    pub insight: Mutex<Option<Value>>,
    pub pending: Mutex<HashMap<String, mpsc::Sender<bool>>>,
    pub convo: Mutex<Vec<Value>>,
    pub appbars: Mutex<Vec<isize>>,
    pub busy: Mutex<bool>,
}

pub static ST: LazyLock<St> = LazyLock::new(|| St {
    settings: Mutex::new(crate::settings::load()),
    apps: RwLock::new(vec![]),
    windows: RwLock::new(vec![]),
    stats: Mutex::new(Value::Null),
    focus: Mutex::new(load_focus()),
    dock_rect: Mutex::new((0.0, 0.0, 0.0, 0.0)),
    wallpaper: Mutex::new(None),
    weather: Mutex::new(None),
    calendar: Mutex::new(None),
    insight: Mutex::new(None),
    pending: Mutex::new(HashMap::new()),
    convo: Mutex::new(vec![]),
    appbars: Mutex::new(vec![]),
    busy: Mutex::new(false),
});

pub fn settings() -> Settings {
    ST.settings.lock().unwrap().clone()
}

fn focus_file() -> std::path::PathBuf {
    crate::settings::data_dir().join("focus.json")
}
fn load_focus() -> Focus {
    let mut f: Focus = std::fs::read_to_string(focus_file()).ok().and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default();
    let today = chrono::Local::now().format("%Y-%m-%d").to_string();
    if f.day != today {
        f = Focus { day: today, ..Default::default() };
    }
    f
}
pub fn save_focus(f: &Focus) {
    if let Ok(t) = serde_json::to_string(f) {
        let _ = std::fs::write(focus_file(), t);
    }
}

pub fn toast(title: &str, body: &str) {
    if ST.focus.lock().unwrap().on {
        return; // Focus holds Mirage's own notifications.
    }
    crate::views::show("toast", None);
    let t = title.to_string();
    let b = body.to_string();
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_millis(250));
        emit("toast", serde_json::json!({ "title": t, "body": b }));
    });
}
