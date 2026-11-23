// Windows: apps, windows, icons, idle, wallpaper, volume, AppBar and quick actions.
#![allow(clippy::missing_safety_doc)]
use super::{AppEntry, WinInfo};
use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use windows::core::{Interface, BOOL, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, RECT, WPARAM};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DwmSetWindowAttribute, DWMWA_CLOAKED, DWMWA_EXTENDED_FRAME_BOUNDS, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND};
use windows::Win32::Graphics::Gdi::*;
use windows::Win32::Storage::FileSystem::FILE_FLAGS_AND_ATTRIBUTES;
use windows::Win32::Storage::Xps::{PrintWindow, PRINT_WINDOW_FLAGS};
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_APARTMENTTHREADED};
use windows::Win32::System::Threading::{OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION};
use windows::Win32::UI::Controls::IImageList;
use windows::Win32::UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO};
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

const NO_WINDOW: u32 = 0x0800_0000;

fn wide(s: &str) -> Vec<u16> {
    OsStr::new(s).encode_wide().chain(std::iter::once(0)).collect()
}
fn hwnd_of(id: &str) -> HWND {
    HWND(id.parse::<isize>().unwrap_or(0) as *mut _)
}
fn hidden_cmd(prog: &str) -> Command {
    let mut c = Command::new(prog);
    c.creation_flags(NO_WINDOW);
    c
}

// ---------------- apps ----------------
pub fn list_apps() -> Vec<AppEntry> {
    let mut out: Vec<AppEntry> = vec![];
    let mut roots = vec![];
    if let Some(a) = dirs::data_dir() {
        roots.push(a.join(r"Microsoft\Windows\Start Menu\Programs"));
    }
    if let Ok(pd) = std::env::var("ProgramData") {
        roots.push(PathBuf::from(pd).join(r"Microsoft\Windows\Start Menu\Programs"));
    }
    let skip = ["uninstall", "readme", "help", "documentation", "license", "release notes", "website", "manual", "support"];
    for r in roots {
        for e in walkdir::WalkDir::new(&r).max_depth(3).into_iter().flatten() {
            let p = e.path();
            if p.extension().and_then(|x| x.to_str()).map(|x| x.eq_ignore_ascii_case("lnk")) != Some(true) {
                continue;
            }
            let name = p.file_stem().unwrap_or_default().to_string_lossy().to_string();
            let low = name.to_lowercase();
            if skip.iter().any(|s| low.contains(s)) || out.iter().any(|a| a.name.eq_ignore_ascii_case(&name)) {
                continue;
            }
            let target = resolve_lnk(p);
            if let Some(t) = &target {
                let tl = t.to_lowercase();
                if !(tl.ends_with(".exe") || tl.is_empty()) {
                    continue; // links to documents and web pages
                }
            }
            out.push(AppEntry { id: format!("lnk:{}", p.display()), name, path: p.display().to_string(), target, icon: None });
        }
    }
    // Store and system apps that have no shortcut file (Settings, Calculator, Photos...).
    if let Ok(o) = hidden_cmd("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", "Get-StartApps | ForEach-Object { $_.Name + \"`t\" + $_.AppID }"])
        .output()
    {
        for line in String::from_utf8_lossy(&o.stdout).lines() {
            let mut it = line.splitn(2, '\t');
            let (Some(n), Some(id)) = (it.next(), it.next()) else { continue };
            let n = n.trim();
            let id = id.trim();
            if n.is_empty() || !id.contains('!') && !id.contains('.') || out.iter().any(|a| a.name.eq_ignore_ascii_case(n)) {
                continue;
            }
            let low = n.to_lowercase();
            if skip.iter().any(|s| low.contains(s)) {
                continue;
            }
            out.push(AppEntry { id: format!("aumid:{id}"), name: n.to_string(), path: format!(r"shell:AppsFolder\{id}"), target: None, icon: None });
        }
    }
    out.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()));
    out
}

fn resolve_lnk(p: &Path) -> Option<String> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_ALL).ok()?;
        let pf: windows::Win32::System::Com::IPersistFile = link.cast().ok()?;
        let w = wide(&p.display().to_string());
        pf.Load(PCWSTR(w.as_ptr()), windows::Win32::System::Com::STGM(0)).ok()?;
        let mut buf = [0u16; 520];
        link.GetPath(&mut buf, std::ptr::null_mut(), 0).ok()?;
        let s = String::from_utf16_lossy(&buf[..buf.iter().position(|&c| c == 0).unwrap_or(0)]);
        Some(s)
    }
}

pub fn launch(path: &str) -> Result<(), String> {
    if path.starts_with("shell:") {
        hidden_cmd("explorer.exe").arg(path).spawn().map(|_| ()).map_err(|e| e.to_string())
    } else {
        shell_open(path, None)
    }
}

fn shell_open(path: &str, verb: Option<&str>) -> Result<(), String> {
    let p = wide(path);
    let v = verb.map(wide);
    let r = unsafe {
        ShellExecuteW(None, v.as_ref().map(|v| PCWSTR(v.as_ptr())).unwrap_or(PCWSTR::null()), PCWSTR(p.as_ptr()), PCWSTR::null(), PCWSTR::null(), SW_SHOWNORMAL)
    };
    if r.0 as isize > 32 { Ok(()) } else { Err(format!("Windows could not open {path}")) }
}
pub fn open_path(path: &str) -> Result<(), String> { shell_open(path, None) }
pub fn open_url(url: &str) -> Result<(), String> { shell_open(url, None) }
pub fn reveal(path: &str) -> Result<(), String> {
    hidden_cmd("explorer.exe").arg(format!("/select,{path}")).spawn().map(|_| ()).map_err(|e| e.to_string())
}
pub fn open_special(which: &str) -> Result<(), String> {
    match which {
        "downloads" => open_path(&dirs::download_dir().unwrap_or_default().display().to_string()),
        _ => hidden_cmd("explorer.exe").arg("shell:RecycleBinFolder").spawn().map(|_| ()).map_err(|e| e.to_string()),
    }
}
pub fn trash(path: &str) -> Result<(), String> {
    // Moves to the Recycle Bin through the shell, so it can be restored.
    let ps = format!("Add-Type -AssemblyName Microsoft.VisualBasic; $p='{}'; if (Test-Path -LiteralPath $p -PathType Container) {{ [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($p,'OnlyErrorDialogs','SendToRecycleBin') }} else {{ [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($p,'OnlyErrorDialogs','SendToRecycleBin') }}", path.replace('\'', "''"));
    let o = hidden_cmd("powershell.exe").args(["-NoProfile", "-NonInteractive", "-Command", &ps]).output().map_err(|e| e.to_string())?;
    if o.status.success() { Ok(()) } else { Err(String::from_utf8_lossy(&o.stderr).to_string()) }
}

// ---------------- icons ----------------
pub fn icon_png(path: &str) -> Option<Vec<u8>> {
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let w = wide(path);
        let mut info = SHFILEINFOW::default();
        let r = SHGetFileInfoW(PCWSTR(w.as_ptr()), FILE_FLAGS_AND_ATTRIBUTES(0), Some(&mut info), std::mem::size_of::<SHFILEINFOW>() as u32, SHGFI_SYSICONINDEX);
        if r == 0 {
            return None;
        }
        let list: IImageList = SHGetImageList(SHIL_JUMBO as i32).ok()?;
        let hicon = list.GetIcon(info.iIcon, 0x1 /* ILD_TRANSPARENT */).ok()?;
        let png = hicon_to_png(hicon, 256);
        let _ = DestroyIcon(hicon);
        png
    }
}

unsafe fn hicon_to_png(hicon: HICON, size: i32) -> Option<Vec<u8>> {
    let mut ii = ICONINFO::default();
    GetIconInfo(hicon, &mut ii).ok()?;
    let hdc = CreateCompatibleDC(None);
    let mut bmi = BITMAPINFO::default();
    bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
    bmi.bmiHeader.biWidth = size;
    bmi.bmiHeader.biHeight = -size;
    bmi.bmiHeader.biPlanes = 1;
    bmi.bmiHeader.biBitCount = 32;
    let mut px = vec![0u8; (size * size * 4) as usize];
    let lines = GetDIBits(hdc, ii.hbmColor, 0, size as u32, Some(px.as_mut_ptr() as *mut _), &mut bmi, DIB_RGB_COLORS);
    let _ = DeleteDC(hdc);
    let _ = DeleteObject(ii.hbmColor.into());
    let _ = DeleteObject(ii.hbmMask.into());
    if lines == 0 {
        return None;
    }
    // BGRA -> RGBA, and find the real content box (jumbo lists pad small icons).
    let (mut minx, mut miny, mut maxx, mut maxy) = (size, size, 0, 0);
    for y in 0..size {
        for x in 0..size {
            let i = ((y * size + x) * 4) as usize;
            px.swap(i, i + 2);
            if px[i + 3] > 8 {
                minx = minx.min(x); miny = miny.min(y); maxx = maxx.max(x); maxy = maxy.max(y);
            }
        }
    }
    if maxx <= minx {
        return None;
    }
    let img = image::RgbaImage::from_raw(size as u32, size as u32, px)?;
    let side = (maxx - minx).max(maxy - miny) as u32 + 1;
    let img = if side < 120 {
        // A 32 or 48 px icon inside a 256 canvas: crop and scale up a little.
        let cx = ((minx + maxx) / 2) as i64 - side as i64 / 2;
        let cy = ((miny + maxy) / 2) as i64 - side as i64 / 2;
        let c = image::imageops::crop_imm(&img, cx.max(0) as u32, cy.max(0) as u32, side, side).to_image();
        image::imageops::resize(&c, 96, 96, image::imageops::FilterType::CatmullRom)
    } else {
        image::imageops::resize(&img, 128, 128, image::imageops::FilterType::Lanczos3)
    };
    let mut out = std::io::Cursor::new(vec![]);
    img.write_to(&mut out, image::ImageFormat::Png).ok()?;
    Some(out.into_inner())
}

// ---------------- windows ----------------
fn exe_of(hwnd: HWND) -> Option<String> {
    unsafe {
        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, Some(&mut pid));
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 1024];
        let mut n = buf.len() as u32;
        let r = QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut n);
        let _ = CloseHandle(h);
        r.ok()?;
        Some(String::from_utf16_lossy(&buf[..n as usize]))
    }
}
fn title_of(hwnd: HWND) -> String {
    let mut buf = [0u16; 512];
    let n = unsafe { GetWindowTextW(hwnd, &mut buf) };
    String::from_utf16_lossy(&buf[..n.max(0) as usize])
}
fn is_app_window(hwnd: HWND) -> bool {
    unsafe {
        if !IsWindowVisible(hwnd).as_bool() {
            return false;
        }
        if GetWindow(hwnd, GW_OWNER).map(|o| !o.0.is_null()).unwrap_or(false) {
            return false;
        }
        let ex = GetWindowLongW(hwnd, GWL_EXSTYLE) as u32;
        if ex & WS_EX_TOOLWINDOW.0 != 0 {
            return false;
        }
        let mut cloaked = 0u32;
        let _ = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut _, 4);
        if cloaked != 0 {
            return false;
        }
        !title_of(hwnd).is_empty()
    }
}
fn app_name(exe: &str) -> String {
    // The product name from the file's version info would be nicer; the file name is reliable.
    let stem = Path::new(exe).file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default();
    match stem.to_lowercase().as_str() {
        "explorer" => "File Explorer".into(),
        "msedge" => "Microsoft Edge".into(),
        "chrome" => "Google Chrome".into(),
        "firefox" => "Firefox".into(),
        "code" => "Visual Studio Code".into(),
        "windowsterminal" | "wt" => "Terminal".into(),
        "outlook" | "olk" => "Outlook".into(),
        "winword" => "Word".into(),
        "excel" => "Excel".into(),
        "powerpnt" => "PowerPoint".into(),
        "ms-teams" | "teams" => "Microsoft Teams".into(),
        "spotify" => "Spotify".into(),
        "applicationframehost" => "App".into(),
        _ => {
            let mut c = stem.chars();
            c.next().map(|f| f.to_uppercase().collect::<String>() + c.as_str()).unwrap_or(stem)
        }
    }
}

pub fn list_windows(thumbs: bool, own_pid: u32) -> Vec<WinInfo> {
    let mut hwnds: Vec<HWND> = vec![];
    unsafe extern "system" fn cb(h: HWND, l: LPARAM) -> BOOL {
        let v = &mut *(l.0 as *mut Vec<HWND>);
        v.push(h);
        BOOL(1)
    }
    unsafe {
        let _ = EnumWindows(Some(cb), LPARAM(&mut hwnds as *mut _ as isize));
    }
    let mut out = vec![];
    for h in hwnds {
        if !is_app_window(h) {
            continue;
        }
        let mut pid = 0u32;
        unsafe { GetWindowThreadProcessId(h, Some(&mut pid)) };
        if pid == own_pid {
            continue;
        }
        let path = exe_of(h);
        let title = title_of(h);
        let mut app = path.as_deref().map(app_name).unwrap_or_default();
        if app == "App" || app.is_empty() {
            app = title.clone();
        }
        let thumb = if thumbs { thumbnail(h) } else { None };
        out.push(WinInfo { id: (h.0 as isize).to_string(), app, title, path, icon: None, thumb });
    }
    out
}

fn thumbnail(h: HWND) -> Option<String> {
    unsafe {
        if IsIconic(h).as_bool() {
            return None;
        }
        let mut r = RECT::default();
        GetWindowRect(h, &mut r).ok()?;
        let (w, hh) = (r.right - r.left, r.bottom - r.top);
        if w < 50 || hh < 50 || w > 10000 {
            return None;
        }
        let sdc = GetDC(None);
        let dc = CreateCompatibleDC(Some(sdc));
        let bmp = CreateCompatibleBitmap(sdc, w, hh);
        let old = SelectObject(dc, bmp.into());
        let ok = PrintWindow(h, dc, PRINT_WINDOW_FLAGS(2) /* PW_RENDERFULLCONTENT */).as_bool();
        let mut bmi = BITMAPINFO::default();
        bmi.bmiHeader.biSize = std::mem::size_of::<BITMAPINFOHEADER>() as u32;
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -hh;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        let mut px = vec![0u8; (w * hh * 4) as usize];
        SelectObject(dc, old);
        let lines = GetDIBits(dc, bmp, 0, hh as u32, Some(px.as_mut_ptr() as *mut _), &mut bmi, DIB_RGB_COLORS);
        let _ = DeleteObject(bmp.into());
        let _ = DeleteDC(dc);
        ReleaseDC(None, sdc);
        if !ok || lines == 0 {
            return None;
        }
        let mut rgb = Vec::with_capacity((w * hh * 3) as usize);
        for c in px.chunks_exact(4) {
            rgb.extend_from_slice(&[c[2], c[1], c[0]]);
        }
        let img = image::RgbImage::from_raw(w as u32, hh as u32, rgb)?;
        let tw = 480u32;
        let th = (hh as u32 * tw / w as u32).max(1);
        let small = image::imageops::resize(&img, tw, th, image::imageops::FilterType::Triangle);
        super::jpeg_data_url(&image::DynamicImage::ImageRgb8(small), 78)
    }
}

pub fn foreground(own_pid: u32) -> Option<(String, String)> {
    unsafe {
        let h = GetForegroundWindow();
        if h.0.is_null() {
            return None;
        }
        let mut pid = 0u32;
        GetWindowThreadProcessId(h, Some(&mut pid));
        if pid == own_pid {
            return None;
        }
        let title = title_of(h);
        let app = exe_of(h).map(|p| app_name(&p)).unwrap_or_default();
        if app == "File Explorer" && title.is_empty() {
            return Some(("Desktop".into(), String::new()));
        }
        Some((if app == "App" { title.clone() } else { app }, title))
    }
}

pub fn focus_window(id: &str) -> Result<(), String> {
    let h = hwnd_of(id);
    unsafe {
        if IsIconic(h).as_bool() {
            let _ = ShowWindow(h, SW_RESTORE);
        }
        // Windows only lets the foreground app hand over focus; a tap of Alt
        // counts as user input and lifts that rule for this call.
        use windows::Win32::UI::Input::KeyboardAndMouse::{keybd_event, KEYEVENTF_KEYUP, VK_MENU};
        keybd_event(VK_MENU.0 as u8, 0, Default::default(), 0);
        keybd_event(VK_MENU.0 as u8, 0, KEYEVENTF_KEYUP, 0);
        let _ = SetForegroundWindow(h);
    }
    Ok(())
}
pub fn close_window(id: &str) -> Result<(), String> {
    unsafe { PostMessageW(Some(hwnd_of(id)), WM_CLOSE, WPARAM(0), LPARAM(0)).map_err(|e| e.to_string()) }
}

pub fn snap(id: Option<&str>, layout: &str) -> Result<(), String> {
    unsafe {
        let h = match id { Some(i) => hwnd_of(i), None => GetForegroundWindow() };
        if h.0.is_null() {
            return Err("No window to snap".into());
        }
        let mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let _ = GetMonitorInfoW(mon, &mut mi);
        let wa = mi.rcWork;
        let (ww, wh) = (wa.right - wa.left, wa.bottom - wa.top);
        let g = 8; // gap between snapped windows
        let (x, y, w, hh) = match layout {
            "left" => (0, 0, ww / 2, wh),
            "right" => (ww / 2, 0, ww - ww / 2, wh),
            "top" => (0, 0, ww, wh / 2),
            "bottom" => (0, wh / 2, ww, wh - wh / 2),
            "tl" => (0, 0, ww / 2, wh / 2),
            "tr" => (ww / 2, 0, ww - ww / 2, wh / 2),
            "bl" => (0, wh / 2, ww / 2, wh - wh / 2),
            "br" => (ww / 2, wh / 2, ww - ww / 2, wh - wh / 2),
            "third1" => (0, 0, ww / 3, wh),
            "third2" => (ww / 3, 0, ww / 3, wh),
            "third3" => (2 * ww / 3, 0, ww - 2 * ww / 3, wh),
            "center" => (ww / 6, wh / 10, ww * 2 / 3, wh * 8 / 10),
            _ => (0, 0, ww, wh),
        };
        if IsZoomed(h).as_bool() {
            let _ = ShowWindow(h, SW_RESTORE);
        }
        // Account for the invisible resize border Windows adds around windows.
        let mut outer = RECT::default();
        let mut frame = RECT::default();
        let _ = GetWindowRect(h, &mut outer);
        let _ = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, &mut frame as *mut _ as *mut _, std::mem::size_of::<RECT>() as u32);
        let (bl, bt, br, bb) = (frame.left - outer.left, frame.top - outer.top, outer.right - frame.right, outer.bottom - frame.bottom);
        let gx = if layout == "max" { 0 } else { g };
        SetWindowPos(h, None, wa.left + x + gx / 2 - bl, wa.top + y + gx / 2 - bt, w - gx + bl + br, hh - gx + bt + bb, SWP_NOZORDER | SWP_NOACTIVATE)
            .map_err(|e| e.to_string())
    }
}

// ---------------- system ----------------
pub fn idle_seconds() -> u64 {
    unsafe {
        let mut li = LASTINPUTINFO { cbSize: std::mem::size_of::<LASTINPUTINFO>() as u32, dwTime: 0 };
        if GetLastInputInfo(&mut li).as_bool() {
            let now = windows::Win32::System::SystemInformation::GetTickCount();
            return (now.wrapping_sub(li.dwTime) / 1000) as u64;
        }
    }
    0
}
/// True while a full-screen game, video or presentation is running.
pub fn busy_fullscreen() -> bool {
    unsafe {
        matches!(SHQueryUserNotificationState(), Ok(s) if s == QUNS_BUSY || s == QUNS_RUNNING_D3D_FULL_SCREEN || s == QUNS_PRESENTATION_MODE)
    }
}

pub fn set_wallpaper(path: &str) -> Result<(), String> {
    use windows::Win32::System::Registry::{RegSetKeyValueW, HKEY_CURRENT_USER, REG_SZ};
    let set = |name: &str, val: &str| unsafe {
        let k = wide(r"Control Panel\Desktop");
        let n = wide(name);
        let v = wide(val);
        let _ = RegSetKeyValueW(HKEY_CURRENT_USER, PCWSTR(k.as_ptr()), PCWSTR(n.as_ptr()), REG_SZ.0, Some(v.as_ptr() as *const _), (v.len() * 2) as u32);
    };
    set("WallpaperStyle", "10"); // Fill
    set("TileWallpaper", "0");
    let mut w = wide(path);
    unsafe {
        SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, Some(w.as_mut_ptr() as *mut _), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE).map_err(|e| e.to_string())
    }
}
pub fn current_wallpaper() -> Option<PathBuf> {
    let mut buf = [0u16; 1024];
    unsafe {
        SystemParametersInfoW(SPI_GETDESKWALLPAPER, buf.len() as u32, Some(buf.as_mut_ptr() as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0)).ok()?;
    }
    let s = String::from_utf16_lossy(&buf[..buf.iter().position(|&c| c == 0).unwrap_or(0)]);
    let p = PathBuf::from(&s);
    if !s.is_empty() && p.exists() {
        return Some(p);
    }
    // Windows keeps a copy of the current picture here, also for slideshows.
    let t = dirs::data_dir()?.join(r"Microsoft\Windows\Themes\TranscodedWallpaper");
    t.exists().then_some(t)
}

fn endpoint_volume() -> Option<windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume> {
    use windows::Win32::Media::Audio::*;
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let en: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
        let dev = en.GetDefaultAudioEndpoint(eRender, eConsole).ok()?;
        dev.Activate(CLSCTX_ALL, None).ok()
    }
}
pub fn volume() -> Option<(f32, bool)> {
    let ev = endpoint_volume()?;
    unsafe { Some((ev.GetMasterVolumeLevelScalar().ok()? * 100.0, ev.GetMute().ok()?.as_bool())) }
}
pub fn set_volume(v: f32) -> Result<(), String> {
    let ev = endpoint_volume().ok_or("No audio device")?;
    unsafe {
        let _ = ev.SetMute(false, std::ptr::null());
        ev.SetMasterVolumeLevelScalar((v / 100.0).clamp(0.0, 1.0), std::ptr::null()).map_err(|e| e.to_string())
    }
}
pub fn toggle_mute() -> Result<(), String> {
    let ev = endpoint_volume().ok_or("No audio device")?;
    unsafe {
        let m = ev.GetMute().map_err(|e| e.to_string())?.as_bool();
        ev.SetMute(!m, std::ptr::null()).map_err(|e| e.to_string())
    }
}

pub fn is_dark() -> bool {
    let o = hidden_cmd("reg").args(["query", r"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "/v", "AppsUseLightTheme"]).output();
    o.map(|o| String::from_utf8_lossy(&o.stdout).contains("0x0")).unwrap_or(true)
}

pub fn quick(action: &str) -> Result<(), String> {
    match action {
        "lock" => unsafe { windows::Win32::System::Shutdown::LockWorkStation().map_err(|e| e.to_string()) },
        "display_off" => unsafe {
            // SC_MONITORPOWER, 2 = off. Broadcast so it works from any window.
            let _ = SendMessageTimeoutW(HWND_BROADCAST, WM_SYSCOMMAND, WPARAM(0xF170), LPARAM(2), SMTO_ABORTIFHUNG, 500, None);
            Ok(())
        },
        "screenshot" => hidden_cmd("explorer.exe").arg("ms-screenclip:").spawn().map(|_| ()).map_err(|e| e.to_string()),
        "dark_on" | "dark_off" => {
            let v = if action == "dark_on" { "0" } else { "1" };
            for name in ["AppsUseLightTheme", "SystemUsesLightTheme"] {
                hidden_cmd("reg").args(["add", r"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "/v", name, "/t", "REG_DWORD", "/d", v, "/f"]).output().map_err(|e| e.to_string())?;
            }
            unsafe {
                let s = wide("ImmersiveColorSet");
                let _ = SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, WPARAM(0), LPARAM(s.as_ptr() as isize), SMTO_ABORTIFHUNG, 200, None);
            }
            Ok(())
        }
        "mute" => toggle_mute(),
        _ => Err(format!("Unknown action {action}")),
    }
}

// ---------------- GPU ----------------
pub struct Gpu {
    q: isize,
    c: isize,
}
unsafe impl Send for Gpu {}
impl Gpu {
    pub fn new() -> Option<Gpu> {
        use windows::Win32::System::Performance::*;
        unsafe {
            let mut q = PDH_HQUERY::default();
            if PdhOpenQueryW(PCWSTR::null(), 0, &mut q) != 0 {
                return None;
            }
            let mut c = PDH_HCOUNTER::default();
            let path = wide(r"\GPU Engine(*engtype_3D)\Utilization Percentage");
            if PdhAddEnglishCounterW(q, PCWSTR(path.as_ptr()), 0, &mut c) != 0 {
                return None;
            }
            PdhCollectQueryData(q);
            Some(Gpu { q: q.0 as isize, c: c.0 as isize })
        }
    }
    pub fn read(&mut self) -> Option<f32> {
        use windows::Win32::System::Performance::*;
        unsafe {
            let q = PDH_HQUERY(self.q as *mut _);
            let c = PDH_HCOUNTER(self.c as *mut _);
            if PdhCollectQueryData(q) != 0 {
                return None;
            }
            let (mut size, mut n) = (0u32, 0u32);
            PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE, &mut size, &mut n, None);
            if size == 0 {
                return None;
            }
            let mut buf = vec![0u8; size as usize];
            if PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE, &mut size, &mut n, Some(buf.as_mut_ptr() as *mut PDH_FMT_COUNTERVALUE_ITEM_W)) != 0 {
                return None;
            }
            let items = std::slice::from_raw_parts(buf.as_ptr() as *const PDH_FMT_COUNTERVALUE_ITEM_W, n as usize);
            let total: f64 = items.iter().map(|i| i.FmtValue.Anonymous.doubleValue).sum();
            Some(total.clamp(0.0, 100.0) as f32)
        }
    }
}

// ---------------- AppBar: keeps maximized windows clear of the bar and dock ----------------
const APPBAR_MSG: u32 = WM_APP + 0x4D;
pub fn appbar_set(hwnd_raw: isize, edge: &str, thickness: i32) -> Option<(i32, i32, i32, i32)> {
    unsafe {
        let hwnd = HWND(hwnd_raw as *mut _);
        let mut d = APPBARDATA { cbSize: std::mem::size_of::<APPBARDATA>() as u32, hWnd: hwnd, uCallbackMessage: APPBAR_MSG, ..Default::default() };
        SHAppBarMessage(ABM_NEW, &mut d);
        let mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let _ = GetMonitorInfoW(mon, &mut mi);
        let m = mi.rcMonitor;
        d.uEdge = if edge == "top" { ABE_TOP } else { ABE_BOTTOM };
        d.rc = m;
        if edge == "top" { d.rc.bottom = m.top + thickness } else { d.rc.top = m.bottom - thickness }
        SHAppBarMessage(ABM_QUERYPOS, &mut d);
        if edge == "top" { d.rc.bottom = d.rc.top + thickness } else { d.rc.top = d.rc.bottom - thickness }
        SHAppBarMessage(ABM_SETPOS, &mut d);
        Some((d.rc.left, d.rc.top, d.rc.right, d.rc.bottom))
    }
}
pub fn appbar_remove(hwnd_raw: isize) {
    unsafe {
        let mut d = APPBARDATA { cbSize: std::mem::size_of::<APPBARDATA>() as u32, hWnd: HWND(hwnd_raw as *mut _), ..Default::default() };
        SHAppBarMessage(ABM_REMOVE, &mut d);
    }
}
pub fn set_taskbar_autohide(on: bool) {
    unsafe {
        let tb = FindWindowW(windows::core::w!("Shell_TrayWnd"), PCWSTR::null()).unwrap_or_default();
        let mut d = APPBARDATA { cbSize: std::mem::size_of::<APPBARDATA>() as u32, hWnd: tb, ..Default::default() };
        d.lParam = LPARAM(if on { ABS_AUTOHIDE as isize } else { ABS_ALWAYSONTOP as isize });
        SHAppBarMessage(ABM_SETSTATE, &mut d);
    }
}
pub fn round_corners(hwnd_raw: isize) {
    unsafe {
        let pref = DWMWCP_ROUND;
        let _ = DwmSetWindowAttribute(HWND(hwnd_raw as *mut _), DWMWA_WINDOW_CORNER_PREFERENCE, &pref as *const _ as *const _, std::mem::size_of_val(&pref) as u32);
    }
}
/// Keeps a window out of Alt+Tab and the taskbar.
pub fn tool_window(hwnd_raw: isize) {
    unsafe {
        let h = HWND(hwnd_raw as *mut _);
        let ex = GetWindowLongW(h, GWL_EXSTYLE) as u32;
        SetWindowLongW(h, GWL_EXSTYLE, (ex | WS_EX_TOOLWINDOW.0) as i32);
    }
}
/// Work area of the primary monitor in physical pixels (screen minus taskbar and app bars).
pub fn work_area() -> Option<(i32, i32, i32, i32)> {
    let mut r = RECT::default();
    unsafe { SystemParametersInfoW(SPI_GETWORKAREA, 0, Some(&mut r as *mut _ as *mut _), SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0)).ok()? };
    Some((r.left, r.top, r.right, r.bottom))
}

// ---------------- speech ----------------
pub fn voices() -> Vec<String> {
    let o = hidden_cmd("powershell.exe").args(["-NoProfile", "-NonInteractive", "-Command", "Add-Type -AssemblyName System.Speech; (New-Object System.Speech.Synthesis.SpeechSynthesizer).GetInstalledVoices() | ForEach-Object { $_.VoiceInfo.Name }"]).output();
    o.map(|o| String::from_utf8_lossy(&o.stdout).lines().map(|l| l.trim().to_string()).filter(|l| !l.is_empty()).collect()).unwrap_or_default()
}
pub fn speak(text: &str, voice: &str) -> Option<std::process::Child> {
    let t = text.replace('\'', "''");
    let v = if voice.is_empty() { String::new() } else { format!("$s.SelectVoice('{}');", voice.replace('\'', "''")) };
    hidden_cmd("powershell.exe").args(["-NoProfile", "-NonInteractive", "-Command", &format!("Add-Type -AssemblyName System.Speech; $s=New-Object System.Speech.Synthesis.SpeechSynthesizer; {v} $s.Rate=1; $s.Speak('{t}')")]).spawn().ok()
}

pub fn search_files_native(_q: &str, _limit: usize) -> Option<Vec<super::FileHit>> {
    None
}
