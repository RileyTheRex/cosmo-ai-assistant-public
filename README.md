# 🌌 Cosmo AI Assistant

**Your voice, your PC, zero cloud.** Cosmo is a lightweight Windows tray app that turns a wake word into real actions — opening apps, locking your machine, dictating text into any window, and setting reminders — all through **100% local speech recognition**. No accounts, no API keys, no data ever leaving your computer.

> Say *"cosmo lock the computer"* and it's locked. Hold a key, whisper a reminder, let go — done. That's the whole interaction model.

![Platform](https://img.shields.io/badge/platform-Windows-0078D6?style=flat-square&logo=windows)
![Language](https://img.shields.io/badge/language-C%23-239120?style=flat-square&logo=csharp)
![Speech](https://img.shields.io/badge/speech-100%25%20local-success?style=flat-square)
![Cloud](https://img.shields.io/badge/cloud%20services-none-lightgrey?style=flat-square)

---

## ✨ What it does

- 🗣️ **Wake-word commands** — say `"cosmo ..."` to trigger fully customizable commands defined in `commands.json` (open an app, lock the PC, run a search, launch a terminal, and more)
- ⌨️ **Hold-to-talk dictation** — hold `~` (tilde), speak, release — your words get transcribed and typed straight into whatever's focused
- 🎮 **Gaming mode** — adds `Tab` as a second dictation key for games that bind `~` to their console (Alt+Tab still works normally)
- ⏰ **Hold-to-talk reminders** — hold `Insert`, say something like *"remind me in 20 minutes to check the oven,"* release — no wake word needed, and it's on by default
- 🔔 **Confirmation ding** — a synthesized tone confirms every successful voice command (dictation stays silent since it fires too often for that)
- 💾 **Persistent reminders** — pending reminders survive an app restart or a full reboot; they re-arm on startup and still fire when Cosmo comes back
- 🔒 **Fully local** — wake-word matching runs on Windows' built-in `System.Speech.Recognition`, and all free-form transcription runs through a bundled `whisper.cpp` server. Nothing is sent anywhere.

---

## ⏰ Reminders

Reminders are entirely local — no Discord, no companion server, no phone app. Hold `Insert`, say something like *"remind me in 20 minutes to check the oven,"* release. Cosmo parses the spoken duration (digits or words, combinations like "twenty five minutes," "half an hour," "a quarter of an hour," found anywhere in the sentence — before, after, or around the message) and schedules an in-memory timer. When it fires, you get a Windows tray balloon notification plus the confirmation ding.

Pending reminders are persisted to `reminders.json` (next to `commands.json`) and re-armed on startup, so closing Cosmo — or restarting your PC — with a reminder still pending doesn't lose it. It fires once Cosmo is running again, either at the original time or immediately if that time already passed while it wasn't running.

---

## 🏗️ Architecture

| File | Responsibility |
|---|---|
| `Program.cs` | Tray icon, menu, `SpeechRecognitionEngine` setup (wake-word grammar), wiring for both hold-to-talk hotkeys |
| `Hotkey.cs` / `RemindHotkey.cs` | Independent global low-level keyboard hooks (`WH_KEYBOARD_LL`) — one scoped to `~`/`Tab` (dictation), one to `Insert` (reminders). Each swallows its key while enabled so it never leaks into the focused app |
| `WaveInRecorder.cs` / `WavWriter.cs` | Raw mic capture via `winmm.dll` for push-to-talk audio |
| `WhisperEngine.cs` | Launches `whisper-server.exe` as a child process at startup, POSTs captured audio to its local `/inference` endpoint |
| `ReminderParser.cs` | Parses loose spoken English durations out of a free-form transcript and returns the leftover text as the reminder message |
| `Actions.cs` | Executes fixed commands (process/system/claude-terminal), search, dictation typing, and reminder scheduling/persistence/delivery |
| `Config.cs` | `commands.json`/`settings.json` data-contract types |
| `Tone.cs` | Synthesizes short in-memory tones (PTT click, confirmation ding) rather than shipping `.wav` assets or depending on Windows system sounds |

---

## 🚀 Quick self-install

Two scripts, run in order, get you from a fresh clone to a working `bin\CosmoAIAssistant.exe`:

```powershell
.\build.ps1           # compiles the app via csc.exe -> bin\CosmoAIAssistant.exe
.\install-whisper.ps1 # downloads whisper.cpp's server + the base.en model into whisper\
```

| Script | What it does |
|---|---|
| `build.ps1` | Invokes `csc.exe` directly against the fixed file list (see [Building](#-building)) to produce `bin\CosmoAIAssistant.exe` |
| `install-whisper.ps1` | Downloads a prebuilt whisper.cpp CPU x64 server release, copies `whisper-server.exe` + the minimal DLL set into `whisper\`, and downloads `ggml-base.en.bin` into `whisper\models\` — reproduces the manual layout described below without a 600MB repo checkout |

Optional, once both scripts have run:

```powershell
.\install-startup.ps1 # adds a shortcut so Cosmo launches at Windows sign-in
```

`install-whisper.ps1` takes two optional params if you want a different whisper.cpp release or model size:

```powershell
.\install-whisper.ps1 -Tag b5130 -Model ggml-base.en.bin
```

Then launch `bin\CosmoAIAssistant.exe` — it starts `whisper-server.exe` as a child process automatically (see [`WhisperEngine.cs`](WhisperEngine.cs)) and adds a tray icon.

---

## 🔧 Building

No `.csproj`/`.sln` — run `build.ps1`, which invokes `csc.exe` directly with an explicit file list.

> ⚠️ **Heads up:** any new `.cs` file must be added to that list manually, or it silently fails to compile in — no error, just missing functionality.

You'll need a **whisper.cpp Windows server build** (not included — ~600MB with models, too large for this repo). Run `install-whisper.ps1` (see [Quick self-install](#-quick-self-install) above) to fetch one automatically, or lay it out by hand as `whisper/` (a sibling of `bin/`):

- `whisper-server.exe` + the minimal DLL set: `ggml.dll`, `ggml-base.dll`, `ggml-cpu-*.dll` (all CPU-dispatch variants for your target machines), `whisper.dll`
- `models/ggml-base.en.bin` — download via whisper.cpp's model-download script or directly from Hugging Face. `base.en` trades a little accuracy for much lower latency than `small.en`, which matters for a push-to-talk tool used mid-conversation

---

## ⚙️ Configuring commands

Edit `commands.json`. Each entry has a `phrase` (spoken after the wake word) and a `type`:

- **`process`** — launch an exe or URI (`path`, optional `args`; both support environment variable expansion like `%LOCALAPPDATA%`)
- **`system`** — currently just `action: "lock"` (locks the workstation)
- **`claude-terminal`** — opens a terminal running `claude` in a given directory (`path`)
- **`search`** — wildcard: everything spoken after the phrase becomes a URL-encoded query against `urlTemplate`
- **`dictate`** — wildcard: everything spoken after the phrase gets typed via `SendInput`

---

## 🎛️ Settings reference

`settings.json` (tray-toggle state, all persisted, all default `false` except where noted):

| Setting | Description |
|---|---|
| `commandsEnabled` | Whether the wake-word `SpeechRecognitionEngine` runs at all. Off by default; hold-to-talk dictation and reminders work regardless |
| `notificationsEnabled` | Whether routine "here's what I did" balloon tips show. Failures and reminder deliveries always show regardless of this |
| `debugNotifications` | Extra balloons for below-threshold/rejected recognitions |
| `gamingModeEnabled` | Adds `Tab` as a second dictation trigger key |
| `remindHotkeyDisabled` | Stored inverted (`false` = enabled) so the hold-Insert reminder hotkey defaults on even for a `settings.json` that predates the field |

---

## 🚧 Known limitations

- No single-instance enforcement yet — nothing stops two copies of the exe running at once (each would install its own hotkeys/recognizer)
- Concurrent hold-to-talk (both `~` and `Insert` held at once) sends two simultaneous requests to the same single `whisper-server.exe` instance — not yet verified safe under concurrency
- Reminders are local-only by design — no remote delivery (push notification to a phone, Discord/Slack message, email) is built in. If you want that, `Actions.cs`'s `ReminderDue` event is the hook point to wire up your own delivery mechanism
