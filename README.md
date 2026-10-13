# Kurzschrift

*Kurzschrift* is German for shorthand. It's a menu bar app for live meeting translation and transcription. It runs entirely
on-device and has no subscription.

| | Translate Meeting (use case 1) | Transcribe Meeting (use case 2) |
|---|---|---|
| Default language | German → English | English |
| Listens to | Meeting audio (Teams, Zoom, browser…) | Meeting audio **and** your microphone |
| Live window | English translation as people speak (German shown smaller) | Transcript as people speak |
| Saved `.md` | German and English for every passage | English |
| Speakers | Speaker 1…n | Your name + Speaker 1…n |

Everything in the table can be changed in **Settings…**.

## Using it

Click the speech-bubble icon in the menu bar:

- **Translate Meeting (German → English)** (⌘1) or **Transcribe Meeting (English)** (⌘2) starts recording.
- **Stop and Save** (⌘S) finishes recording. Speakers are identified and the final transcript is written, which takes a few seconds.
- **Show Live Window** (⌘L) opens the meeting window (below).
- **Export Transcript** saves or copies the transcript as it stands right now, even mid-meeting.
- **Live Update Interval** sets how often the live translation line and the clock refresh (default 500 ms).
- **Open Last Transcript**, **Open Transcripts Folder**, **Settings…**

While recording, the menu bar shows the elapsed time.

### The meeting window

A floating window that stays on top, even over full-screen Teams:

- **Live captions** (bottom strip): what is being said right now, in the original language as the recognizer hears it. When translating, the English is underneath.
- **Transcript** (left): the whole meeting so far. For translations, the original is in grey above the English. It follows new lines; scroll up to read back and it stays put until you press **Jump to latest**.
- **Minutes** (right, when live minutes are on): updated at the interval you choose, with **Update now**, a copy button, the time of the next update and the estimated API cost.
- **Export** (header): save as Markdown or text, or copy to the clipboard. Includes the transcript, translation and current minutes.

Transcripts are saved to `~/Documents/Meeting Transcripts/`, named like
`2026-09-17 14-05 German meeting (translated).md`. During the meeting the file is kept up to date as
a draft. When you stop, it is replaced with the final version, which includes speaker names. Quitting
the app mid-meeting saves first.

### Live minutes (optional, uses an API key)

Settings → **Minutes** turns on minutes for translated and/or transcribed meetings. Choose Anthropic
(default model `claude-opus-5-5`) or OpenAI, paste an API key, and press **Test Key**. That checks
the key and lists the models it can use. During the meeting the minutes are rewritten at the chosen
interval (default 3 minutes). When you stop, final minutes with speaker names are written at the top
of the transcript file. Minutes are written in English for translated meetings and in the meeting's
language otherwise. **Extra instructions** let you steer them (e.g. "focus on my action items").

- **Privacy:** this is the only feature that sends anything off the Mac. The transcript text, not audio, goes to the provider you chose, and only while minutes are on.
- **Cost:** the transcript is sent so that everything already sent is read from the provider's prompt cache. The pane shows an estimate; with `claude-opus-5-5` a one-hour meeting at 3-minute updates costs around a dollar, and `claude-haiku-5-5` costs a few cents.
- **Key storage:** keys are stored in `~/Library/Application Support/Kurzschrift/api-keys.json`, readable only by your user account. They're deliberately not in the keychain: the app is signed ad hoc, so the keychain would ask for your password after every rebuild.

### Start/stop from Shortcuts, Raycast or a hotkey tool

```
open "kurzschrift://start?mode=translate"
open "kurzschrift://start?mode=transcribe"
open "kurzschrift://stop"
open "kurzschrift://toggle?mode=translate"
open "kurzschrift://minutes"     # update the minutes now
```

## How it works (all on this Mac)

- **Meeting audio** comes from a Core Audio process tap (vendored [audiotee](https://github.com/makeusabrew/audiotee), MIT). No BlackHole or virtual device, and it doesn't change your output.
- **Microphone** input uses the system default mic, or the one chosen in Settings. It follows device changes.
- **Speech recognition** uses Apple SpeechAnalyzer (macOS 26), one recognizer per source.
- **Translation** uses the Apple Translation framework, the same engine as the Translate app.
- **Speaker separation** uses [FluidAudio](https://github.com/FluidInference/FluidAudio) (vendored, Apache 2.0). It runs pyannote community-1 + VBx after the meeting on the recorded meeting audio, in a separate helper process. If that ever crashes, you still get the transcript without speaker names. Words are matched to speakers by their timestamps.
- **Echo removal:** with loudspeakers, your microphone also hears the others. Microphone lines that repeat what the meeting audio said at the same moment are dropped.

One-time downloads: Apple's speech and translation models (through macOS) and FluidAudio's speaker
models (21 MB, from Hugging Face, into `~/Library/Application Support/FluidAudio`). Audio is recorded
to `~/Library/Application Support/Kurzschrift/Recordings` during a meeting and deleted afterwards,
unless **Keep audio** is on.

## Accuracy (measured on this Mac)

- **Speakers on a real meeting:** on the 17-minute AMI meeting ES2004a (4 people, real recording), all 4 were found and 99.2% of words sat in turns labeled with the right person. Processing took 16 s.
- **Short meetings:** speaker detection needs a few minutes of each voice. On a 5-minute excerpt of the same meeting it merged two people into one.
- **Undetected speech:** very quiet or very short speech that speaker detection misses is labeled "Unknown speaker".
- **Recognition:** quality is that of Apple's recognizer. Names and jargon get misheard, and a sentence can come out as a statement when it was a question.
- **One language per recording:** if people switch languages mid-meeting, that part comes out garbled.

## Permissions

The first recording asks for **System Audio Recording** and, when the microphone is included,
**Microphone**. The app is signed ad hoc, without a certificate or keychain access, so **macOS asks
again after every rebuild**. If a meeting produces nothing, check System Settings → Privacy & Security →
Screen & System Audio Recording.

Translation needs the languages downloaded: System Settings → General → Language & Region →
Translation Languages. The Translate tab in Settings shows whether they are installed.

## Build

```bash
./build.sh   # swift build (keychain disabled) → Kurzschrift.app → ~/Applications
```

Needs only the Xcode Command Line Tools. Both dependencies are vendored under `Vendor/`, and FluidAudio
is built without its NeMo text-processing binary, so nothing is fetched from GitHub at build time.

## Troubleshooting and re-running

- Log: `~/Library/Logs/Kurzschrift.log`. It records events and segment lengths, never transcript text.
- To rebuild a transcript from kept audio (turn on **Keep audio**), for example after changing your name or layout:

  ```bash
  "~/Applications/Kurzschrift.app/Contents/MacOS/Kurzschrift" --reprocess --mode transcribe \
      --system "… (meeting audio).wav" --mic "… (microphone).wav" --out ~/Desktop [--verbose]
  ```
- Developer hooks:
  - `kurzschrift://start?mode=translate&audio=/path/meeting.wav` replays a recording through the live pipeline instead of capturing audio, which needs no permissions.
  - `kurzschrift://snapshot?path=/tmp/window.png` draws the meeting window's own view to a PNG.
  - `defaults write local.kurzschrift minutesBaseURL.anthropic http://localhost:8765` (or `.openai`) points the minutes requests at a local mock server.
