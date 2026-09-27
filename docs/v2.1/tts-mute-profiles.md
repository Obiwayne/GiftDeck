# Text to speech: quick mute and a voice per profile

Like StreamToEarn's "pick a voice, swap per preset, mute on demand".

## Quick mute

- A speaker button in the **Live panel header** (right of "Peak") and on **Go LIVE**
  (`Views/TtsMuteButton.xaml`, one control used in both places).
  - Live panel: just the speaker icon while on (the header is narrow); red "TTS muted" while muted.
  - Go LIVE: in the **live bar**, left of the Go LIVE / End LIVE button, always labelled
    ("TTS on" / "TTS muted"). The live bar is always on screen, while the Music card sits in a
    scrolling column and is about the music player (a speaker there would read as "music volume").
  - Tooltip: "Text to speech is muted. Click to turn it back on." / "Text to speech is on. Click to mute it".
- Muting (`TtsService.SetMuted`) stops what's being said now (Windows voice cancelled, an online
  clip stopped mid-play, queued lines dropped, a clip still downloading is thrown away) and
  `Speak()` refuses everything until unmuted: Speak actions in events and chat reading both go
  through it. `Speak()` now returns false when it didn't queue anything.
- **Saved** (`AppSettings.TtsMuted` in settings.json), not session-only: if GiftDeck restarts mid-stream
  (crash, update) it doesn't suddenly start reading chat again. So it can't stay off unnoticed, the
  mute is visible everywhere: the menu entry becomes "Text to speech (muted)" with a red mute icon
  (also when the menu is collapsed to icons), and the Text to speech page shows a red
  "Text to speech is muted" banner with an Unmute button.
- Mute is the same for every profile (it's a "right now" switch, not a setup choice).

## Voice per profile

- Each profile has `profiles\<name>\tts.json`: voice, speed, volume, read chat on/off
  (`Services/TtsProfile.cs`).
- The live values stay in `AppSettings.TtsVoice/TtsRate/TtsVolume/TtsReadChat`, so the rest of the
  app (RulesEngine, TtsService) is unchanged: switching profile copies the profile's values in and
  re-applies the synth; the TTS page saves to both settings.json and the active profile's tts.json.
- The chat template ("{user} says {comment}") and the cut-off length stay the same for every profile;
  the page says so.
- The TTS page says "These settings belong to the **<profile>** profile" and refreshes when the
  profile is switched while it's open.
- **Migration**: on start, every profile without tts.json gets the current global values, so an
  existing user keeps their voice (e.g. Microsoft Zira Desktop, chat reading on) in every profile.
- New blank profile: starts with the voice in use. Copy: copies the source's tts.json.
- **Export/import** (.giftdeck): tts.json is in the zip. Importing an older file without it gives the
  new profile the voice in use now.

## Files

- `Services/TtsService.cs`: mute, `MuteChanged`, `OnlineBusy`, generation/mute checks before an online clip plays, `Speak()` returns bool.
- `Services/TtsProfile.cs` (new): per-profile values, save/load/migrate.
- `Services/ProfileService.cs`: small hooks in Init, Create, SaveActive, LoadActive, Export, Import.
- `Models/Models.cs`: `AppSettings.TtsMuted`.
- `Views/TtsMuteButton.xaml(.cs)` (new), one line each in `Views/LivePanel.xaml` and `Views/GoLiveView.xaml`.
- `MainWindow.TtsMute.cs` (new) + one call in `MainWindow.xaml.cs`: the "(muted)" menu entry.
- `Views/TtsView.xaml(.cs)`: profile line, muted banner, saves per profile; subtitle now mentions the Google voices.

## Test

`tests/Tts` (TtsHarness): builds against a scratch GiftDeck.dll and runs in-process with
`GIFTDECK_DATA` at a scratch folder (refuses anything under %APPDATA%). No sound: online downloads
are faked (a silent WAV), volume 0 throughout.

```
dotnet build GiftDeck.csproj -c Debug -o <scratch>\build
dotnet build tests\Tts\TtsHarness.csproj -c Debug -o <scratch>\harness -p:GiftDeckBuild=<scratch>\build
<scratch>\harness\TtsHarness.exe <scratch>\data <scratch>\shots
```

Covers migration from a global settings.json, A/B switch, new/copied profiles, export/import (new and
old files), mute for Windows and online voices, chat reading muted vs unmuted, stopping a playing
online clip and a downloading one, mute saved across restarts. It also renders the Live panel header,
the Go LIVE live bar, the TTS page and the menu entry (on and muted) to PNGs with the app theme.

Not covered: the real Google endpoint, clicking the buttons in the running app, the full MainWindow
(the menu entry is rendered with MainWindow's own helper on a menu button built like BuildNav's).
