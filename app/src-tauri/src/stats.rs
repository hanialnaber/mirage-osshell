// Background loops: system stats, the foreground app, the window list, idle
// detection for the ambient screen, the focus timer and dock click-through.
// Everything is sampled on a slow clock so Mirage itself stays near 0% CPU.
use crate::platform;
use crate::state::{emit, save_focus, settings, toast, ST};
use crate::views;
use serde_json::json;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use sysinfo::{Networks, Pid, ProcessRefreshKind, ProcessesToUpdate, System};

static AMBIENT_FIRED: AtomicBool = AtomicBool::new(false);
pub fn idle_reset() {
    AMBIENT_FIRED.store(true, Ordering::Relaxed);
}

pub fn start() {
    std::thread::Builder::new().name("mirage-stats".into()).spawn(stats_loop).ok();
    std::thread::Builder::new().name("mirage-dock".into()).spawn(dock_loop).ok();
    std::thread::Builder::new()
        .name("mirage-apps".into())
        .spawn(|| loop {
            crate::apps::refresh();
            std::thread::sleep(Duration::from_secs(600));
        })
        .ok();
}

fn stats_loop() {
    let own = std::process::id();
    let mut sys = System::new();
    let mut nets = Networks::new_with_refreshed_list();
    let mut gpu = platform::Gpu::new();
    let mut battery = None;
    let mut vol: Option<(f32, bool)> = None;
    let mut gpu_v: Option<f32> = None;
    let mut mine: Vec<Pid> = vec![Pid::from_u32(own)];
    let mut last_fg = (String::new(), String::new());
    let mut last_wins = String::new();
    let mut last = Instant::now();
    let ncpu = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(1) as f32;
    let mut tick: u64 = 0;
    sys.refresh_cpu_usage();
    loop {
        std::thread::sleep(Duration::from_millis(1500));
        tick += 1;
        let dt = last.elapsed().as_secs_f64().max(0.1);
        last = Instant::now();
        sys.refresh_cpu_usage();
        sys.refresh_memory();
        nets.refresh(true);
        let (mut down, mut up) = (0u64, 0u64);
        for (_, n) in nets.iter() {
            down += n.received();
            up += n.transmitted();
        }
        // Mirage and its WebView helper processes, found every 30 s.
        if tick % 20 == 1 {
            sys.refresh_processes_specifics(ProcessesToUpdate::All, true, ProcessRefreshKind::nothing());
            let root = Pid::from_u32(own);
            let mut set = vec![root];
            let mut grew = true;
            while grew {
                grew = false;
                for (pid, p) in sys.processes() {
                    if !set.contains(pid) && p.parent().map(|pp| set.contains(&pp)).unwrap_or(false) {
                        set.push(*pid);
                        grew = true;
                    }
                }
            }
            mine = set;
        }
        sys.refresh_processes_specifics(ProcessesToUpdate::Some(&mine), true, ProcessRefreshKind::nothing().with_cpu().with_memory());
        let (mut mm, mut mc) = (0u64, 0f32);
        for p in &mine {
            if let Some(p) = sys.process(*p) {
                mm += p.memory();
                mc += p.cpu_usage();
            }
        }
        if tick % 20 == 1 {
            battery = read_battery();
        }
        if tick % 3 == 1 {
            vol = platform::volume();
            if let Some(g) = gpu.as_mut() {
                gpu_v = g.read();
            }
        }
        let total = sys.total_memory().max(1);
        let used = sys.used_memory();
        let payload = json!({
            "cpu": sys.global_cpu_usage(),
            "mem": used as f64 * 100.0 / total as f64,
            "mem_used": used, "mem_total": total,
            "gpu": gpu_v,
            "down": (down as f64 / dt) as u64, "up": (up as f64 / dt) as u64,
            "volume": vol.map(|v| v.0), "muted": vol.map(|v| v.1).unwrap_or(false),
            "battery": battery,
            "mirage_mem": mm, "mirage_cpu": mc / ncpu,
        });
        *ST.stats.lock().unwrap() = payload.clone();
        emit("stats", payload);

        if let Some(fg) = platform::foreground(own) {
            if fg != last_fg {
                emit("foreground", json!({ "app": fg.0, "title": fg.1 }));
                last_fg = fg;
            }
        }
        if tick % 2 == 0 {
            let wins = platform::list_windows(false, own);
            let key: String = wins.iter().map(|w| format!("{}|{}|", w.id, w.app)).collect();
            if key != last_wins {
                last_wins = key;
                let wins = crate::apps::with_icons(wins);
                *ST.windows.write().unwrap() = wins.clone();
                emit("windows", wins);
            }
        }
        idle_check();
        focus_check();
        if tick % 400 == 0 {
            crate::widgets::refresh_all();
        }
    }
}

fn read_battery() -> Option<serde_json::Value> {
    let m = starship_battery::Manager::new().ok()?;
    let b = m.batteries().ok()?.flatten().next()?;
    let pct = b.state_of_charge().get::<starship_battery::units::ratio::percent>();
    let charging = matches!(b.state(), starship_battery::State::Charging | starship_battery::State::Full);
    Some(json!({ "pct": pct, "charging": charging }))
}

fn idle_check() {
    let st = settings();
    let idle = platform::idle_seconds();
    let limit = st.ambient_minutes.max(1) as u64 * 60;
    if idle < limit {
        AMBIENT_FIRED.store(false, Ordering::Relaxed);
        return;
    }
    if !st.surfaces.ambient || AMBIENT_FIRED.load(Ordering::Relaxed) || platform::busy_fullscreen() {
        return;
    }
    AMBIENT_FIRED.store(true, Ordering::Relaxed);
    let _ = crate::state::app().run_on_main_thread(|| views::show("ambient", None));
}

fn focus_check() {
    let mut f = ST.focus.lock().unwrap();
    let today = chrono::Local::now().format("%Y-%m-%d").to_string();
    if f.day != today {
        f.day = today;
        f.today_minutes = 0.0;
    }
    if !f.on {
        return;
    }
    let ends = f.ends_at.as_deref().and_then(|e| chrono::DateTime::parse_from_rfc3339(e).ok());
    if let Some(e) = ends {
        if chrono::Local::now() >= e {
            stop_focus_locked(&mut f);
            let snap = f.clone();
            drop(f);
            emit("focus", snap);
            toast("Focus session done", "Nice work. Take a short break.");
        }
    }
}

pub fn start_focus(minutes: i64) {
    let mut f = ST.focus.lock().unwrap();
    let now = chrono::Local::now();
    f.on = true;
    f.started_at = Some(now.to_rfc3339());
    f.ends_at = Some((now + chrono::Duration::minutes(minutes)).to_rfc3339());
    save_focus(&f);
    emit("focus", f.clone());
}
fn stop_focus_locked(f: &mut crate::state::Focus) {
    if let Some(s) = f.started_at.as_deref().and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok()) {
        let mins = (chrono::Local::now().fixed_offset() - s).num_seconds() as f64 / 60.0;
        f.today_minutes += mins.max(0.0);
    }
    f.on = false;
    f.started_at = None;
    f.ends_at = None;
    save_focus(f);
}
pub fn stop_focus() {
    let mut f = ST.focus.lock().unwrap();
    if f.on {
        stop_focus_locked(&mut f);
    }
    emit("focus", f.clone());
}

/// The dock window spans the screen width; only the dock itself takes clicks.
fn dock_loop() {
    let mut passing = true;
    loop {
        std::thread::sleep(Duration::from_millis(60));
        let Some(w) = views::get("dock") else {
            std::thread::sleep(Duration::from_millis(500));
            continue;
        };
        let (Ok(cur), Ok(pos), Ok(sf)) = (crate::state::app().cursor_position(), w.outer_position(), w.scale_factor()) else { continue };
        let (x, y, rw, rh) = *ST.dock_rect.lock().unwrap();
        let lx = (cur.x - pos.x as f64) / sf;
        let ly = (cur.y - pos.y as f64) / sf;
        let inside = lx >= x && lx <= x + rw && ly >= y && ly <= y + rh;
        if inside == passing {
            passing = !inside;
            let _ = w.set_ignore_cursor_events(passing);
        }
    }
}
