# ZipPla custom v6 changes

This build is based on `ZipPlaFork-metadata-singleinstance.zip` supplied by the user.

## Metadata
- `Behavior > Metadata` is an independent docked panel.
- `Details` and `Metadata` can be visible simultaneously.
- `Behavior > Layout > Metadata / Thumbnails` provides four positions:
  - Thumbnails / Metadata
  - Metadata / Thumbnails
  - Thumbnails | Metadata
  - Metadata | Thumbnails
- Metadata UI follows Tiefsee4's `MainExif` / `getItemDom` concept: Name | Value.
  (d1: the grid has two columns. Clicking a row or pressing Ctrl+C copies the value, so there is no
  separate Copy column. The value shown in the grid is truncated for readability while the copied
  text is complete.)
- PNG `tEXt`, `zTXt`, `iTXt` are read directly.
- JPEG XMP/COM and WebP XMP are read directly.
- Existing bundled TagLib is used for EXIF/image fields.
- A1111 parameters, NovelAI JSON, and ComfyUI prompt JSON are parsed.
- ComfyUI sampler-style nodes resolve linked `seed`, `steps`, `cfg`, `sampler`, `scheduler`, `model`, `vae`, `denoise`, prompt and size values.

## Single Window
- Normal ZipPla launches with no argument or one folder/file path use the single-instance mutex when enabled.
- Internal `-switch` invocations remain unaffected.
- A second normal launch activates the existing window; a path argument is forwarded to it.

## Bookmark
- External context-menu paths are deduplicated by normalized full path before insertion.
