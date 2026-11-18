# Kurzschrift

*Kurzschrift* is German for shorthand. It's a free app for live meeting translation and
transcription, in the menu bar on macOS and in the notification area on Windows. Speech recognition,
translation and speaker separation run on your computer; only the optional AI minutes and questions
use an online API. [Privacy](#privacy) lists everything that ever leaves your computer, and
[No warranty, and your responsibilities](#no-warranty-and-your-responsibilities) says what you take on
by using it.

| | Translate Meeting | Transcribe Meeting |
|---|---|---|
| Default language | German → English | English |
| Listens to | Meeting audio (Teams, Zoom, browser…) | Meeting audio **and** your microphone |
| Live window | English translation as people speak (German shown smaller) | Transcript as people speak |
| Saved `.md` | German and English for every passage | English |
| Speakers | Speaker 1…n | Your name + Speaker 1…n |

Everything in the table can be changed in **Settings…**.

## Requirements and installing

**Mac:** macOS 26 or later (it uses Apple's SpeechAnalyzer) on an Apple Silicon Mac. There is also
an Intel build (`…-mac-x86_64.dmg`); it has not been run on a real Intel Mac yet.

**Windows:** see [Kurzschrift for Windows](#kurzschrift-for-windows).

**Download for Mac:**

1. Download `Kurzschrift-<version>-mac-arm64.dmg` from [Releases](https://github.com/havurgiray/kurzschrift/releases/latest), open it, and drag **Kurzschrift.app** onto the Applications folder next to it.
2. Open it. macOS says it could not verify the app, because it isn't notarized by Apple (that needs a paid Apple developer account). Click **Done**.
3. Open System Settings → Privacy & Security, scroll down to "Kurzschrift was blocked…", click **Open Anyway** and confirm with your password or Touch ID. You do this once for each new version.

**Or build it from source** with the Xcode Command Line Tools (`xcode-select --install`, Swift 6.2 or later):

```bash
git clone https://github.com/havurgiray/kurzschrift.git
cd kurzschrift
./build.sh                       # builds Kurzschrift.app and installs it into ~/Applications
open ~/Applications/Kurzschrift.app
```

The speech-bubble icon appears in the menu bar. The first recording asks for the permissions
described under [Permissions](#permissions).

Recording other people may need their consent where you live. Let the people in your meetings know
that you transcribe them. Kurzschrift doesn't tell them for you; see
[your responsibilities](#no-warranty-and-your-responsibilities).

## Using it

Click the speech-bubble icon in the menu bar:

- **Translate Meeting (German → English)** (⌘1) or **Transcribe Meeting (English)** (⌘2) starts recording.
- A second **Transcribe Meeting** entry (⌘3) writes down the languages of Translate Meeting without translating them, for example "Transcribe Meeting (German + English)". It's there while the two modes use different languages; Settings → Transcribe switches it off.
- **Stop and Save** (⌘S) finishes recording. Speakers are identified and the final transcript is written, which takes a few seconds.
- **Bookmark This Moment** (⌘B) marks "this was important" with a ⭐ in the transcript and the minutes.
- **Show Live Window** (⌘L) opens the meeting window (below). **Minimize Live Window to a Bar** (⌘M) shrinks it to a small bar, and **Expand Live Window** brings back the full window.
- **Subtitles** (⌘T) shows TV-style captions along the bottom of the screen while recording. Clicks pass straight through them.
- **Export Transcript** saves or copies the transcript as it stands right now, even mid-meeting.
- **Live Update Interval** sets how often the live translation line and the clock refresh (default 500 ms).
- **Open Last Transcript**, **Open Transcripts Folder**, **Settings…**
- **Check for Updates…** asks GitHub whether a newer release exists and offers its download page. Kurzschrift never downloads or installs anything by itself. Settings → General → About can do the same check once a day (off by default); a newer version then shows in the menu as **Get Kurzschrift 1.x…**.

While recording, the menu bar shows the elapsed time.

**Global shortcuts** (work in any app): ⌃⌥T starts or stops translating, ⌃⌥R starts or stops
transcribing, and ⌃⌥B adds a bookmark.

**Automatic start and stop** (Settings → Meetings):

- When Teams, Zoom, Webex, FaceTime, Slack or Discord starts using the microphone, a notification offers **Translate** or **Transcribe**. When the call ends, the recording stops and saves.
- A recording nobody has spoken in for 10 minutes stops and saves by itself. The window counts down the last minute and has a **Keep Recording** button.
- If a calendar event is happening, the file is named after it, and its attendees go into the front matter and fill in the participants under **Context** (below).

### The meeting window

A floating window that stays on top, even over full-screen Teams:

- **Live captions** (bottom strip): what is being said right now, in the original language as the recognizer hears it. When translating, the English is underneath.
- **Transcript** (left): the whole meeting so far. For translations, the original is in grey above the English. It follows new lines; scroll up to read back and it stays put until you press **Jump to latest**.
- **Minutes** (right, when live minutes are on): updated at the interval you choose, with **Update now**, a copy button, the time of the next update and the estimated API cost. The checklist button adds the action items to Apple Reminders; you pick which, and due dates like "by Friday" are recognized.
- **Ask** (right, with an API key): questions about the meeting so far ("what did they decide about the budget?"), answered from the transcript with timestamps. Questions and answers are saved in the file.
- **Context** (right): who's in the meeting, one per line with a role if it helps ("Maria Huber – project lead"), and what it's about (topic, agenda, background, abbreviations). It starts with the calendar invite's attendees. The minutes and your answers use it, speech recognition listens for the names, renaming a speaker offers them, and it's saved at the top of the file. After the meeting, **Redo Minutes** writes the minutes again once you've changed it.
- **Speaker names**: after the meeting, click "Speaker 2" to rename them everywhere, in the window, the file and the minutes. Renaming a speaker to a name already in the meeting merges the two. With **Remember this voice** on, that person is named automatically in later meetings. Remembered voices are listed in Settings → General → Speakers.
- **Export** (header): save as Markdown or text, or copy to the clipboard. Includes the transcript, translation and current minutes.
- **Minimize** (the inward arrows, top right): shrinks the window to a small bar with the clock, the language being recognized, the latest caption (and its translation), **Bookmark** and **Stop & Save**. Recording, the transcript and the minutes carry on as before. The outward arrows bring back the full window. The bar and the full window each remember their own position and size, and the choice is kept for the next meeting (also in Settings → General).

Transcripts are saved to `~/Documents/Meeting Transcripts/`, named like
`2026-09-17 14-05 German meeting (translated).md`. During the meeting the file is kept up to date as
a draft. When you stop, it is replaced with the final version, which includes speaker names. Quitting
the app mid-meeting saves first.

If Kurzschrift is force-quit or crashes mid-meeting, the draft has the transcript up to that moment,
and the next launch moves the recording next to it ("… (meeting audio, recovered).wav") and tells
you. [Troubleshooting](#troubleshooting-and-re-running) shows how to rebuild the full transcript
from that recording.

### Live minutes (optional, uses an API key)

Settings → **Minutes** turns on minutes for translated and/or transcribed meetings. Choose Anthropic
(default model `claude-opus-5-5`) or OpenAI, paste an API key, and press **Test Key**. That checks
the key and lists the models it can use. During the meeting the minutes are rewritten at the chosen
interval (default 3 minutes; a change applies to a meeting in progress too). Switching minutes off
in Settings during a meeting stops them for that meeting. When you stop, final minutes with speaker names are written at the top
of the transcript file. If that last request fails, the file says so and the pane offers **Try Again**. Minutes are written in English for translated meetings and in the meeting's
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

- **Privacy:** minutes and **Ask** are the only features that send anything from a meeting off your computer. The transcript text, not audio, goes to the provider you chose, and only while minutes are on or when you ask a question. See [Privacy](#privacy).
- **Cost:** the transcript is sent so that everything already sent is read from the provider's prompt cache. The pane shows an estimate; with `claude-opus-5-5` a one-hour meeting at 3-minute updates costs around a dollar, and `claude-haiku-5-5` costs a few cents.
- **Key storage:** on the Mac, keys are stored in `~/Library/Application Support/Kurzschrift/api-keys.json`, readable only by your user account. They're deliberately not in the keychain: the app is signed ad hoc, so the keychain would ask for your password after every rebuild. On Windows they're encrypted for your Windows account.

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
open "kurzschrift://start?mode=transcribe&language=second"   # the second Transcribe entry
open "kurzschrift://stop"
open "kurzschrift://toggle?mode=translate"
open "kurzschrift://minutes"     # update the minutes now (switches AI minutes on in a notes-only meeting;
                                 # after a meeting, redoes them once you've changed the context)
open "kurzschrift://bookmark"
open "kurzschrift://ask?q=What%20did%20we%20decide%3F"
open "kurzschrift://rename?from=Speaker%202&to=Maria"   # after a meeting; &remember=no to skip the voice
```

`ask`, `rename`, and `minutes` when it would switch AI minutes on or redo them, only work while
Settings → General → Links → **Allow links to ask questions, start AI minutes and rename speakers**
is on (on Windows: Settings → Advanced). It's off by default, because any web page can try to open
a kurzschrift:// link and these send the transcript to your AI provider or change the saved file.

## Kurzschrift for Windows

Windows 10 (version 2004 or later) or Windows 11, on a 64-bit Intel/AMD PC (x64) or an ARM PC
(Snapdragon). About 1 GB of disk for the models and 2 GB of memory while recording.

**Download and install:**

1. Download `Kurzschrift-<version>-windows-x64-setup.exe` from [Releases](https://github.com/havurgiray/kurzschrift/releases/latest),
   or `…-windows-arm64-setup.exe` for an ARM PC.
2. Run it. Windows SmartScreen says "Windows protected your PC", because the installer isn't signed
   (that needs a paid certificate). Click **More info → Run anyway**. It installs for your user
   only, so it doesn't ask for administrator rights.
3. The speech bubble appears in the notification area (bottom right; it may be under the ^ arrow).
   The first meeting downloads the models once, about 0.8 GB: speech recognition 640 MB, translation
   about 110 MB per language pair, speaker separation 33 MB. They're kept in
   `%LOCALAPPDATA%\Kurzschrift\Models`.

**What's the same as on the Mac:** the menu (right-click the speech bubble), including the second
Transcribe entry and **Check for Updates…**; the meeting window with the transcript, **Jump to latest**,
Minutes, Ask and Context; the small bar; subtitles; bookmarks; auto-stop after silence; speaker
separation and renaming (with "remember this voice"); the `.md` transcript format; AI minutes with
your own API key; recovery of a meeting that was never saved; and `kurzschrift://` links.

**What's different:**

- Speech recognition is NVIDIA Parakeet v3 alone (25 European languages, recognized without being
  told which). Text appears a sentence at a time; while someone talks, a provisional version is
  refreshed every 2 seconds (Settings → General). There is no "Sometimes also" setting and no live
  update interval: one model hears all its languages.
- Translation is OPUS-MT from the University of Helsinki, on your PC. From English into 13 languages
  and into English from 14; other pairs go through English. Its English is plainer than Apple's.
  When a number from the original is missing in the translation, it is added in brackets.
- Meeting audio is what one output device plays: Windows' default, or the one you choose in
  Settings → General → Audio devices (do that if Teams plays on a headset that isn't the default).
- Global shortcuts: Ctrl+Alt+T (translate), Ctrl+Alt+R (transcribe), Ctrl+Alt+B (bookmark).
- Call detection notices Teams, Zoom, Webex, Slack, Discord and Skype through Windows' own list of
  apps using the microphone, and offers to translate or transcribe.
- The recording is kept as MP3 at the chosen bit rate. If Windows has no MP3 encoder it becomes
  `.m4a` at 96 kbps, and failing that the uncompressed WAV.
- API keys are in `%APPDATA%\Kurzschrift\api-keys.json`, encrypted for your Windows account, so the
  file can't be copied to another PC or account. (On the Mac the same file holds them as plain text,
  readable only by your user account.)
- Settings are arranged differently: General, Translate, Transcribe, Minutes, Meetings and Advanced.
  The link settings, the models folder, the log and the licenses are under Advanced. Vocabulary is
  under Minutes, because on Windows it goes to the minutes only.
- Export is in the tray menu, not in the meeting window's header.
- If a transcript or recording can't be written to your transcripts folder, it goes to
  `%LOCALAPPDATA%\Kurzschrift\Unsaved` and a message says so.
- Not on Windows: the calendar (event title and attendees), adding action items to Reminders,
  "Also count browser calls", rebuilding a transcript from a recording (`--reprocess`), and handing
  Context names to the speech recognizer (the minutes get them).

**Status:** tested in a Windows 11 ARM virtual machine with recorded and replayed German meetings,
including a 44-minute live-capture test, but not yet in a real Teams call on a real PC. Device
changes mid-meeting, shutting Windows down mid-meeting and the call prompts have only been
compiled, not exercised.

The developer links work as on the Mac while Settings → Advanced allows them. The log is
`%LOCALAPPDATA%\Kurzschrift\Kurzschrift.log`. For tests, the environment variables
`KURZSCHRIFT_MINUTES_BASE_URL_ANTHROPIC`, `KURZSCHRIFT_MINUTES_BASE_URL_OPENAI` and
`KURZSCHRIFT_UPDATE_URL` point the minutes and the update check at a mock server; they are only
honoured while developer links are on.

**Build it yourself** with the .NET 10 SDK and Inno Setup 6, in PowerShell:

```powershell
windows\build.ps1                 # both installers into windows\dist
windows\build.ps1 -Arch x64 -NoInstaller
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
again after every update or rebuild**. If a meeting produces nothing, check System Settings → Privacy & Security →
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
./build.sh     # swift build (keychain disabled) → Kurzschrift.app → ~/Applications
./release.sh   # DMGs for Apple Silicon and Intel, and a zip, into dist/ for a GitHub release; installs nothing
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
  It also works on a recovered `.wav` from a meeting that was never saved (the unfinished file header is repaired first). The minutes follow your settings, so they are requested if they're on for that mode.
- Developer hooks. The links among them only work while Settings → General → **Allow developer links** is on. It's off by default, because any web page can try to open a kurzschrift:// link.
  - `kurzschrift://start?mode=translate&audio=/path/meeting.wav` replays a recording through the live pipeline instead of capturing audio, which needs no permissions.
  - `kurzschrift://edit-minutes?text=…` replaces the minutes as if you had edited them (`?step=begin` / `?step=cancel` hold and release an edit).
  - `kurzschrift://snapshot?path=/tmp/window.png` (or `&window=subtitles`, or `&window=settings&tab=general`) draws the meeting window's, the subtitle bar's or a settings tab's own view to a PNG (the path has to end in `.png`).
  - `kurzschrift://window?size=small` (or `size=full`) switches the meeting window between the bar and the full window; `tab=minutes`, `ask` or `context` picks the tab on the right.
  - `kurzschrift://context?participants=…&about=…` fills in the Context tab (a parameter left out keeps its text).
  - `Kurzschrift --parakeet "meeting audio.m4a" out.json [v2|v3]` transcribes a recording with NVIDIA Parakeet (downloads about 470 MB once) and writes the text with token timings, for comparing recognizers.
  - `defaults write local.kurzschrift minutesBaseURL.anthropic http://localhost:8765` (and `minutesBaseURL.openai`, so it doesn't matter which provider is chosen) points the minutes requests at a local mock server. While one is set, the log says so at launch and names the server each request went to.
  - `defaults write local.kurzschrift updateCheckURL http://localhost:8791/latest` points the update check at a mock, and `defaults write local.kurzschrift skipRecovery -bool true` leaves unsaved recordings where they are at launch (for running a test build next to real leftovers).

## Privacy

Kurzschrift has no server, no account, no analytics and no telemetry. Its author receives nothing
from your computer, ever.

**Stays on your computer:** the audio, speech recognition, translation, speaker separation, the
voices you name, the transcripts, the kept recordings and the log (which records events and lengths,
never what was said).

**Leaves your computer, and only in these cases:**

| When | What is sent | To |
|---|---|---|
| Live minutes are on, you press **AI Minutes**, or you ask a question under **Ask** | The transcript text so far (never audio), the minutes so far, your notes, the Context tab, your vocabulary and, on the Mac, the calendar event's attendee names, with your own API key | Anthropic or OpenAI, whichever you chose |
| The first meeting, once per model | A plain download of the models. Like any download it shows your IP address, nothing else | Hugging Face and GitHub; on the Mac also Apple, for its speech and translation models |
| You press **Check Now**, or you turned on the daily update check (off by default) | One request for the number of the latest release, with Kurzschrift's version number | GitHub |

Nothing else goes out. The calendar and Reminders (Mac) are read and written on the Mac only.

What Anthropic or OpenAI do with the text is set by your agreement with them, not by Kurzschrift.
Their API terms differ from their chat apps' terms; read them before sending meetings that aren't
yours to share.

**Where things are kept:**

| | Mac | Windows |
|---|---|---|
| Transcripts and kept recordings | `~/Documents/Meeting Transcripts` (or the folder you chose) | `Documents\Meeting Transcripts` (or the folder you chose) |
| Recording in progress | `~/Library/Application Support/Kurzschrift/Recordings` | `%LOCALAPPDATA%\Kurzschrift\Recordings` |
| API keys, named voices | `~/Library/Application Support/Kurzschrift` | `%APPDATA%\Kurzschrift` |
| What couldn't be saved to the transcripts folder | stays in the Recordings folder until the next launch | `%LOCALAPPDATA%\Kurzschrift\Unsaved` |
| Settings | `defaults read local.kurzschrift` | `%APPDATA%\Kurzschrift\settings.json` |
| Models | `~/Library/Application Support/FluidAudio`, and macOS's own | `%LOCALAPPDATA%\Kurzschrift\Models` |
| Log | `~/Library/Logs/Kurzschrift.log` | `%LOCALAPPDATA%\Kurzschrift\Kurzschrift.log` |

Deleting the app leaves these folders; delete them too to remove everything.

Transcripts and recordings hold what other people said, and often their names. Where you keep them
and who you show them to is up to you.

`kurzschrift://` links can be opened by any web page after your browser asks. Links that start, stop
or bookmark a recording always work. Links that send the transcript to your AI provider or change a
saved file (`ask`, `minutes` when it would switch AI minutes on, `rename`) only work while you allow
them in Settings.

## No warranty, and your responsibilities

This section explains the license in plain words. It doesn't replace it, and it isn't legal advice.

- **No warranty, no liability.** Kurzschrift is provided "as is", without warranty of any kind, and
  to the extent the law allows, its author and contributors aren't liable for any damage or loss
  from using it or from not being able to use it. That includes a meeting that wasn't recorded, a
  lost or wrong transcript, and anything decided on the basis of one. These are sections 15 and 16
  of the [license](LICENSE), which apply in full.
- **The output can be wrong.** Speech recognition mishears words and names, translation can drop or
  change meaning, speakers can be mixed up, and AI minutes can state things nobody said. Check
  anything that matters against the recording before you rely on it or pass it on. Don't use it
  where a mistake could harm someone, such as medical, legal or safety decisions.
- **Recording is your responsibility.** Whether you may record, transcribe or translate a
  conversation depends on where you and the others are, and on the rules of your workplace or school.
  Many places require everyone's consent. In the EU and elsewhere, data-protection law applies to
  what you record: you need a lawful reason, you have to tell people, and you answer for how the
  transcripts and recordings are stored, shared and deleted. Kurzschrift doesn't announce itself to
  the other participants and doesn't check any of this. You do.
- **AI providers are your own account.** Minutes and Ask use your API key. The provider's terms,
  prices and data handling apply, and you pay what they charge. The cost shown in the app is an
  estimate.
- **Unsigned downloads.** The apps aren't notarized or signed with a paid certificate. Download them
  only from this repository's Releases page and compare the SHA-256 in the release notes if you
  want to be sure the file is the published one.
- **Not affiliated.** Kurzschrift is an independent project. It isn't affiliated with or endorsed by
  Microsoft, Zoom, Cisco, Apple, NVIDIA, Anthropic, OpenAI or anyone else named here; their names
  and products are trademarks of their owners.

## License

Copyright (C) 2026 Giray Havur

Kurzschrift is free software: you can redistribute it and/or modify it under the terms of
version 3 of the [GNU Affero General Public License](LICENSE) as published by the Free Software
Foundation, and of no later version (SPDX: `AGPL-3.0-only`).

Kurzschrift is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even
the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
[GNU Affero General Public License](LICENSE) for more details.

The vendored packages in `Vendor/` keep their own licenses: audiotee is MIT, FluidAudio is
Apache 2.0 (see [Vendor/README.md](Vendor/README.md)).

The Windows version uses [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) (Apache 2.0),
[ONNX Runtime](https://github.com/microsoft/onnxruntime) (MIT) and [NAudio](https://github.com/naudio/NAudio)
(MIT), from NuGet. Its models are downloaded at first use: Parakeet TDT 0.6B v3 (CC BY 4.0) as converted
for sherpa-onnx, [Silero VAD](https://github.com/snakers4/silero-vad) (MIT),
[OPUS-MT](https://github.com/Helsinki-NLP/Opus-MT) (Apache 2.0 or CC BY 4.0 depending on the language pair, ONNX
exports by Xenova),
[pyannote segmentation 3.0](https://huggingface.co/pyannote/segmentation-3.0) (MIT) and
[WeSpeaker](https://github.com/wenet-e2e/wespeaker) ResNet34 (CC BY 4.0).

The full license texts of everything the apps include are in [notices/](notices/): `mac.txt` ships inside
Kurzschrift.app, `windows.txt` is installed next to Kurzschrift.exe as `THIRD-PARTY-NOTICES.txt`.
`notices/make.py` rebuilds both from the exact upstream versions.

On the Mac, two sets of models are downloaded at first use and aren't part of this repository. Both are under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/), and both are Fluid Inference's modified
Core ML conversions:

- Speech recognition: [NVIDIA Parakeet TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3), converted in [FluidInference/parakeet-tdt-0.6b-v3-coreml](https://huggingface.co/FluidInference/parakeet-tdt-0.6b-v3-coreml).
- Speaker separation: [pyannote speaker-diarization-community-1](https://huggingface.co/pyannote/speaker-diarization-community-1), with WeSpeaker speaker embeddings and PLDA parameters from BUT Speech@FIT, converted in [FluidInference/speaker-diarization-coreml](https://huggingface.co/FluidInference/speaker-diarization-coreml).
