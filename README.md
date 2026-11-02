# Kurzschrift

*Kurzschrift* is German for shorthand. It's a free macOS menu bar app for live meeting translation
and transcription. Speech recognition, translation and speaker separation run on your Mac; only the
optional AI minutes and questions use an online API.

| | Translate Meeting | Transcribe Meeting |
|---|---|---|
| Default language | German → English | English |
| Listens to | Meeting audio (Teams, Zoom, browser…) | Meeting audio **and** your microphone |
| Live window | English translation as people speak (German shown smaller) | Transcript as people speak |
| Saved `.md` | German and English for every passage | English |
| Speakers | Speaker 1…n | Your name + Speaker 1…n |

Everything in the table can be changed in **Settings…**.

## Requirements and installing

- macOS 26 or later (it uses Apple's SpeechAnalyzer). Developed and tested on an Apple Silicon Mac.
- The Xcode Command Line Tools (`xcode-select --install`), with Swift 6.2 or later.

There is no prebuilt download. Build and install it from source:

```bash
git clone https://github.com/havurgiray/kurzschrift.git
cd kurzschrift
./build.sh                       # builds Kurzschrift.app and installs it into ~/Applications
open ~/Applications/Kurzschrift.app
```

The speech-bubble icon appears in the menu bar. The first recording asks for the permissions
described under [Permissions](#permissions).

Recording other people may need their consent where you live. Let the people in your meetings know
that you transcribe them.

## Using it

Click the speech-bubble icon in the menu bar:

- **Translate Meeting (German → English)** (⌘1) or **Transcribe Meeting (English)** (⌘2) starts recording.
- **Stop and Save** (⌘S) finishes recording. Speakers are identified and the final transcript is written, which takes a few seconds.
- **Bookmark This Moment** (⌘B) marks "this was important" with a ⭐ in the transcript and the minutes.
- **Show Live Window** (⌘L) opens the meeting window (below). **Minimize Live Window to a Bar** (⌘M) shrinks it to a small bar, and **Expand Live Window** brings back the full window.
- **Subtitles** (⌘T) shows TV-style captions along the bottom of the screen while recording. Clicks pass straight through them.
- **Export Transcript** saves or copies the transcript as it stands right now, even mid-meeting.
- **Live Update Interval** sets how often the live translation line and the clock refresh (default 500 ms).
- **Open Last Transcript**, **Open Transcripts Folder**, **Settings…**

While recording, the menu bar shows the elapsed time.

**Global shortcuts** (work in any app): ⌃⌥T starts or stops translating, ⌃⌥R starts or stops
transcribing, and ⌃⌥B adds a bookmark.

**Automatic start and stop** (Settings → Meetings):

- When Teams, Zoom, Webex, FaceTime, Slack or Discord starts using the microphone, a notification offers **Translate** or **Transcribe**. When the call ends, the recording stops and saves.
- A recording nobody has spoken in for 10 minutes stops and saves by itself. The window counts down the last minute and has a **Keep Recording** button.
- If a calendar event is happening, the file is named after it and the attendee list goes into the front matter and the minutes.

### The meeting window

A floating window that stays on top, even over full-screen Teams:

- **Live captions** (bottom strip): what is being said right now, in the original language as the recognizer hears it. When translating, the English is underneath.
- **Transcript** (left): the whole meeting so far. For translations, the original is in grey above the English. It follows new lines; scroll up to read back and it stays put until you press **Jump to latest**.
- **Minutes** (right, when live minutes are on): updated at the interval you choose, with **Update now**, a copy button, the time of the next update and the estimated API cost. The checklist button adds the action items to Apple Reminders; you pick which, and due dates like "by Friday" are recognized.
- **Ask** (right, with an API key): questions about the meeting so far ("what did they decide about the budget?"), answered from the transcript with timestamps. Questions and answers are saved in the file.
- **Speaker names**: after the meeting, click "Speaker 2" to rename them everywhere, in the window, the file and the minutes. Renaming a speaker to a name already in the meeting merges the two. With **Remember this voice** on, that person is named automatically in later meetings. Remembered voices are listed in Settings → General → Speakers.
- **Export** (header): save as Markdown or text, or copy to the clipboard. Includes the transcript, translation and current minutes.
- **Minimize** (the inward arrows, top right): shrinks the window to a small bar with the clock, the language being recognized, the latest caption (and its translation), **Bookmark** and **Stop & Save**. Recording, the transcript and the minutes carry on as before. The outward arrows bring back the full window. The bar and the full window each remember their own position and size, and the choice is kept for the next meeting (also in Settings → General).

Transcripts are saved to `~/Documents/Meeting Transcripts/`, named like
`2026-09-17 14-05 German meeting (translated).md`. During the meeting the file is kept up to date as
a draft. When you stop, it is replaced with the final version, which includes speaker names. Quitting
the app mid-meeting saves first.

### Live minutes (optional, uses an API key)

Settings → **Minutes** turns on minutes for translated and/or transcribed meetings. Choose Anthropic
(default model `claude-opus-5-5`) or OpenAI, paste an API key, and press **Test Key**. That checks
the key and lists the models it can use. During the meeting the minutes are rewritten at the chosen
interval (default 3 minutes; a change applies to a meeting in progress too). When you stop, final minutes with speaker names are written at the top
of the transcript file. Minutes are written in English for translated meetings and in the meeting's
language otherwise. Each update sends the whole transcript so far plus the current minutes.

Without an API key (or with minutes off), the same pane holds your own notes, saved with the
transcript under "Notes". Once a key is set, an **AI Minutes** button appears there, even mid-meeting:
it writes minutes from the transcript so far and keeps them updated at the interval, or, after the
meeting, writes final minutes from the whole transcript. Notes you've taken are kept and worked in.

You can edit the minutes with the pencil button, during the meeting or after it: correct a name,
delete something wrong, or add your own notes. Updates wait while you edit. Your version becomes the
current minutes, and a list of everything you changed goes with every later update and the final
minutes. The model works your text in where it belongs, tidies its wording (shorthand, typos,
misheard names), fills it in from the transcript, applies your corrections throughout, and doesn't
bring back what you deleted. After the meeting, edits go straight into the
file.

The prompt is shown in Settings → Minutes and can be edited (e.g. "focus on my action items"); **Reset to Default** brings back the built-in one.

- **Privacy:** minutes and **Ask** are the only features that send anything off the Mac. The transcript text, not audio, goes to the provider you chose, and only while minutes are on or when you ask a question.
- **Cost:** the transcript is sent so that everything already sent is read from the provider's prompt cache. The pane shows an estimate; with `claude-opus-5-5` a one-hour meeting at 3-minute updates costs around a dollar, and `claude-haiku-5-5` costs a few cents.
- **Key storage:** keys are stored in `~/Library/Application Support/Kurzschrift/api-keys.json`, readable only by your user account. They're deliberately not in the keychain: the app is signed ad hoc, so the keychain would ask for your password after every rebuild.

### Getting names and languages right

- **Vocabulary** (Settings → General): names and terms that keep getting misheard (product names, acronyms, colleagues' names). They go to Apple's speech recognizer and to the minutes.
- **Mixed-language meetings** (Settings → Translate / Transcribe → "Sometimes also"): a second recognizer listens in parallel, and for each sentence the one that understood it is kept. The menu and the meeting window say "German + English" when this is on, and the live captions name the language they're showing.
  - English sentences in a translated German meeting are kept as spoken instead of being translated. It's on for translated meetings (also English) and off for transcribed ones.
  - Deciding: mostly each recognizer's own per-word confidence, blended with Apple's on-device language detection. The English model turns German speech into English-sounding nonsense with low confidence, so English words the German recognizer didn't hear at all are dropped below 0.6 confidence while English is the minority. On a real Austrian meeting this cut speech put in the wrong language from 11% to 1%, with no change on an English meeting (AMI ES2004a).
  - Live captions switch as soon as a recognizer writes the *other* language: Apple's German model writes English speech down as English (98% of the time in an English meeting), so its own text shows when English starts. They switch back when a finished sentence shows a clear confidence gap (the English model scores 0.1 to 0.4 on German speech).
  - Finished lines appear a little later than in one-language meetings, because both recognizers have to finish.

### Start/stop from Shortcuts, Raycast or a hotkey tool

```
open "kurzschrift://start?mode=translate"
open "kurzschrift://start?mode=transcribe"
open "kurzschrift://stop"
open "kurzschrift://toggle?mode=translate"
open "kurzschrift://minutes"     # update the minutes now (switches AI minutes on in a notes-only meeting)
open "kurzschrift://bookmark"
open "kurzschrift://ask?q=What%20did%20we%20decide%3F"
open "kurzschrift://rename?from=Speaker%202&to=Maria"   # after a meeting; &remember=no to skip the voice
```

## How it works (all on this Mac)

- **Meeting audio** comes from a Core Audio process tap (vendored [audiotee](https://github.com/makeusabrew/audiotee), MIT). No BlackHole or virtual device, and it doesn't change your output.
- **Microphone** input uses the system default mic, or the one chosen in Settings. It follows device changes.
- **Speech recognition** uses Apple SpeechAnalyzer (macOS 26), one recognizer per source, for the live captions and the first version of each line.
- **Better transcript:** about every 15 s of speech, [NVIDIA Parakeet TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) (CC BY 4.0, Core ML conversion by FluidInference) goes over that stretch again on the Neural Engine, and its sentences replace Apple's in the window, the minutes and the file. One model covers German and English, including switches between them; each sentence's language is detected for the translation. It takes about 30 s per hour of audio. Settings → General → **Improve the transcript with Parakeet** turns it off.
- **Translation** uses the Apple Translation framework, the same engine as the Translate app.
- **Speaker separation** uses [FluidAudio](https://github.com/FluidInference/FluidAudio) (vendored, Apache 2.0). It runs pyannote community-1 + VBx after the meeting on the recorded meeting audio, in a separate helper process. If that ever crashes, you still get the transcript without speaker names. Words are matched to speakers by their timestamps.
- **Echo removal:** with loudspeakers, your microphone also hears the others. Microphone lines that repeat what the meeting audio said at the same moment are dropped.

One-time downloads: Apple's speech and translation models (through macOS), and FluidAudio's speaker
models (21 MB) and Parakeet (470 MB), both from Hugging Face into `~/Library/Application Support/FluidAudio`. Audio is recorded
to `~/Library/Application Support/Kurzschrift/Recordings` during a meeting as an uncompressed WAV (16 kHz
mono, about 115 MB per hour), which transcription and speaker separation work from. When the meeting is
saved, a compressed copy (AAC `.m4a`, 32 kbps by default, about 14 MB per hour) goes next to the
transcript and the WAV is deleted. Settings → General sets the bit rate (16 to 48 kbps, or the
uncompressed WAV) or turns keeping the recording off.

## Accuracy (measured on this Mac)

- **Speakers on a real meeting:** on the 17-minute AMI meeting ES2004a (4 people, real recording), all 4 were found and 99.2% of words sat in turns labeled with the right person. Processing took 16 s.
- **Short meetings:** speaker detection needs a few minutes of each voice. On a 5-minute excerpt of the same meeting it merged two people into one.
- **Undetected speech:** very quiet or very short speech that speaker detection misses is labeled "Unknown speaker".
- **Fewer phantom speakers:** speaker detection splits short reactions ("yeah", "mm") off into extra speakers, and with loudspeakers and **Several people share this microphone** on, it found the people in the call a second time on the microphone. Speakers who only ever speak in snippets of under 8 s and have under 10% of the talking are folded into the nearest real speaker, and microphone voices that match someone in the meeting audio count as that person. A 65-minute call with three people went from 10 speakers to 3; AMI ES2004a keeps its 4.
- **Recognition:** word error rate on AMI ES2004a against its manual transcript: Apple 20.3%, Parakeet v3 16.3% on the whole recording and 16.8% in the 15 s stretches used live (v2, English only: 19.5%; Whisper large-v3-turbo: 31.1%, because it invents text in pauses). Single sentences give Parakeet too little context (26.1%), which is why it waits for 15 s. On an Austrian German meeting Apple left out whole sentences that Parakeet got. Names and jargon still get misheard (add them under **Vocabulary**, which helps Apple's live version), and a sentence can come out as a statement when it was a question.
- **Languages:** each recording expects one spoken language plus, optionally, a second one (see [Getting names and languages right](#getting-names-and-languages-right)). Speech in any other language comes out garbled.

## Permissions

The first recording asks for **System Audio Recording** and, when the microphone is included,
**Microphone**. The app is signed ad hoc, without a certificate or keychain access, so **macOS asks
again after every rebuild**. If a meeting produces nothing, check System Settings → Privacy & Security →
Screen & System Audio Recording.

Translation needs the languages downloaded: System Settings → General → Language & Region →
Translation Languages. The Translate tab in Settings shows whether they are installed.

Optional features ask once when first used:
- **Notifications:** for call detection.
- **Calendar:** when a meeting starts, to find the current event.
- **Reminders:** when you add action items.

All of them stay on the Mac. Voices you name are stored in `~/Library/Application Support/Kurzschrift/voices.json`.

## Build

```bash
./build.sh   # swift build (keychain disabled) → Kurzschrift.app → ~/Applications
```

Needs only the Xcode Command Line Tools. Both dependencies are vendored under `Vendor/`, and FluidAudio
is built without its NeMo text-processing binary, so nothing is fetched from GitHub at build time.

## Troubleshooting and re-running

- Log: `~/Library/Logs/Kurzschrift.log`. It records events and segment lengths, never transcript text.
- To rebuild a transcript from a kept recording, for example after changing your name or layout:

  ```bash
  "~/Applications/Kurzschrift.app/Contents/MacOS/Kurzschrift" --reprocess --mode transcribe \
      --system "… (meeting audio).m4a" --mic "… (microphone).m4a" --out ~/Desktop \
      [--language de-DE] [--also en-US|none] [--start 2026-09-30T10:20:52+02:00] [--verbose]
  ```

  `--language` and `--also` override the spoken languages, and `--start` sets the meeting's original start time for the file. With Parakeet on, the whole recording is redone with it at once (`--no-parakeet` keeps Apple's text).
- Developer hooks. The links among them only work while Settings → General → **Allow developer links** is on. It's off by default, because any web page can try to open a kurzschrift:// link.
  - `kurzschrift://start?mode=translate&audio=/path/meeting.wav` replays a recording through the live pipeline instead of capturing audio, which needs no permissions.
  - `kurzschrift://edit-minutes?text=…` replaces the minutes as if you had edited them (`?step=begin` / `?step=cancel` hold and release an edit).
  - `kurzschrift://snapshot?path=/tmp/window.png` (or `&window=subtitles`) draws the meeting window's or the subtitle bar's own view to a PNG (the path has to end in `.png`).
  - `kurzschrift://window?size=small` (or `size=full`) switches the meeting window between the bar and the full window.
  - `Kurzschrift --parakeet "meeting audio.m4a" out.json [v2|v3]` transcribes a recording with NVIDIA Parakeet (downloads about 470 MB once) and writes the text with token timings, for comparing recognizers.
  - `defaults write local.kurzschrift minutesBaseURL.anthropic http://localhost:8765` (and `minutesBaseURL.openai`, so it doesn't matter which provider is chosen) points the minutes requests at a local mock server. While one is set, the log says so at launch and names the server each request went to.

## License

Kurzschrift is free software: you can redistribute it and/or modify it under the terms of the
[GNU Affero General Public License v3.0](LICENSE) as published by the Free Software Foundation.
It comes with no warranty.

The vendored packages in `Vendor/` keep their own licenses: audiotee is MIT, FluidAudio is
Apache 2.0 (see [Vendor/README.md](Vendor/README.md)). The Parakeet model, downloaded at first use and
not part of this repository, is NVIDIA's, under CC BY 4.0.
