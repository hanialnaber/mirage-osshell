// Stubs so the crate builds on other systems (Linux is not a target yet).
use super::{AppEntry, FileHit, WinInfo};
use std::path::PathBuf;
pub fn list_apps() -> Vec<AppEntry> { vec![] }
pub fn launch(p: &str) -> Result<(), String> { std::process::Command::new("xdg-open").arg(p).spawn().map(|_| ()).map_err(|e| e.to_string()) }
pub fn open_path(p: &str) -> Result<(), String> { launch(p) }
pub fn open_url(p: &str) -> Result<(), String> { launch(p) }
pub fn reveal(p: &str) -> Result<(), String> { launch(p) }
pub fn open_special(_w: &str) -> Result<(), String> { Ok(()) }
pub fn trash(_p: &str) -> Result<(), String> { Err("Not supported".into()) }
pub fn icon_png(_p: &str) -> Option<Vec<u8>> { None }
pub fn list_windows(_t: bool, _p: u32) -> Vec<WinInfo> { vec![] }
pub fn foreground(_p: u32) -> Option<(String, String)> { None }
pub fn focus_window(_i: &str) -> Result<(), String> { Ok(()) }
pub fn close_window(_i: &str) -> Result<(), String> { Ok(()) }
pub fn snap(_i: Option<&str>, _l: &str) -> Result<(), String> { Ok(()) }
pub fn idle_seconds() -> u64 { 0 }
pub fn busy_fullscreen() -> bool { false }
pub fn set_wallpaper(_p: &str) -> Result<(), String> { Ok(()) }
pub fn current_wallpaper() -> Option<PathBuf> { None }
pub fn volume() -> Option<(f32, bool)> { None }
pub fn set_volume(_v: f32) -> Result<(), String> { Ok(()) }
pub fn toggle_mute() -> Result<(), String> { Ok(()) }
pub fn is_dark() -> bool { true }
pub fn quick(_a: &str) -> Result<(), String> { Ok(()) }
pub struct Gpu;
impl Gpu { pub fn new() -> Option<Gpu> { None } pub fn read(&mut self) -> Option<f32> { None } }
pub fn voices() -> Vec<String> { vec![] }
pub fn speak(_t: &str, _v: &str) -> Option<std::process::Child> { None }
pub fn search_files_native(_q: &str, _l: usize) -> Option<Vec<FileHit>> { None }
pub fn appbar_set(_h: isize, _e: &str, _t: i32) -> Option<(i32, i32, i32, i32)> { None }
pub fn appbar_remove(_h: isize) {}
pub fn set_taskbar_autohide(_o: bool) {}
pub fn round_corners(_h: isize) {}
pub fn tool_window(_h: isize) {}
pub fn work_area() -> Option<(i32, i32, i32, i32)> { None }
pub fn private_mem(_pid: u32) -> Option<u64> { None }
