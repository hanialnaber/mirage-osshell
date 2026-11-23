// Mirage: a desktop layer for Windows and macOS. It sits on top of the
// system (no system files are replaced) and goes away cleanly when you quit.
pub mod apps;
pub mod assistant;
pub mod bridge;
pub mod commands;
pub mod files;
pub mod platform;
pub mod settings;
pub mod state;
pub mod stats;
pub mod views;
pub mod voice;
pub mod wallpapers;
pub mod widgets;

use state::{settings, ST};
use tauri::menu::{Menu, MenuItem, PredefinedMenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::Listener;
use tauri_plugin_global_shortcut::{Code, GlobalShortcutExt, Modifiers, Shortcut, ShortcutState};

/// Gives the screen back exactly as it was: AppBars removed, taskbar shown.
pub fn shutdown() {
    views::remove_appbars();
    platform::set_taskbar_autohide(false);
    bridge::stop();
}

fn shortcuts() -> Vec<(Shortcut, &'static str)> {
    let alt = Some(Modifiers::ALT);
    let ca = Some(Modifiers::CONTROL | Modifiers::ALT);
    let mut v = vec![
        (Shortcut::new(alt, Code::Space), "palette"),
        (Shortcut::new(alt, Code::KeyM), "voice"),
        (Shortcut::new(alt, Code::ArrowUp), "overview"),
        (Shortcut::new(Some(Modifiers::ALT | Modifiers::SHIFT), Code::KeyA), "ambient"),
        (Shortcut::new(ca, Code::ArrowLeft), "snap:left"),
        (Shortcut::new(ca, Code::ArrowRight), "snap:right"),
        (Shortcut::new(ca, Code::ArrowUp), "snap:max"),
        (Shortcut::new(ca, Code::KeyC), "snap:center"),
    ];
    if cfg!(windows) {
        v.extend([
            (Shortcut::new(ca, Code::KeyU), "snap:tl"),
            (Shortcut::new(ca, Code::KeyI), "snap:tr"),
            (Shortcut::new(ca, Code::KeyJ), "snap:bl"),
            (Shortcut::new(ca, Code::KeyK), "snap:br"),
        ]);
    }
    v
}

fn on_shortcut(action: &str) {
    if let Some(layout) = action.strip_prefix("snap:") {
        let l = layout.to_string();
        std::thread::spawn(move || {
            let _ = platform::snap(None, &l);
        });
        return;
    }
    match action {
        "voice" => {
            views::show("voice", None);
        }
        "ambient" => views::show("ambient", None),
        v => views::toggle(v),
    }
}

fn tray(app: &tauri::App) -> tauri::Result<()> {
    let h = app.handle();
    let menu = Menu::with_items(
        h,
        &[
            &MenuItem::with_id(h, "palette", "Command bar", true, None::<&str>)?,
            &MenuItem::with_id(h, "assistant", "Talk to Mirage", true, None::<&str>)?,
            &MenuItem::with_id(h, "overview", "Show all windows", true, None::<&str>)?,
            &MenuItem::with_id(h, "ambient", "Ambient screen", true, None::<&str>)?,
            &PredefinedMenuItem::separator(h)?,
            &MenuItem::with_id(h, "settings", "Settings…", true, None::<&str>)?,
            &MenuItem::with_id(h, "quit", "Quit Mirage", true, None::<&str>)?,
        ],
    )?;
    let mac = cfg!(target_os = "macos");
    let icon = tauri::image::Image::from_bytes(if mac { include_bytes!("../icons/tray-template.png") } else { include_bytes!("../icons/tray.png") })?;
    let t = TrayIconBuilder::with_id("mirage")
        .icon(icon)
        .icon_as_template(mac)
        .tooltip("Mirage")
        .menu(&menu)
        .show_menu_on_left_click(mac)
        .on_menu_event(|app, e| match e.id().as_ref() {
            "quit" => {
                shutdown();
                app.exit(0);
            }
            id => {
                let id = id.to_string();
                let _ = app.run_on_main_thread(move || views::show(&id, None));
            }
        })
        .on_tray_icon_event(|t, e| {
            if let tauri::tray::TrayIconEvent::Click { button: tauri::tray::MouseButton::Left, button_state: tauri::tray::MouseButtonState::Up, .. } = e {
                if !cfg!(target_os = "macos") {
                    let _ = t.app_handle().run_on_main_thread(|| views::toggle("palette"));
                }
            }
        })
        .build(app)?;
    // On a Mac, where the system menu bar stays, Mirage shows CPU next to its icon.
    if mac {
        std::thread::spawn(move || loop {
            std::thread::sleep(std::time::Duration::from_secs(3));
            let cpu = ST.stats.lock().unwrap()["cpu"].as_f64();
            if let Some(c) = cpu {
                let _ = t.set_title(Some(format!("{c:.0}%")));
            }
        });
    }
    Ok(())
}

pub fn run() {
    let smoke = std::env::args().any(|a| a == "--smoke");
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _args, _cwd| {
            let _ = app.run_on_main_thread(|| views::show("palette", None));
        }))
        .plugin(tauri_plugin_autostart::init(tauri_plugin_autostart::MacosLauncher::LaunchAgent, Some(vec!["--autostart"])))
        .plugin(
            tauri_plugin_global_shortcut::Builder::new()
                .with_handler(|_app, sc, ev| {
                    if ev.state() != ShortcutState::Pressed {
                        return;
                    }
                    if let Some((_, a)) = shortcuts().into_iter().find(|(s, _)| s == sc) {
                        on_shortcut(a);
                    }
                })
                .build(),
        )
        .invoke_handler(tauri::generate_handler![
            commands::get_settings,
            commands::set_settings,
            commands::list_apps,
            commands::launch,
            commands::list_windows,
            commands::focus_window,
            commands::close_window,
            commands::snap_window,
            commands::dock_rect,
            commands::search_files,
            commands::open_path,
            commands::reveal_path,
            commands::open_special,
            commands::open_url,
            commands::toggle_view,
            commands::show_view,
            commands::hide_view,
            commands::quit,
            commands::quick_action,
            commands::control_state,
            commands::set_volume,
            commands::wallpaper_info,
            commands::list_wallpapers,
            commands::set_wallpaper,
            commands::next_wallpaper,
            commands::widgets_data,
            commands::geocode,
            commands::assistant_send,
            commands::assistant_answer,
            commands::assistant_reset,
            commands::assistant_test,
            commands::set_api_key,
            commands::api_key_status,
            commands::voice_listen,
            commands::voice_status,
            commands::voice_download,
            commands::bridge_token,
            commands::about,
        ])
        .setup(move |app| {
            let _ = state::APP.set(app.handle().clone());
            #[cfg(target_os = "macos")]
            app.set_activation_policy(tauri::ActivationPolicy::Accessory);

            app.listen_any("view-ready", |e| {
                if let Ok(label) = serde_json::from_str::<String>(e.payload()) {
                    views::on_ready(&label);
                }
            });

            let gs = app.global_shortcut();
            for (sc, name) in shortcuts() {
                if gs.register(sc).is_err() {
                    eprintln!("mirage: shortcut for {name} is taken by another app");
                }
            }
            if let Err(e) = tray(app) {
                eprintln!("mirage: tray: {e}");
            }
            {
                use tauri_plugin_autostart::ManagerExt;
                let al = app.autolaunch();
                let want = settings().autostart && !smoke;
                if want != al.is_enabled().unwrap_or(false) {
                    let _ = if want { al.enable() } else { al.disable() };
                }
            }

            let st = settings();
            for v in ["bar", "dock", "widgets"] {
                let on = match v {
                    "bar" => st.surfaces.bar,
                    "dock" => st.surfaces.dock,
                    _ => st.surfaces.widgets,
                };
                if on {
                    views::create(v);
                }
            }
            views::create("palette");
            platform::set_taskbar_autohide(st.hide_taskbar && cfg!(windows) && st.surfaces.dock);
            if !st.onboarded && !smoke {
                views::show("settings", None);
            }
            if !st.wallpaper.is_empty() {
                // Re-apply in case Windows or macOS reset it after an update.
                let id = st.wallpaper.clone();
                std::thread::spawn(move || {
                    let _ = wallpapers::set(&id);
                });
            }

            stats::start();
            files::warm();
            widgets::refresh_all();
            voice::start();
            bridge::sync();

            if smoke {
                smoke_test(app.handle().clone());
            }
            Ok(())
        })
        .build(tauri::generate_context!())
        .expect("error while starting Mirage");

    app.run(|_app, e| {
        if let tauri::RunEvent::Exit = e {
            shutdown();
        }
        if let tauri::RunEvent::ExitRequested { api, code, .. } = e {
            // Closing the last panel must not quit Mirage; only Quit does.
            if code.is_none() {
                api.prevent_exit();
            }
        }
    });
}

/// `mirage --smoke`: starts every surface, waits for them to settle, writes
/// a report (memory, CPU, which views mounted) and quits. Used by CI.
fn smoke_test(app: tauri::AppHandle) {
    std::thread::spawn(move || {
        std::thread::sleep(std::time::Duration::from_secs(6));
        for v in ["control", "overview", "settings"] {
            let v = v.to_string();
            let _ = app.run_on_main_thread(move || views::show(&v, None));
            std::thread::sleep(std::time::Duration::from_millis(800));
        }
        std::thread::sleep(std::time::Duration::from_secs(20));
        let mut samples = vec![];
        for _ in 0..10 {
            std::thread::sleep(std::time::Duration::from_millis(1500));
            samples.push(ST.stats.lock().unwrap().clone());
        }
        let mem = samples.iter().filter_map(|s| s["mirage_mem"].as_u64()).max().unwrap_or(0);
        let cpu: Vec<f64> = samples.iter().filter_map(|s| s["mirage_cpu"].as_f64()).collect();
        let avg = if cpu.is_empty() { 0.0 } else { cpu.iter().sum::<f64>() / cpu.len() as f64 };
        let mounted: Vec<String> = views::ALL.iter().filter(|v| views::get(v).is_some()).map(|v| v.to_string()).collect();
        let report = serde_json::json!({
            "os": std::env::consts::OS,
            "version": app.package_info().version.to_string(),
            "mirage_mem_mb": mem as f64 / 1_048_576.0,
            "mirage_cpu_avg_pct": avg,
            "views": mounted,
            "apps_found": apps::list().len(),
            "wallpapers": wallpapers::list().len(),
        });
        let out = std::env::var("MIRAGE_SMOKE_OUT").unwrap_or_else(|_| "mirage-smoke.json".into());
        let _ = std::fs::write(&out, serde_json::to_string_pretty(&report).unwrap_or_default());
        println!("{report}");
        shutdown();
        app.exit(0);
    });
}
