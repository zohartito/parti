# Grasshopper Deadline Kit

Small scripts for turning live Rhino/Grasshopper slider exports into usable
submission media.

This kit came from a real Ladybug Direct Sun Hours deadline workflow. The aim is
not to replace careful analysis. The aim is to preserve honest Grasshopper or
Ladybug output and make a deliverable quickly.

## Scripts

- `stitch_grasshopper_slider_sequences.sh`
  Stitches BMP/PNG/JPG sequences exported from Grasshopper into clean and
  labeled MP4/GIF loops.

- `capture_slider_sequences.py`
  Rhino Python template for setting known Grasshopper Number Slider GUIDs,
  solving, and capturing the active Rhino viewport.

- `rhino_view_capture.py`
  Minimal Rhino Python smoke test for active viewport capture.

## Recommended manual export flow

1. Frame the Rhino viewport.
2. Right-click a Grasshopper Number Slider.
3. Choose `Animate...`.
4. Use 8 to 12 frames for deadline work.
5. Export to BMP at 1280 x 720 or 1920 x 1080.
6. Repeat for each slider shot.
7. Stitch the resulting sequences:

```bash
SRC="/path/to/Grasshopper exported frames"
OUT="$SRC/_stitched"

GH_STEMS="GH_SLIDER_PRACTICE_9 GH_SLIDER_PRACTICE_10 GH_SLIDER_PRACTICE_11" \
GH_LABELS="Depth range|Width adjustment|Height adjustment" \
GH_SKIP_COUNTS="1 0 0" \
tools/grasshopper-deadline-kit/stitch_grasshopper_slider_sequences.sh "$SRC" "$OUT"
```

## Output

The stitch script writes:

- `grasshopper_slider_loop.mp4`
- `grasshopper_slider_loop.gif`
- `grasshopper_slider_loop_labeled.mp4`
- `grasshopper_slider_loop_labeled.gif`
- `grasshopper_slider_contact_sheet.png`

## Smoke tests

With ffmpeg installed, run `python3 -m unittest discover -s tests/tools -v`.
The tests generate synthetic frames and never open Rhino.

## Notes

- Relative source and output directories are supported. Inputs are converted to BMP before stitching.
- ffmpeg logs are saved under the output directory in `_logs/`; failures print the log path and last 30 lines to stderr.
- Use `GH_SKIP_COUNTS` when the first exported frame is a bad setup frame.
- If the installed ffmpeg does not include the `drawtext` filter, the labeled
  outputs are still created but text overlays are omitted.
- Prefer the clean GIF for submission boards.
- Prefer the labeled MP4 when the viewer needs to understand which parameter is
  changing.
- Do not use this to fake simulation colors. If a Ladybug result is wrong, mark
  it as unresolved or use the animation only as a parametric geometry demo.
