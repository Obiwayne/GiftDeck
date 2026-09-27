# All-in-one overlay and green-screen alert videos

Two stream-tool additions (v2.1): one overlay link that shows everything, and see-through green-screen
videos on alerts.

## All-in-one overlay (`/overlay/all`)

One Browser Source / TikTok LIVE Studio "Link" source, portrait 1080 x 1920 by default (landscape works too).
It shows, each switchable and placeable on the Overlays page ("All-in-one overlay" card, at the top,
marked Recommended):

| Part | Page it reuses | Notes |
|---|---|---|
| Alerts and interrupts | `alerts.html` | Covers the whole page in its own layer; "Where" = where alert cards pop up. Full-screen alerts always fill everything. |
| Gift Spinner | `spinner.html?id=any` | Shows whichever spinner an event spins (first one in between). "Only show while it spins" on by default. Floats on top; doesn't push other parts. |
| Goals | `goal.html` (one per goal) | Stacked. |
| Top 3 gifters and next goal | `strip.html` | |
| Gift board | `menu.html` | Off by default. Stretch mode gets square tiles + header. |
| Gift list | `giftlist.html` | Off by default. |

Settings (`OverlayConfig.AllInOne`, `Models/AllInOneModels.cs`, saved per profile in overlays.json):
per part `On`, `Spot` (TopLeft, Top, TopRight, Left, Middle, Right, BottomLeft, Bottom, BottomRight) and
`Width` (% of the page); `SpinnerOnlyWhileSpinning`; `Scale` (Size slider, 50-300 %, default 130);
`LeaveRoomForTikTok` (portrait only: keeps the top 8 % and bottom 18 % clear for the TikTok app's own
buttons and chat); `Landscape` (only changes the size Add to OBS and the preview use).
The page gets them in the normal state message (`allInOne`), so changes show live.

### Why iframes (and not shared drawing code)

Each part is the real overlay page in a same-origin iframe. The pages' CSS uses short generic class names
(`.title`, `.bar`, `.goal`, `.fill`, `.who`...) that clash as soon as two pages share one document, and the
spinner/menu size themselves from their own viewport. Iframes keep every page exactly as it is (one copy of
the drawing code, the single pages keep working), and the all-in-one page only does layout.

What was factored out instead is the connection: `Overlays/common.js` (`GD.listen`). On its own a page opens
`/events` as before; with `?embed=1` inside the all-in-one page it listens for `postMessage` from the parent,
which holds the only `/events` connection and passes every message down (and re-sends the latest state
when a part says `gd-ready`). So the all-in-one link costs one connection however many parts it shows.
Browsers allow only ~6 open connections to one server, and each single overlay page keeps one open; in the
test run headless Edge stopped loading new pages from the overlay server after it had opened 5-6 single
pages in one tab (likely the same limit), which is one more reason to prefer the one link.

Parts are drawn at `Scale x min(page width, height) / 1080` (CSS transform on the iframe), so the preview
at 270 x 480 looks the same as the real 1080 x 1920. Heights of the flowing parts are measured from their
content every 300 ms (same origin), so a growing gift list or a new gifter just fits.

Server: `/overlay/all` -> `all.html`; `/lib/<name>.js` serves embedded `Overlays/*.js`
(csproj: `EmbeddedResource Include="Overlays\*.html;Overlays\*.js"`).
The single pages (`alerts`, `goal`, `strip`, `giftlist`, `menu`, `spinner`) now include `/lib/common.js`.
`alerts.html` also takes `?pos=<spot>`; `spinner.html` takes `?id=any` and `?idle=hide`.

UI: `Views/AllInOnePanel.xaml(.cs)` (Copy URL, Add to OBS, Test alert, Test spin, WebView2 preview),
placed at the top of `OverlaysView`. Add to OBS makes a 1080 x 1920 (or 1920 x 1080) source with
"Control audio via OBS" on (`ObsService.AddBrowserSourceAsync(..., audioViaObs: true)`); the Gift alerts
"Add to OBS" now does the same, so alert videos' sound reaches the stream mix.

## Remove green background (chroma key) on alert videos

Alerts editor (Overlays page > Alerts and interrupts), per alert: **Remove green background** tick box,
colour (hex box + swatch, default `#00FF00`; blank = read it from the video's corners), **Pick from video**
(the preview browser reads the corners of the first frame), **Strength** (1-100, default 40) and
**Soft edges** (1-100, default 8). Fields on `AlertDef`: `KeyGreen`, `KeyColor`, `KeyStrength`,
`KeySoftness`. The alert message carries `key: {color, strength, softness}` for video alerts only.

`Overlays/chroma.js` (`GDChroma.attach(video, canvas, opts, onFail)`): the video plays (with its sound,
hidden 2 px element) and every new frame (`requestVideoFrameCallback`, else animation frames) is drawn onto
a canvas through a chroma key. The maths is OBS's Chroma Key filter: distance from the key colour in the
Cb/Cr plane with a small box filter, `similarity = strength/100`, `smoothness = softness/100`, plus spill
removal (0.1) so green fringes go grey. WebGL shader (premultiplied alpha); canvas 2D fallback with the same
maths (videos over 1920 px are drawn at 1920). A keyed alert has no dark card behind it, so the subject
floats on the stream. If the video can't be read (a web address without CORS: the canvas would be
"tainted"), it falls back to showing the plain video.

Sound: the page never forces mute. It calls `play()` with sound and only mutes if the browser refuses
autoplay with sound (a normal browser tab without a click). OBS allows sound; tick "Control audio via OBS"
to have it in the stream mix (Add to OBS does). The all-in-one iframes have `allow="autoplay"`.
The WPF preview (WebView2) may still start alert videos muted: it's a normal browser for autoplay.

## Tests

`tests/Overlays/OverlayHarness` (console, not part of GiftDeck.exe):

```
dotnet build tests/Overlays/OverlayHarness/OverlayHarness.csproj -c Debug -o <scratch>\hbin
<scratch>\hbin\OverlayHarness.exe <scratch>\run 21386
```

It sets `GIFTDECK_DATA` to `<scratch>\run\data` before anything loads, builds a sample profile (5 gift events,
2 goals, 3 gifters, 5 board tiles, a spinner, 3 alerts using a generated green-screen clip), runs the real
`OverlayServer` on the given port (refuses 21300), makes the clip with ffmpeg (640x360, green background,
moving colour bars, a skin-tone bar, 440 Hz tone, MP4 + WebM), renders the WPF cards offscreen with the app
theme, then drives headless Edge over its DevTools port (`--remote-debugging-port`, `--no-sandbox`,
`--autoplay-policy=no-user-gesture-required`, `--mute-audio`) and captures with `Page.captureScreenshot`
(`--screenshot` hangs on these SSE pages). Checks include: parts present, no `/events` connections inside
the parts, live goal updates, WebGL keying, colour read from corners, no pure-green pixels left over a
checkerboard / colour (all-in-one card and full-screen, alerts page), canvas 2D fallback, pick-from-video,
video plays unmuted, cross-origin video falls back to plain, switching a part off live. Screenshots go to
`<scratch>\run\shots`.

Notes from testing: headless Edge painted a CSS *gradient* page background as white behind a top-level WebGL
canvas (plain colours and the checkerboard were fine; the page itself is transparent in OBS), so the test uses
plain colours there. `HARNESS_SERVE_ONLY=<seconds>` just keeps the sample server up for poking at by hand.

Not tested here: inside real OBS / TikTok LIVE Studio (CEF autoplay and audio routing, WebGL there),
the WebView2 preview and "Pick from video" button in the running app (the JS it calls is tested in Edge),
Add to OBS with `reroute_audio`.
