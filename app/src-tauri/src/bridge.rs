// Agent bridge: a small local web API so other AI agents on this computer can
// use Mirage's tools, under the same permissions as the built-in assistant.
// Off by default; listens on 127.0.0.1 only; every call needs the token.
//
//   GET  /tools              list of tools with their JSON schemas
//   POST /tools/<name>       run one tool; body is its JSON input
//   Authorization: Bearer <token from Settings › Agent bridge>
use crate::state::settings;
use serde_json::{json, Value};
use std::sync::{Arc, LazyLock, Mutex};

static SERVER: LazyLock<Mutex<Option<(u16, Arc<tiny_http::Server>)>>> = LazyLock::new(|| Mutex::new(None));

pub fn token() -> String {
    let f = crate::settings::data_dir().join("bridge-token");
    if let Ok(t) = std::fs::read_to_string(&f) {
        if t.trim().len() >= 32 {
            return t.trim().to_string();
        }
    }
    let t = format!("{}{}", uuid::Uuid::new_v4().simple(), uuid::Uuid::new_v4().simple());
    let _ = std::fs::write(&f, &t);
    t
}

/// Starts, stops or moves the server to match the settings.
pub fn sync() {
    let st = settings();
    let mut cur = SERVER.lock().unwrap();
    let want = st.bridge.enabled.then_some(st.bridge.port);
    if cur.as_ref().map(|c| c.0) == want {
        return;
    }
    if let Some((_, s)) = cur.take() {
        s.unblock();
    }
    let Some(port) = want else { return };
    match tiny_http::Server::http(("127.0.0.1", port)) {
        Ok(s) => {
            let s = Arc::new(s);
            *cur = Some((port, s.clone()));
            std::thread::Builder::new().name("mirage-bridge".into()).spawn(move || serve(s)).ok();
        }
        Err(e) => crate::state::toast("Agent bridge couldn't start", &format!("Port {port}: {e}")),
    }
}

pub fn stop() {
    if let Some((_, s)) = SERVER.lock().unwrap().take() {
        s.unblock();
    }
}

fn reply(req: tiny_http::Request, code: u16, body: Value) {
    let h = tiny_http::Header::from_bytes(&b"Content-Type"[..], &b"application/json"[..]).unwrap();
    let _ = req.respond(tiny_http::Response::from_string(body.to_string()).with_status_code(code).with_header(h));
}

fn serve(s: Arc<tiny_http::Server>) {
    for mut req in s.incoming_requests() {
        let tok = token();
        let authed = req.headers().iter().any(|h| h.field.equiv("Authorization") && h.value.as_str().trim() == format!("Bearer {tok}"));
        // Browsers can reach localhost too; refuse anything a web page sent.
        let from_page = req.headers().iter().any(|h| h.field.equiv("Origin"));
        if !authed || from_page {
            reply(req, 401, json!({ "error": "Missing or wrong token" }));
            continue;
        }
        let url = req.url().to_string();
        let method = req.method().clone();
        match (method, url.as_str()) {
            (tiny_http::Method::Get, "/tools") => reply(req, 200, json!({ "tools": crate::assistant::tool_list() })),
            (tiny_http::Method::Post, u) if u.starts_with("/tools/") => {
                let name = u.trim_start_matches("/tools/").to_string();
                if !crate::assistant::tool_list().iter().any(|t| t["name"] == name.as_str()) {
                    reply(req, 404, json!({ "error": "No such tool" }));
                    continue;
                }
                let mut body = String::new();
                let _ = std::io::Read::take(req.as_reader(), 1 << 20).read_to_string(&mut body);
                let input: Value = serde_json::from_str(&body).unwrap_or(json!({}));
                // Each call runs on its own thread, so an Ask doesn't hold the others.
                std::thread::spawn(move || {
                    let id = format!("bridge_{}", uuid::Uuid::new_v4().simple());
                    let (_, r) = crate::assistant::gated(&id, &name, &input, "Agent: ");
                    match r {
                        Ok(t) => reply(req, 200, json!({ "ok": true, "result": t })),
                        Err(e) => reply(req, 403, json!({ "ok": false, "error": e })),
                    }
                });
            }
            _ => reply(req, 404, json!({ "error": "Not found" })),
        }
    }
}

use std::io::Read as _;
