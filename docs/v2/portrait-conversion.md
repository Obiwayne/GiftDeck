# Portrait conversion (Aitum Vertical -> "GiftDeck Portrait")

`Services/ObsPortraitSetup.cs` turns the Aitum Stream Suite vertical canvas into a normal OBS scene
collection and profile whose main canvas is 1080x1920. Notes below are from the user's OBS 32.2.2
files (`%APPDATA%\obs-studio\basic`), read-only.

## How the collection stores the Aitum canvas

OBS 31+ has native multi-canvas support, and Aitum uses it, so there is no separate Aitum scene file:

- `canvases[]` in the collection lists extra canvases: `{ "info": { "name": "Aitum Vertical", "uuid": "54207c8e-...", "private": false, "flags": 14 } }`.
  The main canvas is not listed; libobs gives it the fixed uuid `6c69626f-6273-4c00-9d88-c5136d61696e`.
- Every scene in `sources[]` (id `scene`) has a `canvas_uuid`. Main-canvas scenes carry the fixed uuid,
  vertical scenes carry the Aitum canvas uuid. Groups (`groups[]`) also carry `canvas_uuid`.
- `scene_order`, `current_scene` and `current_program_scene` cover only the main canvas.
- Aitum adds two keys to each vertical scene's `settings`: `order` (its position in Aitum's scene list,
  0-based) and `canvas_active` (true on the scene currently live on that canvas).
- Scene items are the same as on the main canvas: `settings.items[]`, bottom-to-top, each with
  `source_uuid` + `name`, `id`, `visible`, `locked`, transform (`pos`, `scale`, `rot`, `crop_*`,
  `bounds*`, `align`), relative transform (`pos_rel`, `scale_rel`, `bounds_rel`) with
  `scale_ref` = the canvas size (1080x1920 for vertical items), blend/scale filter, show/hide transitions.
- Inputs (camera, captures, browser sources, media) are shared by both canvases; they have no canvas.
  Filters live on the input itself (`filters[]`), so they are shared too.
- Global audio (`DesktopAudioDevice1`, `AuxAudioDevice1`, ...) is top-level, outside `sources[]`.
  The vertical scenes add `Mic/Aux` as a scene item by its uuid.
- Transitions: the collection's `transitions`/`quick_transitions` are main-canvas; Aitum keeps the
  vertical canvas's transition name (`Fade`) in the profile's `aitum.json`.
- The profile's `aitum.json` holds the canvas (name, uuid, dock layout) and Aitum's own outputs
  (Vertical Stream, Vertical Backtrack, Vertical Virtual Camera). The canvas size (1080x1920) is only
  reported live (vendor `get_canvas`), not in these files.

## What the converter does

- Vertical scenes become main-canvas scenes (`canvas_uuid` set to the main uuid, `order`/`canvas_active`
  removed), listed in Aitum's `order`. The `canvas_active` scene becomes the current (program) scene.
- Kept: every source the vertical scenes reach through their items (following nested scenes and groups),
  plus any source a kept source or its filters names in its settings (masks, clones, move filters),
  plus the global audio devices. Items, transforms, visibility and filters are copied unchanged.
- Dropped: landscape-only scenes and sources only they use, the `canvases` entry, saved projectors.
  A landscape scene nested in a vertical scene is kept (with a warning).
- Profile: new folder `profiles/GiftDeck_Portrait` with the source `basic.ini` renamed, `[Video]`
  base and output set to 1080x1920 and rescale sizes flipped to 1080x1920; fps, encoder, bitrate,
  audio and hotkeys unchanged. `service.json` and encoder JSONs are copied; `aitum.json` is not.
- Files are named the way OBS names them (spaces become `_`): `scenes/GiftDeck_Portrait.json`.
  An existing "GiftDeck Portrait" collection/profile is moved to `basic/giftdeck-backups/<timestamp>/`
  (outside `scenes/` and `profiles/`, so OBS doesn't list a duplicate).
- `Apply` refuses while `obs64.exe` runs, since OBS rewrites its collection and profile on exit.
  OBS isn't switched to the new collection; launch it with
  `--collection "GiftDeck Portrait" --profile "GiftDeck Portrait"`.

## Caveats

- The collection file is only as fresh as OBS's last save. With OBS open, the live Aitum canvas already
  differed from the file (an extra Map item in SPLIT, Browser hidden in TT, SPLIT live instead of TT).
  Convert after OBS has been closed.
- Aitum's vendor `get_scenes` lists scenes in file order (CAM, SPLIT, TT, Monitor); the `order` keys say
  Monitor, SPLIT, TT, CAM. The converter trusts `order`; confirm against the Aitum dock.
