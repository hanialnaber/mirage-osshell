// Mirage's surfaces are separate small windows. Desktop-layer windows (bar,
// dock, widgets) live for the whole session; floating ones are made on first
// use and hidden after; the ambient screen is destroyed when it closes so it
// costs nothing while you work.
use crate::platform;
use crate::state::{app, settings, ST};
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};
use std::sync::{LazyLock, Mutex};
use tauri::{AppHandle, Emitter, Manager, PhysicalPosition, PhysicalSize, WebviewUrl, WebviewWindow, WebviewWindowBuilder};

pub const ALL: [&str; 9] = ["bar", "dock", "widgets", "palette", "overview", "ambient", "control", "settings", "toast"];

static READY: LazyLock<Mutex<HashSet<String>>> = LazyLock::new(|| Mutex::new(HashSet::new()));
static HIDDEN_SINCE: LazyLock<Mutex<HashMap<String, std::time::Instant>>> = LazyLock::new(|| Mutex::new(HashMap::new()));
static QUEUE: LazyLock<Mutex<HashMap<String, Vec<(String, Value)>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

/// Physical rect of a surface: (x, y, w, h).
type Rect = (i32, i32, i32, i32);

struct Screen {
    x: i32,
    y: i32,
    w: i32,
    h: i32,
    s: f64,
    work: Rect,
}

fn screen(a: &AppHandle) -> Screen {
    let m = a.primary_monitor().ok().flatten();
    let (x, y, w, h, s, mut work) = match &m {
        Some(m) => {
            let p = m.position();
            let z = m.size();
            let wa = m.work_area();
            (p.x, p.y, z.width as i32, z.height as i32, m.scale_factor(), (wa.position.x, wa.position.y, wa.size.width as i32, wa.size.height as i32))
        }
        None => (0, 0, 1920, 1080, 1.0, (0, 0, 1920, 1040)),
    };
    if let Some((l, t, r, b)) = platform::work_area() {
        work = (l, t, r - l, b - t);
    }
    Screen { x, y, w, h, s, work }
}

fn px(v: f64, s: f64) -> i32 {
    (v * s).round() as i32
}

fn geometry(view: &str, sc: &Screen) -> Rect {
    let s = sc.s;
    let (wx, wy, ww, wh) = sc.work;
    let st = settings();
    match view {
        "bar" => (sc.x, sc.y, sc.w, px(34.0, s)),
        "dock" => {
            let h = px(150.0, s);
            (sc.x, wy + wh - h + if st.reserve_space && cfg!(windows) { px(96.0, s) } else { 0 }, sc.w, h)
        }
        "widgets" => {
            let w = px(372.0, s);
            let x = if st.widgets_side == "left" { wx + px(8.0, s) } else { wx + ww - w - px(8.0, s) };
            (x, wy + px(10.0, s), w, wh - px(20.0, s))
        }
        "palette" => {
            let (w, h) = (px(880.0, s), px(560.0, s));
            (sc.x + (sc.w - w) / 2, sc.y + (sc.h as f64 * 0.16) as i32, w, h)
        }
        "control" => {
            let (w, h) = (px(380.0, s), px(540.0, s));
            (wx + ww - w - px(12.0, s), wy + px(8.0, s), w, h)
        }
        "toast" => {
            let (w, h) = (px(380.0, s), px(96.0, s));
            (wx + ww - w - px(12.0, s), wy + px(12.0, s), w, h)
        }
        "settings" => {
            let (w, h) = (px(920.0, s), px(640.0, s));
            (sc.x + (sc.w - w) / 2, sc.y + (sc.h - h) / 2, w, h)
        }
        _ => (sc.x, sc.y, sc.w, sc.h), // overview, ambient
    }
}

fn floating(view: &str) -> bool {
    matches!(view, "palette" | "control" | "toast" | "settings")
}

pub fn get(view: &str) -> Option<WebviewWindow> {
    app().get_webview_window(view)
}

fn hwnd(w: &WebviewWindow) -> isize {
    #[cfg(windows)]
    {
        w.hwnd().map(|h| h.0 as isize).unwrap_or(0)
    }
    #[cfg(not(windows))]
    {
        let _ = w;
        0
    }
}

/// Creates a surface window (hidden unless it is a desktop-layer one).
pub fn create(view: &str) -> Option<WebviewWindow> {
    if let Some(w) = get(view) {
        return Some(w);
    }
    let a = app();
    let sc = screen(a);
    let st = settings();
    let vib = floating(view) && st.glass != "solid";
    let init = format!("window.__mirageVib={};window.__mirageSolid={};", vib, floating(view) && !vib);
    let (x, y, w, h) = geometry(view, &sc);
    let desk = matches!(view, "bar" | "dock" | "widgets");
    let mut b = WebviewWindowBuilder::new(a, view, WebviewUrl::App(format!("{view}.html").into()))
        .title(format!("Mirage {view}"))
        .decorations(false)
        .transparent(true)
        .resizable(false)
        .skip_taskbar(true)
        .shadow(false)
        .visible(false)
        .focused(!desk && view != "toast")
        .initialization_script(&init)
        .visible_on_all_workspaces(desk)
        // All surfaces load from the same local origin, so on Windows they can
        // share one WebView2 renderer instead of one each. The first three
        // features are the ones wry turns off by default.
        .additional_browser_args("--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection --process-per-site");
    b = match view {
        "widgets" => b.always_on_bottom(true),
        "settings" => b,
        _ => b.always_on_top(true),
    };
    if view == "toast" {
        b = b.focusable(false);
    }
    #[cfg(target_os = "macos")]
    {
        b = b.accept_first_mouse(true);
    }
    let win = b.build().ok()?;
    let _ = win.set_position(PhysicalPosition::new(x, y));
    let _ = win.set_size(PhysicalSize::new(w as u32, h as u32));
    let hw = hwnd(&win);
    platform::tool_window(hw);
    if vib {
        apply_glass(&win);
    }
    if desk {
        place_desk(view, &win);
        let _ = win.show();
    }
    if view == "dock" {
        let _ = win.set_ignore_cursor_events(true);
    }
    // Floating panels close when you click away, like a menu.
    if matches!(view, "palette" | "control" | "overview") {
        let label = view.to_string();
        win.on_window_event(move |e| {
            if let tauri::WindowEvent::Focused(false) = e {
                let l = label.clone();
                std::thread::spawn(move || {
                    std::thread::sleep(std::time::Duration::from_millis(120));
                    if let Some(w) = get(&l) {
                        if !w.is_focused().unwrap_or(false) {
                            let _ = w.hide();
                        }
                    }
                });
            }
        });
    }
    let label = view.to_string();
    win.on_window_event(move |e| {
        if let tauri::WindowEvent::Destroyed = e {
            READY.lock().unwrap().remove(&label);
        }
    });
    Some(win)
}

fn apply_glass(win: &WebviewWindow) {
    #[cfg(windows)]
    {
        if window_vibrancy::apply_acrylic(win, Some((10, 17, 34, 90))).is_err() {
            let _ = win.eval("document.documentElement.classList.remove('vib');document.documentElement.classList.add('solid')");
        }
        platform::round_corners(hwnd(win));
    }
    #[cfg(target_os = "macos")]
    {
        use window_vibrancy::{NSVisualEffectMaterial, NSVisualEffectState};
        let _ = window_vibrancy::apply_vibrancy(win, NSVisualEffectMaterial::HudWindow, Some(NSVisualEffectState::Active), Some(18.0));
    }
    #[cfg(not(any(windows, target_os = "macos")))]
    let _ = win;
}

/// Bar and dock register as AppBars on Windows so maximized windows stay
/// clear of them; then the widgets column fits the remaining work area.
fn place_desk(view: &str, win: &WebviewWindow) {
    let a = app();
    let sc = screen(a);
    let st = settings();
    let hw = hwnd(win);
    match view {
        "bar" => {
            if let Some((l, t, r, _)) = platform::appbar_set(hw, "top", px(34.0, sc.s)) {
                let _ = win.set_position(PhysicalPosition::new(l, t));
                let _ = win.set_size(PhysicalSize::new((r - l) as u32, px(34.0, sc.s) as u32));
                ST.appbars.lock().unwrap().push(hw);
            }
        }
        "dock" => {
            let h = px(150.0, sc.s);
            if st.reserve_space {
                if let Some((l, _, r, b)) = platform::appbar_set(hw, "bottom", px(96.0, sc.s)) {
                    let _ = win.set_position(PhysicalPosition::new(l, b - h));
                    let _ = win.set_size(PhysicalSize::new((r - l) as u32, h as u32));
                    ST.appbars.lock().unwrap().push(hw);
                }
            }
        }
        _ => {}
    }
}

/// Re-lays out the desktop layer after a setting or screen change.
pub fn relayout() {
    let st = settings();
    let on = |v: &str| match v {
        "bar" => st.surfaces.bar,
        "dock" => st.surfaces.dock,
        "widgets" => st.surfaces.widgets,
        _ => false,
    };
    for v in ["bar", "dock"] {
        if let Some(w) = get(v) {
            let hw = hwnd(&w);
            platform::appbar_remove(hw);
            ST.appbars.lock().unwrap().retain(|h| *h != hw);
            if !on(v) {
                let _ = w.destroy();
            }
        }
    }
    if !on("widgets") {
        if let Some(w) = get("widgets") {
            let _ = w.destroy();
        }
    }
    std::thread::sleep(std::time::Duration::from_millis(80));
    for v in ["bar", "dock", "widgets"] {
        if !on(v) {
            continue;
        }
        match get(v) {
            Some(w) => {
                let sc = screen(app());
                let (x, y, ww, hh) = geometry(v, &sc);
                let _ = w.set_position(PhysicalPosition::new(x, y));
                let _ = w.set_size(PhysicalSize::new(ww as u32, hh as u32));
                place_desk(v, &w);
            }
            None => {
                create(v);
            }
        }
        // Let Windows recompute the work area before the next surface reads it.
        std::thread::sleep(std::time::Duration::from_millis(120));
    }
    platform::set_taskbar_autohide(st.hide_taskbar && cfg!(windows) && st.surfaces.dock);
}

pub fn remove_appbars() {
    for h in ST.appbars.lock().unwrap().drain(..) {
        platform::appbar_remove(h);
    }
}

/// Sends an event to one view, holding it until the view has mounted.
pub fn send(view: &str, evt: &str, payload: Value) {
    if READY.lock().unwrap().contains(view) {
        let _ = app().emit_to(view, evt, payload);
    } else {
        QUEUE.lock().unwrap().entry(view.to_string()).or_default().push((evt.to_string(), payload));
    }
}

pub fn on_ready(view: &str) {
    READY.lock().unwrap().insert(view.to_string());
    let q = QUEUE.lock().unwrap().remove(view).unwrap_or_default();
    for (e, p) in q {
        let _ = app().emit_to(view, &e, p);
    }
}

pub fn show(view: &str, section: Option<String>) {
    let (label, payload) = match view {
        "assistant" => ("palette", json!({ "mode": "assistant" })),
        "voice" => ("palette", json!({ "mode": "voice" })),
        "palette" => ("palette", json!({ "mode": "search" })),
        v => (v, json!({ "section": section })),
    };
    if !ALL.contains(&label) {
        return;
    }
    let Some(w) = create(label) else { return };
    if matches!(label, "bar" | "dock" | "widgets") {
        let _ = w.show();
        return;
    }
    if label == "ambient" {
        #[cfg(target_os = "macos")]
        let _ = w.set_simple_fullscreen(true);
    } else {
        let sc = screen(app());
        let (x, y, ww, hh) = geometry(label, &sc);
        let _ = w.set_position(PhysicalPosition::new(x, y));
        let _ = w.set_size(PhysicalSize::new(ww as u32, hh as u32));
    }
    let _ = w.show();
    if label != "toast" {
        let _ = w.unminimize();
        let _ = w.set_focus();
    }
    let evt = match label {
        "palette" => "palette",
        "settings" => "settings-open",
        "overview" => "overview",
        "ambient" => "ambient",
        "control" => "control",
        _ => "",
    };
    if !evt.is_empty() {
        send(label, evt, payload);
    }
}

pub fn visible(view: &str) -> bool {
    let label = if view == "assistant" || view == "voice" { "palette" } else { view };
    get(label).map(|w| w.is_visible().unwrap_or(false) && w.is_focused().unwrap_or(true)).unwrap_or(false)
}

pub fn toggle(view: &str) {
    if visible(view) {
        hide(view);
    } else {
        show(view, None);
    }
}

pub fn hide(view: &str) {
    let label = if view == "assistant" || view == "voice" { "palette" } else { view };
    let Some(w) = get(label) else { return };
    if label == "ambient" {
        let _ = w.destroy();
        crate::stats::idle_reset();
        return;
    }
    let _ = w.hide();
}

/// Shows a view from any thread (window work belongs on the main thread).
/// A panel that is already open is left as it is.
pub fn show_async(view: &str) {
    if visible(view) {
        return;
    }
    let v = view.to_string();
    let _ = app().run_on_main_thread(move || show(&v, None));
}

/// Panels you open now and then (settings, overview, control center) are
/// closed for real once they've been hidden for a minute, so they hold no
/// memory while you work. The command bar stays ready for instant open.
pub fn reap() {
    let mut since = HIDDEN_SINCE.lock().unwrap();
    for v in ["settings", "overview", "control"] {
        let Some(w) = get(v) else {
            since.remove(v);
            continue;
        };
        if w.is_visible().unwrap_or(true) {
            since.remove(v);
            continue;
        }
        let t = *since.entry(v.to_string()).or_insert_with(std::time::Instant::now);
        if t.elapsed() >= std::time::Duration::from_secs(60) {
            since.remove(v);
            let l = v.to_string();
            let _ = app().run_on_main_thread(move || {
                if let Some(w) = get(&l) {
                    if !w.is_visible().unwrap_or(true) {
                        let _ = w.destroy();
                    }
                }
            });
        }
    }
}
