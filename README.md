# Cosmo AI Assistant

A Windows tray app (C#, compiled directly via `csc.exe` — no `.csproj`/`.sln`) that
listens for a wake word ("cosmo ...") to run voice commands, plus two independent
hold-to-talk hotkeys for dictation and for scheduling reminders. All speech recognition
is local — wake-word matching via Windows' built-in `System.Speech.Recognition`, and
all free-form transcription (dictation, reminders) via a bundled `whisper.cpp` server.
No cloud services, no accounts, no external dependencies beyond what's documented below.

## Features

- **Wake-word commands** (`commands.json`): "cosmo open chrome", "cosmo lock the
  computer", "cosmo look up \<query\>", etc. — fully customizable, see the format
  reference below. Off by default; see Settings.
- **Hold-`~` (tilde) push-to-talk dictation**: hold, speak, release — transcribed via
  Whisper and typed into whatever's focused. Works regardless of the wake-word toggle.
- **Gaming mode**: adds Tab as a second dictation key (since many games bind `~` to
  their console). Alt+Tab still works normally — only plain Tab triggers dictation.
- **Hold-Insert push-to-talk reminders**: hold Insert, speak a reminder in plain
  English ("remind me in three hours and 25 minutes to take out the trash"), release.
  No wake word needed — holding the key is the trigger. Delivered as a local Windows
  tray notification at the scheduled time (see Reminders below). On by default.
- **Confirmation ding**: a synthesized tone plays after any successful voice *command*
  (not dictation, which fires too often for a per-utterance sound to make sense).

## Reminders

Reminders are entirely local — no Discord, no companion server, no phone app. Hold
Insert, say something like "remind me in 20 minutes to check the oven," release. Cosmo
parses the spoken duration (supports digit or word numbers, combinations like "twenty
five minutes," "half an hour," "a quarter of an hour," and finds the duration anywhere
in the sentence — before, after, or around the message) and schedules an in-memory
timer. When it fires, you get a Windows tray balloon notification and the confirmation
ding — no Discord/SMS/email delivery, since that would require infrastructure this
project doesn't ship with.

Pending reminders are persisted to `reminders.json` (next to `commands.json`) and
re-armed on startup, so closing Cosmo (or restarting your PC) with a reminder still
pending doesn't lose it — it'll still fire once Cosmo is running again, either at the
original time or immediately if that time already passed while it wasn't running.

## Architecture

- `Program.cs` — tray icon, menu, `SpeechRecognitionEngine` setup (wake-word grammar),
  wiring for both hold-to-talk hotkeys.
- `Hotkey.cs` / `RemindHotkey.cs` — independent global low-level keyboard hooks
  (`WH_KEYBOARD_LL`), one scoped to `~`/Tab (dictation), one to Insert (reminders).
  Each swallows its key while enabled so it never leaks into the focused app.
- `WaveInRecorder.cs` / `WavWriter.cs` — raw mic capture via `winmm.dll` for
  push-to-talk audio.
- `WhisperEngine.cs` — launches `whisper-server.exe` as a child process at startup,
  POSTs captured audio to its local `/inference` endpoint.
- `ReminderParser.cs` — parses loose spoken English durations out of a free-form
  transcript and returns the leftover text as the reminder message.
- `Actions.cs` — executes fixed commands (`process`/`system`/`claude-terminal`),
  search, dictation typing, and reminder scheduling/persistence/delivery.
- `Config.cs` — `commands.json`/`settings.json` data-contract types.
- `Tone.cs` — synthesizes short in-memory tones (PTT click, confirmation ding) rather
  than shipping `.wav` assets or depending on Windows system sounds.

## Building

No `.csproj`/`.sln` — run `build.ps1`, which invokes `csc.exe` directly with an
explicit file list. **Any new `.cs` file must be added to that list manually** or it
silently fails to compile in (no error — just missing functionality).

You'll need a `whisper.cpp` Windows server build (not included — ~600MB with models,
too large for this repo). Fetch a release from the
[whisper.cpp releases page](https://github.com/ggml-org/whisper.cpp/releases) and lay
it out as `whisper/` (a sibling of `bin/`):
- `whisper-server.exe` + the minimal DLL set: `ggml.dll`, `ggml-base.dll`,
  `ggml-cpu-*.dll` (all CPU-dispatch variants for your target machines), `whisper.dll`.
- `models/ggml-base.en.bin` — download via whisper.cpp's model-download script or
  directly from Hugging Face. `base.en` trades a little accuracy for much lower
  latency than `small.en`, which matters for a push-to-talk tool used mid-conversation.

## Configuring commands

Edit `commands.json`. Each entry has a `phrase` (spoken after the wake word) and a
`type`:
- `process` — launch an exe or URI (`path`, optional `args`; both support environment
  variable expansion like `%LOCALAPPDATA%`).
- `system` — currently just `action: "lock"` (locks the workstation).
- `claude-terminal` — opens a terminal running `claude` in a given directory (`path`).
- `search` — wildcard: everything spoken after the phrase becomes a URL-encoded query
  against `urlTemplate`.
- `dictate` — wildcard: everything spoken after the phrase gets typed via `SendInput`.

## Settings reference

`settings.json` (tray-toggle state, all persisted, all default `false` except where
noted):
- `commandsEnabled` — whether the wake-word `SpeechRecognitionEngine` runs at all.
  Off by default; hold-to-talk dictation and reminders work regardless.
- `notificationsEnabled` — whether routine "here's what I did" balloon tips show.
  Failures and reminder deliveries always show regardless of this.
- `debugNotifications` — extra balloons for below-threshold/rejected recognitions.
- `gamingModeEnabled` — adds Tab as a second dictation trigger key.
- `remindHotkeyDisabled` — **stored inverted** (false = enabled) so the hold-Insert
  reminder hotkey defaults on even for a `settings.json` that predates the field.

## Known limitations

- No single-instance enforcement yet — nothing stops two copies of the exe running at
  once (each would install its own hotkeys/recognizer).
- Concurrent hold-to-talk (both `~` and Insert held at once) sends two simultaneous
  requests to the same single `whisper-server.exe` instance — not yet verified safe
  under concurrency.
- Reminders are local-only by design — no remote delivery (push notification to a
  phone, Discord/Slack message, email) is built in. If you want that, `Actions.cs`'s
  `ReminderDue` event is the hook point to wire up your own delivery mechanism.
