// Commands the UI calls. Anything that may create a window is async, so it
// runs off the main thread (a blocking command that builds a window can
// deadlock on Windows).
use crate::platform::{self, AppEntry, FileHit, WinInfo};
use crate::settings::Settings;
use crate::state::{emit, settings, ST};
use crate::{apps, assistant, bridge, stats, views, voice, wallpapers, widgets};
use serde_json::{json, Value};
use tauri::{AppHandle, Manager};

type R<T> = Result<T, String>;

/// Quick actions shared by the control center, the command bar and the assistant.
pub fn quick(action: &str) -> R<()> {
    match action {
        "focus" => stats::start_focus(50),
        "focus_stop" => stats::stop_focus(),
        "dark" => platform::quick(if platform::is_dark() { "dark_off" } else { "dark_on" })?,
        a => platform::quick(a)?,
    }
    emit("control", control());
    Ok(())
}

fn control() -> Value {
    json!({
        "focus": ST.focus.lock().unwrap().on,
        "dark": platform::is_dark(),
        "volume": platform::volume().map(|v| v.0),
    })
}

#[tauri::command]
pub fn get_settings() -> Settings {
    settings()
}

#[tauri::command]
pub async fn set_settings(app: AppHandle, settings: Settings) -> R<Settings> {
    let old = {
        let mut cur = ST.settings.lock().unwrap();
        let old = cur.clone();
        *cur = settings.clone();
        crate::settings::save(&cur);
        old
    };
    emit("settings", settings.clone());
    let s = settings.clone();
    std::thread::spawn(move || {
        let n = &s;
        let layout = serde_json::to_string(&old.surfaces).ok() != serde_json::to_string(&n.surfaces).ok() || old.widgets_side != n.widgets_side || old.reserve_space != n.reserve_space || old.hide_taskbar != n.hide_taskbar;
        if layout {
            views::relayout();
        }
        if old.glass != n.glass {
            // Floating panels pick up the new glass the next time they open.
            for v in ["palette", "control", "overview", "toast"] {
                if let Some(w) = views::get(v) {
                    let _ = w.destroy();
                }
            }
            let _ = crate::state::app().run_on_main_thread(|| {
                views::create("palette");
            });
        }
        if old.wallpaper != n.wallpaper && n.wallpaper.is_empty() {
            let _ = wallpapers::restore_original();
        }
        if old.voice.wake != n.voice.wake {
            voice::wake_changed();
        }
        if old.bridge.enabled != n.bridge.enabled || old.bridge.port != n.bridge.port {
            bridge::sync();
        }
        if serde_json::to_string(&old.location).ok() != serde_json::to_string(&n.location).ok() {
            widgets::refresh_weather();
        }
        if old.calendar_ics != n.calendar_ics {
            widgets::refresh_calendar();
        }
        if old.ai_insight != n.ai_insight {
            widgets::refresh_insight();
        }
    });
    if old.autostart != settings.autostart {
        use tauri_plugin_autostart::ManagerExt;
        let al = app.autolaunch();
        let _ = if settings.autostart { al.enable() } else { al.disable() };
    }
    Ok(settings)
}

#[tauri::command]
pub async fn list_apps() -> Vec<AppEntry> {
    apps::list()
}
#[tauri::command]
pub async fn launch(id: String) -> R<()> {
    apps::launch(&id)
}
#[tauri::command]
pub async fn list_windows(thumbs: Option<bool>) -> Vec<WinInfo> {
    let w = platform::list_windows(thumbs.unwrap_or(false), std::process::id());
    apps::with_icons(w)
}
#[tauri::command]
pub async fn focus_window(id: String) -> R<()> {
    platform::focus_window(&id)
}
#[tauri::command]
pub async fn close_window(id: String) -> R<()> {
    platform::close_window(&id)
}
#[tauri::command]
pub async fn snap_window(id: Option<String>, layout: String) -> R<()> {
    platform::snap(id.as_deref(), &layout)
}
#[tauri::command]
pub fn dock_rect(x: f64, y: f64, w: f64, h: f64) {
    *ST.dock_rect.lock().unwrap() = (x, y, w, h);
}
#[tauri::command]
pub async fn search_files(q: String, limit: Option<usize>) -> Vec<FileHit> {
    crate::files::search(&q, limit.unwrap_or(8).min(50))
}
#[tauri::command]
pub async fn open_path(path: String) -> R<()> {
    platform::open_path(&path)
}
#[tauri::command]
pub async fn reveal_path(path: String) -> R<()> {
    platform::reveal(&path)
}
#[tauri::command]
pub async fn open_special(which: String) -> R<()> {
    platform::open_special(&which)
}
#[tauri::command]
pub async fn open_url(url: String) -> R<()> {
    if !(url.starts_with("https://") || url.starts_with("http://")) {
        return Err("Only web links".into());
    }
    platform::open_url(&url)
}
#[tauri::command]
pub async fn toggle_view(view: String) {
    views::toggle(&view);
}
#[tauri::command]
pub async fn show_view(view: String, section: Option<String>) {
    views::show(&view, section);
}
#[tauri::command]
pub async fn hide_view(view: String) {
    views::hide(&view);
}
#[tauri::command]
pub fn quit(app: AppHandle) {
    crate::shutdown();
    app.exit(0);
}
#[tauri::command]
pub async fn quick_action(action: String) -> R<()> {
    quick(&action)
}
#[tauri::command]
pub async fn control_state() -> Value {
    control()
}
#[tauri::command]
pub async fn set_volume(level: f32) -> R<()> {
    platform::set_volume(level.clamp(0.0, 100.0))
}
#[tauri::command]
pub async fn wallpaper_info(full: Option<bool>) -> Option<Value> {
    wallpapers::info(full.unwrap_or(false))
}
#[tauri::command]
pub async fn list_wallpapers() -> Vec<wallpapers::Wp> {
    wallpapers::list()
}
#[tauri::command]
pub async fn set_wallpaper(id: String) -> R<()> {
    wallpapers::set(&id)
}
#[tauri::command]
pub async fn next_wallpaper() -> R<()> {
    wallpapers::next()
}
#[tauri::command]
pub async fn widgets_data() -> Value {
    widgets::data()
}
#[tauri::command]
pub async fn geocode(q: String) -> R<Value> {
    widgets::geocode(&q)
}

// ---------- assistant ----------
#[tauri::command]
pub async fn assistant_send(text: String) -> R<()> {
    let spoken = voice::take_spoken(&text);
    assistant::send(text, spoken);
    Ok(())
}
#[tauri::command]
pub fn assistant_answer(id: String, allow: bool) {
    assistant::answer(&id, allow);
}
#[tauri::command]
pub fn assistant_reset() {
    assistant::reset();
}
#[tauri::command]
pub async fn assistant_test() -> R<String> {
    assistant::test()
}
#[tauri::command]
pub async fn set_api_key(provider: String, key: String) -> R<()> {
    if !matches!(provider.as_str(), "anthropic" | "openai" | "gemini") {
        return Err("Unknown provider".into());
    }
    assistant::set_key(&provider, key.trim())
}
#[tauri::command]
pub async fn api_key_status() -> Value {
    assistant::key_status()
}

// ---------- voice ----------
#[tauri::command]
pub async fn voice_listen() {
    voice::listen();
}
#[tauri::command]
pub async fn voice_status() -> Value {
    voice::status()
}
#[tauri::command]
pub async fn voice_download() {
    voice::download();
}

#[tauri::command]
pub fn bridge_token() -> String {
    bridge::token()
}

#[tauri::command]
pub async fn about(app: AppHandle) -> Value {
    let s = ST.stats.lock().unwrap().clone();
    let mem = s["mirage_mem"].as_u64().unwrap_or(0) as f64 / 1_048_576.0;
    let cpu = s["mirage_cpu"].as_f64().unwrap_or(0.0);
    let usage = if mem > 0.0 { format!("{mem:.0} MB of memory and {cpu:.1}% CPU, counting its web views") } else { "Measuring…".to_string() };
    json!({ "version": app.package_info().version.to_string(), "usage": usage, "data_dir": app.path().app_data_dir().ok() })
}
