#![allow(unused_unsafe)]
// macOS: apps, running apps, icons, idle, wallpaper, volume and quick actions.
// Mirage sits alongside the system menu bar, Dock and Spotlight.
use super::{AppEntry, FileHit, WinInfo};
use objc2_app_kit::{NSApplicationActivationOptions, NSApplicationActivationPolicy, NSBitmapImageFileType, NSBitmapImageRep, NSRunningApplication, NSWorkspace};
use objc2_foundation::{NSDictionary, NSSize, NSString};
use std::path::PathBuf;
use std::process::Command;

/// Runs AppleScript with a time limit. Scripts that talk to other apps can
/// wait on a macOS permission prompt; Mirage must never hang on one.
fn osa(script: &str) -> Result<String, String> {
    osa_for(script, 10)
}
fn osa_for(script: &str, secs: u64) -> Result<String, String> {
    use std::io::Read;
    use std::process::Stdio;
    let mut child = Command::new("osascript")
        .arg("-e")
        .arg(script)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| e.to_string())?;
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(secs);
    let status = loop {
        match child.try_wait() {
            Ok(Some(st)) => break st,
            Ok(None) if std::time::Instant::now() < deadline => std::thread::sleep(std::time::Duration::from_millis(25)),
            Ok(None) => {
                let _ = child.kill();
                let _ = child.wait();
                return Err("macOS didn't answer in time (check Privacy & Security > Automation)".into());
            }
            Err(e) => return Err(e.to_string()),
        }
    };
    let (mut out, mut err) = (String::new(), String::new());
    if let Some(mut o) = child.stdout.take() {
        let _ = o.read_to_string(&mut out);
    }
    if let Some(mut e) = child.stderr.take() {
        let _ = e.read_to_string(&mut err);
    }
    if status.success() {
        Ok(out.trim().to_string())
    } else {
        Err(err.trim().to_string())
    }
}

/// Runs `f` on the main thread (AppKit screen APIs require it) and waits briefly.
fn on_main<T: Send + 'static>(f: impl FnOnce(objc2::MainThreadMarker) -> T + Send + 'static) -> Option<T> {
    if let Some(mtm) = objc2::MainThreadMarker::new() {
        return Some(f(mtm));
    }
    let app = crate::state::APP.get()?;
    let (tx, rx) = std::sync::mpsc::channel();
    app.run_on_main_thread(move || {
        if let Some(mtm) = objc2::MainThreadMarker::new() {
            let _ = tx.send(f(mtm));
        }
    })
    .ok()?;
    rx.recv_timeout(std::time::Duration::from_secs(3)).ok()
}
fn q(s: &str) -> String {
    s.replace('\\', "\\\\").replace('"', "\\\"")
}

pub fn list_apps() -> Vec<AppEntry> {
    let mut roots = vec![PathBuf::from("/Applications"), PathBuf::from("/Applications/Utilities"), PathBuf::from("/System/Applications"), PathBuf::from("/System/Applications/Utilities"), PathBuf::from("/System/Library/CoreServices/Finder.app/..")];
    if let Some(h) = dirs::home_dir() {
        roots.push(h.join("Applications"));
    }
    let mut out: Vec<AppEntry> = vec![];
    for r in roots {
        let Ok(rd) = std::fs::read_dir(&r) else { continue };
        for e in rd.flatten() {
            let p = e.path();
            if p.extension().and_then(|x| x.to_str()) != Some("app") {
                continue;
            }
            let name = p.file_stem().unwrap_or_default().to_string_lossy().to_string();
            if r.ends_with("..") && name != "Finder" {
                continue;
            }
            if out.iter().any(|a| a.name == name) {
                continue;
            }
            let path = std::fs::canonicalize(&p).unwrap_or(p).display().to_string();
            out.push(AppEntry { id: format!("app:{path}"), name, path: path.clone(), target: Some(path), icon: None });
        }
    }
    out.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
    out
}

pub fn launch(path: &str) -> Result<(), String> {
    Command::new("open").arg(path).spawn().map(|_| ()).map_err(|e| e.to_string())
}
pub fn open_path(path: &str) -> Result<(), String> { launch(path) }
pub fn open_url(url: &str) -> Result<(), String> { launch(url) }
pub fn reveal(path: &str) -> Result<(), String> {
    Command::new("open").args(["-R", path]).spawn().map(|_| ()).map_err(|e| e.to_string())
}
pub fn open_special(which: &str) -> Result<(), String> {
    match which {
        "downloads" => launch(&dirs::download_dir().unwrap_or_default().display().to_string()),
        _ => osa("tell application \"Finder\" to open trash\ntell application \"Finder\" to activate").map(|_| ()),
    }
}
pub fn trash(path: &str) -> Result<(), String> {
    osa(&format!("tell application \"Finder\" to delete (POSIX file \"{}\" as alias)", q(path))).map(|_| ())
}

pub fn icon_png(path: &str) -> Option<Vec<u8>> {
    unsafe {
        let ws = NSWorkspace::sharedWorkspace();
        let img = ws.iconForFile(&NSString::from_str(path));
        img.setSize(NSSize::new(128.0, 128.0));
        let tiff = img.TIFFRepresentation()?;
        let rep = NSBitmapImageRep::imageRepWithData(&tiff)?;
        let png = rep.representationUsingType_properties(NSBitmapImageFileType::PNG, &NSDictionary::new())?;
        let bytes = png.to_vec();
        // TIFF reps hold every size; re-encode at 128 px to keep the cache small.
        let img = image::load_from_memory(&bytes).ok()?;
        let img = img.resize(128, 128, image::imageops::FilterType::Lanczos3);
        let mut out = std::io::Cursor::new(vec![]);
        img.write_to(&mut out, image::ImageFormat::Png).ok()?;
        Some(out.into_inner())
    }
}

/// On macOS the overview shows running apps: window titles need Screen
/// Recording permission, which Mirage does not ask for.
pub fn list_windows(_thumbs: bool, own_pid: u32) -> Vec<WinInfo> {
    let mut out = vec![];
    unsafe {
        let ws = NSWorkspace::sharedWorkspace();
        for app in ws.runningApplications().iter() {
            if app.activationPolicy() != NSApplicationActivationPolicy::Regular {
                continue;
            }
            let pid = app.processIdentifier();
            if pid as u32 == own_pid {
                continue;
            }
            let name = app.localizedName().map(|n| n.to_string()).unwrap_or_default();
            let path = app.bundleURL().and_then(|u| u.path()).map(|p| p.to_string());
            out.push(WinInfo { id: pid.to_string(), app: name.clone(), title: if app.isHidden() { "Hidden".into() } else { String::new() }, path, icon: None, thumb: None });
        }
    }
    out
}

pub fn foreground(own_pid: u32) -> Option<(String, String)> {
    unsafe {
        let app = NSWorkspace::sharedWorkspace().frontmostApplication()?;
        if app.processIdentifier() as u32 == own_pid {
            return None;
        }
        Some((app.localizedName()?.to_string(), String::new()))
    }
}

fn app_for(id: &str) -> Option<objc2::rc::Retained<NSRunningApplication>> {
    let pid: i32 = id.parse().ok()?;
    unsafe { NSRunningApplication::runningApplicationWithProcessIdentifier(pid) }
}
pub fn focus_window(id: &str) -> Result<(), String> {
    let app = app_for(id).ok_or("App is not running")?;
    unsafe {
        app.unhide();
        #[allow(deprecated)]
        app.activateWithOptions(NSApplicationActivationOptions::ActivateAllWindows);
    }
    Ok(())
}
pub fn close_window(id: &str) -> Result<(), String> {
    let app = app_for(id).ok_or("App is not running")?;
    unsafe { app.terminate() };
    Ok(())
}

pub fn snap(id: Option<&str>, layout: &str) -> Result<(), String> {
    if let Some(i) = id {
        focus_window(i)?;
        std::thread::sleep(std::time::Duration::from_millis(250));
    }
    let (fx, fy, fw, fh) = match layout {
        "left" => ("0", "0", "0.5", "1"),
        "right" => ("0.5", "0", "0.5", "1"),
        "tl" => ("0", "0", "0.5", "0.5"),
        "tr" => ("0.5", "0", "0.5", "0.5"),
        "bl" => ("0", "0.5", "0.5", "0.5"),
        "br" => ("0.5", "0.5", "0.5", "0.5"),
        "center" => ("0.1667", "0.1", "0.6667", "0.8"),
        _ => ("0", "0", "1", "1"),
    };
    // The Finder desktop window spans the screen; the menu bar is 25 px.
    let s = format!(
        "tell application \"Finder\" to set b to bounds of window of desktop\n\
         set sw to item 3 of b\nset sh to (item 4 of b) - 25\n\
         tell application \"System Events\" to tell (first process whose frontmost is true)\n\
         set position of window 1 to {{round(sw * {fx}), 25 + round(sh * {fy})}}\n\
         set size of window 1 to {{round(sw * {fw}), round(sh * {fh})}}\nend tell"
    );
    osa(&s).map(|_| ())
}

extern "C" {
    fn CGEventSourceSecondsSinceLastEventType(state: i32, event_type: u32) -> f64;
}
pub fn idle_seconds() -> u64 {
    // kCGEventSourceStateCombinedSessionState = 0, kCGAnyInputEventType = !0
    unsafe { CGEventSourceSecondsSinceLastEventType(0, u32::MAX).max(0.0) as u64 }
}
pub fn busy_fullscreen() -> bool { false }

// Wallpaper through NSWorkspace: no Automation permission needed.
pub fn set_wallpaper(path: &str) -> Result<(), String> {
    let path = path.to_string();
    on_main(move |mtm| {
        let url = objc2_foundation::NSURL::fileURLWithPath(&NSString::from_str(&path));
        let ws = NSWorkspace::sharedWorkspace();
        let opts = NSDictionary::new();
        let mut last = Ok(());
        for screen in objc2_app_kit::NSScreen::screens(mtm).iter() {
            if let Err(e) = unsafe { ws.setDesktopImageURL_forScreen_options_error(&url, &screen, &opts) } {
                last = Err(e.localizedDescription().to_string());
            }
        }
        last
    })
    .unwrap_or_else(|| Err("macOS didn't answer in time".into()))
}
pub fn current_wallpaper() -> Option<PathBuf> {
    let p = on_main(|mtm| {
        let screen = objc2_app_kit::NSScreen::mainScreen(mtm)?;
        let url = NSWorkspace::sharedWorkspace().desktopImageURLForScreen(&screen)?;
        Some(url.path()?.to_string())
    })??;
    let p = PathBuf::from(p);
    p.is_file().then_some(p)
}

pub fn volume() -> Option<(f32, bool)> {
    let o = osa("set v to get volume settings\nreturn ((output volume of v) as text) & \",\" & ((output muted of v) as text)").ok()?;
    let mut it = o.split(',');
    Some((it.next()?.trim().parse().ok()?, it.next()?.trim() == "true"))
}
pub fn set_volume(v: f32) -> Result<(), String> {
    osa(&format!("set volume output volume {} without output muted", v.round() as i32)).map(|_| ())
}
pub fn toggle_mute() -> Result<(), String> {
    osa("set volume output muted (not (output muted of (get volume settings)))").map(|_| ())
}
pub fn is_dark() -> bool {
    // Reading the setting needs no permission; only "Dark" is ever stored.
    Command::new("defaults")
        .args(["read", "-g", "AppleInterfaceStyle"])
        .output()
        .map(|o| String::from_utf8_lossy(&o.stdout).trim() == "Dark")
        .unwrap_or(true)
}
pub fn quick(action: &str) -> Result<(), String> {
    match action {
        "lock" | "display_off" => Command::new("pmset").arg("displaysleepnow").spawn().map(|_| ()).map_err(|e| e.to_string()),
        "screenshot" => Command::new("open").args(["-a", "Screenshot"]).spawn().map(|_| ()).map_err(|e| e.to_string()),
        "dark_on" => osa("tell application \"System Events\" to tell appearance preferences to set dark mode to true").map(|_| ()),
        "dark_off" => osa("tell application \"System Events\" to tell appearance preferences to set dark mode to false").map(|_| ()),
        "mute" => toggle_mute(),
        _ => Err(format!("Unknown action {action}")),
    }
}

pub struct Gpu;
impl Gpu {
    pub fn new() -> Option<Gpu> { Some(Gpu) }
    pub fn read(&mut self) -> Option<f32> {
        let o = Command::new("ioreg").args(["-r", "-d", "1", "-c", "IOAccelerator"]).output().ok()?;
        let s = String::from_utf8_lossy(&o.stdout);
        let i = s.find("\"Device Utilization %\"=")?;
        let rest = &s[i + 23..];
        let n: String = rest.chars().take_while(|c| c.is_ascii_digit()).collect();
        n.parse().ok()
    }
}

pub fn voices() -> Vec<String> {
    let o = Command::new("say").args(["-v", "?"]).output();
    o.map(|o| {
        String::from_utf8_lossy(&o.stdout)
            .lines()
            .filter(|l| l.contains("en_") || l.contains("en-"))
            .filter_map(|l| l.split("  ").next().map(|s| s.trim().to_string()))
            .filter(|s| !s.is_empty())
            .collect()
    })
    .unwrap_or_default()
}
pub fn speak(text: &str, voice: &str) -> Option<std::process::Child> {
    let mut c = Command::new("say");
    if !voice.is_empty() {
        c.args(["-v", voice]);
    }
    c.arg(text).spawn().ok()
}

pub fn search_files_native(q: &str, limit: usize) -> Option<Vec<FileHit>> {
    let home = dirs::home_dir()?;
    let o = Command::new("mdfind").args(["-onlyin", &home.display().to_string(), "-name", q]).output().ok()?;
    let mut out = vec![];
    for l in String::from_utf8_lossy(&o.stdout).lines() {
        let p = PathBuf::from(l);
        let rel = p.strip_prefix(&home).map(|r| r.display().to_string()).unwrap_or_default();
        if rel.split('/').any(|s| s.starts_with('.') || s == "Library") {
            continue;
        }
        out.push(FileHit::from_path(&p));
        if out.len() >= limit {
            break;
        }
    }
    Some(out)
}

pub fn appbar_set(_h: isize, _edge: &str, _t: i32) -> Option<(i32, i32, i32, i32)> { None }
pub fn appbar_remove(_h: isize) {}
pub fn set_taskbar_autohide(_on: bool) {}
pub fn round_corners(_h: isize) {}
pub fn tool_window(_h: isize) {}
pub fn work_area() -> Option<(i32, i32, i32, i32)> { None }

/// Private memory isn't read separately on a Mac; the caller falls back to
/// resident memory.
pub fn private_mem(_pid: u32) -> Option<u64> {
    None
}
