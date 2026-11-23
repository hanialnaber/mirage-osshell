<p align="center"><img src="assets/mirage-256.png" width="120" alt="Mirage icon"></p>
<h1 align="center">Mirage</h1>
<p align="center">A calm, fast desktop layer for Windows and macOS.<br>
A live top bar, a dock, one command bar, desktop widgets, window overview, original 4K wallpapers,
an ambient screen, and Mirage: an AI assistant you can talk to.</p>
<p align="center"><a href="https://hani-alnaber.com/osshell">Website</a> · <a href="https://github.com/hanialnaber/mirage-osshell/releases/latest">Download</a></p>

---

Mirage sits on top of your system. It doesn't replace system files or change how
Windows or macOS boots, and quitting it gives you your normal desktop back.
It installs just for you, with no admin rights.

## Install

| | Download | First open |
|---|---|---|
| **Windows 10 / 11** | `Mirage-Windows-Setup.exe` from [Releases](https://github.com/hanialnaber/mirage-osshell/releases/latest) | SmartScreen says "Windows protected your PC". Click **More info**, then **Run anyway**. |
| **macOS 11 or later** (Apple silicon and Intel) | `Mirage-macOS.dmg`, then drag Mirage to Applications | macOS says it can't check the app. Open **System Settings > Privacy & Security** and click **Open Anyway**. It asks again after each new version. |

Both warnings appear because the builds aren't code-signed (that costs a yearly fee).
The Mac version has had less testing than the Windows one, so tell us what you find.

## What's in it

- **Top bar** with the active app, date and time, live CPU, memory, GPU and network, volume, battery and a control center. Off by default on a Mac, where the menu bar stays.
- **Dock** with your pinned apps, running apps, Downloads and Trash, magnifying under the pointer. Optional on a Mac.
- **Command bar** (`Alt+Space`, `⌥ Space` on a Mac): apps, files, actions, a calculator, and the assistant.
- **Mirage, the assistant**: ask in words or say "Hey Mirage". It opens apps, finds and moves files, writes notes, drafts mail, changes the wallpaper or volume and more, shows every step, and asks first before anything you set to Ask. Each tool can be set to Allow, Ask or Deny.
- **Your AI, your key**: Claude (default), OpenAI, Gemini, or a local model through Ollama. Keys stay in the system keychain.
- **Voice on your computer**: the wake word and speech-to-text run locally with whisper.cpp (models download once, about 225 MB). Replies are spoken with your system's voice.
- **Overview and snapping** (`Alt+↑`, `Ctrl+Alt+arrows`), **widgets** (clock, today's events from your calendar link, weather, system, focus timer, daily insight), **notifications**, **original 4K wallpapers** with a line each, and an **ambient screen** when you step away.
- **Tinted icons**: app icons in navy and gold, on by default, with a switch back to original colors.
- **Agent bridge** (off by default): other AI agents on your computer can use Mirage's tools through a local, token-protected API, under the same Allow/Ask/Deny rules.

## Shortcuts

| Windows | Mac | |
|---|---|---|
| `Alt+Space` | `⌥ Space` | Command bar |
| `Alt+M` | `⌥ M` | Talk to Mirage |
| `Alt+↑` | `⌥ ↑` | Show all windows |
| `Alt+Shift+A` | `⌥ ⇧ A` | Ambient screen |
| `Ctrl+Alt+←/→/↑/C` | `⌃ ⌥ ←/→/↑/C` | Snap left, right, maximize, center |
| `Ctrl+Alt+U/I/J/K` | | Snap to a quarter |

## Build it yourself

```
cd app/src-tauri
npx @tauri-apps/cli@2 build
```

Needs Rust, Node 22, CMake and a C compiler (and LLVM on Windows) for the on-device voice.
`--no-default-features` builds without voice. CI builds and smoke-tests both platforms on
every push (`.github/workflows/build.yml`), and the **Release** workflow publishes both downloads.

The wallpapers are drawn from math by `app/tools/wallpapers.py` (no photos or stock),
and the site's videos are rendered from the real interface by `app/tools/media/render.mjs`.

## License

MIT
