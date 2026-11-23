// Data for the widgets and the ambient screen: weather (Open-Meteo, no key),
// the user's calendar from an iCal link, focus time and a daily insight.
use crate::state::{emit, settings, ST};
use chrono::{Datelike, Duration, Local, NaiveDate, NaiveDateTime, TimeZone};
use serde_json::{json, Value};

fn http() -> reqwest::blocking::Client {
    reqwest::blocking::Client::builder().timeout(std::time::Duration::from_secs(15)).user_agent("Mirage/2").build().unwrap()
}

pub fn data() -> Value {
    json!({
        "weather": ST.weather.lock().unwrap().clone(),
        "calendar": ST.calendar.lock().unwrap().clone().unwrap_or(json!({ "events": [], "configured": !settings().calendar_ics.is_empty() })),
        "focus": ST.focus.lock().unwrap().clone(),
        "insight": ST.insight.lock().unwrap().clone().unwrap_or_else(insight_builtin),
    })
}

pub fn refresh_all() {
    std::thread::spawn(|| {
        refresh_weather();
        refresh_calendar();
        refresh_insight();
    });
}

// ---------------- weather ----------------
pub fn geocode(q: &str) -> Result<Value, String> {
    let r: Value = http().get("https://geocoding-api.open-meteo.com/v1/search").query(&[("name", q), ("count", "1"), ("language", "en")]).send().map_err(|e| e.to_string())?.json().map_err(|e| e.to_string())?;
    let x = r["results"].get(0).ok_or(format!("Couldn't find a place called {q}"))?;
    let name = [x["name"].as_str(), x["country"].as_str()].iter().flatten().cloned().collect::<Vec<_>>().join(", ");
    let loc = json!({ "name": name, "lat": x["latitude"], "lon": x["longitude"] });
    std::thread::spawn(|| {
        std::thread::sleep(std::time::Duration::from_millis(300));
        refresh_weather();
    });
    Ok(loc)
}

fn wmo(code: i64) -> &'static str {
    match code {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 | 48 => "Fog",
        51..=57 => "Drizzle",
        61..=67 => "Rain",
        71..=77 => "Snow",
        80..=82 => "Showers",
        85 | 86 => "Snow showers",
        95..=99 => "Thunderstorms",
        _ => "—",
    }
}

pub fn refresh_weather() {
    let Some(loc) = settings().location else {
        *ST.weather.lock().unwrap() = None;
        emit("weather", Value::Null);
        return;
    };
    let r = http()
        .get("https://api.open-meteo.com/v1/forecast")
        .query(&[
            ("latitude", loc.lat.to_string()),
            ("longitude", loc.lon.to_string()),
            ("current", "temperature_2m,weather_code,wind_speed_10m".into()),
            ("hourly", "temperature_2m".into()),
            ("forecast_hours", "12".into()),
            ("timezone", "auto".into()),
        ])
        .send()
        .and_then(|r| r.json::<Value>());
    let Ok(r) = r else { return };
    let offset = r["utc_offset_seconds"].as_i64().unwrap_or(0);
    let hourly: Vec<Value> = r["hourly"]["time"]
        .as_array()
        .map(|ts| {
            ts.iter()
                .zip(r["hourly"]["temperature_2m"].as_array().cloned().unwrap_or_default())
                .filter_map(|(t, v)| {
                    // Open-Meteo gives local wall time at the place; send an absolute time.
                    let nt = NaiveDateTime::parse_from_str(t.as_str()?, "%Y-%m-%dT%H:%M").ok()?;
                    let utc = nt - Duration::seconds(offset);
                    Some(json!({ "t": format!("{}Z", utc.format("%Y-%m-%dT%H:%M:%S")), "temp": v }))
                })
                .collect()
        })
        .unwrap_or_default();
    let w = json!({
        "place": loc.name.split(',').next().unwrap_or(&loc.name).trim(),
        "temp": r["current"]["temperature_2m"],
        "desc": wmo(r["current"]["weather_code"].as_i64().unwrap_or(-1)),
        "wind": r["current"]["wind_speed_10m"],
        "hourly": hourly,
    });
    *ST.weather.lock().unwrap() = Some(w.clone());
    emit("weather", w);
}

// ---------------- calendar ----------------
#[derive(Clone, Debug)]
struct Ev {
    title: String,
    location: String,
    start: chrono::DateTime<Local>,
    end: chrono::DateTime<Local>,
    all_day: bool,
    rrule: Option<String>,
    exdates: Vec<NaiveDate>,
}

fn unfold(t: &str) -> Vec<String> {
    let mut out: Vec<String> = vec![];
    for l in t.replace("\r\n", "\n").split('\n') {
        if (l.starts_with(' ') || l.starts_with('\t')) && !out.is_empty() {
            out.last_mut().unwrap().push_str(&l[1..]);
        } else {
            out.push(l.to_string());
        }
    }
    out
}

fn unescape(s: &str) -> String {
    s.replace("\\n", " ").replace("\\N", " ").replace("\\,", ",").replace("\\;", ";").replace("\\\\", "\\")
}

/// Parses DTSTART/DTEND values: floating, UTC (Z), with TZID (treated as
/// local unless it is UTC; good enough for a glanceable widget) or all-day.
fn parse_dt(params: &str, v: &str) -> Option<(chrono::DateTime<Local>, bool)> {
    let v = v.trim();
    if params.contains("VALUE=DATE") || v.len() == 8 {
        let d = NaiveDate::parse_from_str(v, "%Y%m%d").ok()?;
        return Some((Local.from_local_datetime(&d.and_hms_opt(0, 0, 0)?).earliest()?, true));
    }
    if let Some(z) = v.strip_suffix('Z') {
        let nt = NaiveDateTime::parse_from_str(z, "%Y%m%dT%H%M%S").ok()?;
        return Some((chrono::Utc.from_utc_datetime(&nt).with_timezone(&Local), false));
    }
    let nt = NaiveDateTime::parse_from_str(v, "%Y%m%dT%H%M%S").ok()?;
    Some((Local.from_local_datetime(&nt).earliest()?, false))
}

fn parse_ics(t: &str) -> Vec<Ev> {
    let mut out = vec![];
    let mut cur: Option<Ev> = None;
    let mut has_end = false;
    for l in unfold(t) {
        if l == "BEGIN:VEVENT" {
            let now = Local::now();
            cur = Some(Ev { title: String::new(), location: String::new(), start: now, end: now, all_day: false, rrule: None, exdates: vec![] });
            has_end = false;
            continue;
        }
        if l == "END:VEVENT" {
            if let Some(mut e) = cur.take() {
                if !has_end {
                    e.end = if e.all_day { e.start + Duration::days(1) } else { e.start + Duration::hours(1) };
                }
                out.push(e);
            }
            continue;
        }
        let Some(e) = cur.as_mut() else { continue };
        let Some((k, v)) = l.split_once(':') else { continue };
        let (name, params) = k.split_once(';').unwrap_or((k, ""));
        match name {
            "SUMMARY" => e.title = unescape(v),
            "LOCATION" => e.location = unescape(v),
            "DTSTART" => {
                if let Some((d, ad)) = parse_dt(params, v) {
                    e.start = d;
                    e.all_day = ad;
                }
            }
            "DTEND" => {
                if let Some((d, _)) = parse_dt(params, v) {
                    e.end = d;
                    has_end = true;
                }
            }
            "RRULE" => e.rrule = Some(v.to_string()),
            "EXDATE" => {
                for x in v.split(',') {
                    if let Some((d, _)) = parse_dt(params, x) {
                        e.exdates.push(d.date_naive());
                    }
                }
            }
            _ => {}
        }
    }
    out
}

/// Does a recurring event fall on `day`? Supports DAILY/WEEKLY/MONTHLY/YEARLY
/// with INTERVAL, BYDAY (weekly), UNTIL and COUNT.
fn occurs_on(e: &Ev, day: NaiveDate) -> bool {
    let first = e.start.date_naive();
    if day < first || e.exdates.contains(&day) {
        return false;
    }
    let Some(r) = &e.rrule else { return day == first };
    let mut freq = "";
    let mut interval = 1i64;
    let mut until: Option<NaiveDate> = None;
    let mut count: Option<i64> = None;
    let mut byday: Vec<chrono::Weekday> = vec![];
    for part in r.split(';') {
        let Some((k, v)) = part.split_once('=') else { continue };
        match k {
            "FREQ" => freq = match v { "DAILY" => "d", "WEEKLY" => "w", "MONTHLY" => "m", "YEARLY" => "y", _ => "" },
            "INTERVAL" => interval = v.parse().unwrap_or(1).max(1),
            "UNTIL" => until = parse_dt("", v).map(|d| d.0.date_naive()),
            "COUNT" => count = v.parse().ok(),
            "BYDAY" => {
                byday = v
                    .split(',')
                    .filter_map(|d| {
                        let d = d.trim_start_matches(|c: char| c == '-' || c == '+' || c.is_ascii_digit());
                        Some(match d {
                            "MO" => chrono::Weekday::Mon,
                            "TU" => chrono::Weekday::Tue,
                            "WE" => chrono::Weekday::Wed,
                            "TH" => chrono::Weekday::Thu,
                            "FR" => chrono::Weekday::Fri,
                            "SA" => chrono::Weekday::Sat,
                            "SU" => chrono::Weekday::Sun,
                            _ => return None,
                        })
                    })
                    .collect()
            }
            _ => {}
        }
    }
    if until.map(|u| day > u).unwrap_or(false) {
        return false;
    }
    let days = (day - first).num_days();
    let (hit, n) = match freq {
        "d" => (days % interval == 0, days / interval),
        "w" => {
            let weeks = (day.week(chrono::Weekday::Mon).first_day() - first.week(chrono::Weekday::Mon).first_day()).num_days() / 7;
            let wd = if byday.is_empty() { vec![first.weekday()] } else { byday.clone() };
            (weeks % interval == 0 && wd.contains(&day.weekday()), weeks / interval * wd.len() as i64)
        }
        "m" => {
            let months = (day.year() - first.year()) as i64 * 12 + day.month() as i64 - first.month() as i64;
            (months % interval == 0 && day.day() == first.day(), months / interval)
        }
        "y" => {
            let years = (day.year() - first.year()) as i64;
            (years % interval == 0 && day.month() == first.month() && day.day() == first.day(), years / interval)
        }
        _ => (day == first, 0),
    };
    // COUNT is approximate for weekly rules with several days; close enough for a glance.
    hit && count.map(|c| n < c).unwrap_or(true)
}

pub fn refresh_calendar() {
    let url = settings().calendar_ics.trim().to_string();
    if url.is_empty() {
        let c = json!({ "events": [], "configured": false });
        *ST.calendar.lock().unwrap() = Some(c.clone());
        emit("calendar", c);
        return;
    }
    let url = url.replacen("webcal://", "https://", 1);
    let Ok(t) = http().get(&url).send().and_then(|r| r.error_for_status()).and_then(|r| r.text()) else { return };
    let evs = parse_ics(&t);
    let today = Local::now().date_naive();
    let mut out = vec![];
    for day in [today, today + Duration::days(1)] {
        let mut list: Vec<Value> = evs
            .iter()
            .filter(|e| occurs_on(e, day))
            .filter_map(|e| {
                let shift = day - e.start.date_naive();
                let s = e.start + shift;
                let en = e.end + shift;
                Some(json!({ "title": e.title, "location": e.location, "start": s.to_rfc3339(), "end": en.to_rfc3339(), "all_day": e.all_day }))
            })
            .collect();
        list.sort_by(|a, b| a["start"].as_str().cmp(&b["start"].as_str()));
        out.extend(list);
    }
    let c = json!({ "events": out, "configured": true });
    *ST.calendar.lock().unwrap() = Some(c.clone());
    emit("calendar", c);
}

// ---------------- insight ----------------
const LINES: &[(&str, &str)] = &[
    ("The impediment to action advances action. What stands in the way becomes the way.", "Marcus Aurelius"),
    ("We suffer more often in imagination than in reality.", "Seneca"),
    ("Well begun is half done.", "Aristotle"),
    ("Knowing is not enough; we must apply. Willing is not enough; we must do.", "Goethe"),
    ("It is not that we have a short time to live, but that we waste a lot of it.", "Seneca"),
    ("The secret of getting ahead is getting started.", "Proverb"),
    ("Simplicity is the ultimate sophistication.", "Proverb"),
    ("He who has a why to live can bear almost any how.", "Nietzsche"),
    ("Do the difficult things while they are easy and the great things while they are small.", "Lao Tzu"),
    ("Nothing in life is to be feared, it is only to be understood.", "Marie Curie"),
    ("The best way out is always through.", "Robert Frost"),
    ("What we think, we become.", "The Buddha"),
    ("An unhurried mind sees further. Protect one block of deep work today.", "Mirage"),
    ("Small, finished things beat large, unfinished ones. Ship one today.", "Mirage"),
    ("Write the next step down. A clear next step is half of momentum.", "Mirage"),
    ("Your attention is the scarcest thing on this machine. Spend it on purpose.", "Mirage"),
    ("A walk counts as work when the problem is hard.", "Mirage"),
    ("The man who moves a mountain begins by carrying away small stones.", "Proverb"),
    ("Be like water.", "Lao Tzu, paraphrased"),
    ("Science is organized knowledge. Wisdom is organized life.", "Kant"),
    ("Doubt is the origin of wisdom.", "Al-Ghazali, attributed"),
    ("When you change the way you look at things, the things you look at change.", "Proverb"),
    ("Done is a feature.", "Mirage"),
    ("Quiet the inputs before you make the output.", "Mirage"),
];

fn insight_builtin() -> Value {
    let d = Local::now().ordinal() as usize;
    let (t, s) = LINES[d % LINES.len()];
    json!({ "text": t, "source": s })
}

pub fn refresh_insight() {
    let st = settings();
    let v = if st.ai_insight {
        crate::assistant::one_shot("Write one short, original, practical insight for someone starting their workday at a computer: under 22 words, no quotation marks, no emoji, no hashtags.")
            .ok()
            .map(|t| json!({ "text": t.trim().trim_matches('"'), "source": "Mirage AI" }))
            .unwrap_or_else(insight_builtin)
    } else {
        insight_builtin()
    };
    *ST.insight.lock().unwrap() = Some(v.clone());
    emit("insight", v);
}
