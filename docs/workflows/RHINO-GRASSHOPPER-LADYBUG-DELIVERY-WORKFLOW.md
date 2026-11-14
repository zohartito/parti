# Rhino, Grasshopper, and Ladybug Deadline Delivery Workflow

This workflow captures the process used during a live Rhino 8 / Grasshopper /
Ladybug deadline session where the goal was to debug a false-color direct-sun
demonstration and then produce a usable animation under time pressure.

The core rule: preserve the truth of the analysis. Do not recolor results to
make a diagram say something the Ladybug result does not say.

## When to use this

Use this workflow when a Grasshopper definition is already open in Rhino and the
user needs one of these outcomes quickly:

- diagnose a Ladybug Direct Sun Hours / SunPath problem;
- understand a dense "green blob" region of a Grasshopper definition;
- animate selected Number Sliders;
- stitch exported slider frames into a looping GIF or MP4;
- preserve trial-and-error work for future Grasshopper automation.

## First pass: freeze the situation

1. Save or duplicate the open Rhino and Grasshopper files before patching.
2. Capture screenshots of:
   - the Rhino viewport;
   - the Grasshopper canvas region in question;
   - any selected or blue-highlighted sliders;
   - the Ladybug result and legend.
3. Export or write a small text tracker with:
   - active Rhino file name;
   - Grasshopper definition name;
   - Ladybug components involved;
   - slider values and ranges;
   - which geometry parameters are referenced from Rhino.
4. If the user is upset that it "looked better before," treat rollback as a
   first-class option instead of continuing to patch.

## Research before editing

Before touching the graph, confirm these component semantics:

- Ladybug Direct Sun Hours result color is driven by the component result and
  `legend_par_`, not by arbitrary Rhino display color.
- SunPath `north_` changes the solar orientation convention. Do not use it as a
  cosmetic fix.
- Standard Grasshopper `Rotate.Angle` expects radians. A slider displaying `180`
  does not mean 180 degrees unless it passes through a degrees-to-radians
  conversion.
- Rhino-referenced geometry can be the real bug: orientation, face normals,
  model units, referenced surface order, or a moved/rotated context object can
  make the solar result look wrong even if the Ladybug components are wired.

## Direct-sun debugging checklist

1. Verify `AnalysisPeriod` feeds both `SunPath` and `DirectSunHours` when the
   demo is supposed to be time-based.
2. Verify `SunPath` and `DirectSunHours` share the same EPW location and HOYs.
3. Verify the `_geometry` input is the actual analysis geometry, not only the
   glass or not only a cut face.
4. Verify `context_` is either intentionally empty or intentionally includes
   only shading/blocking geometry.
5. Clear custom `legend_par_` while debugging unless the user explicitly wants
   a non-default legend.
6. Check if a red Ladybug component is reporting a real missing input, a stale
   runtime warning, or an invalid upstream data tree.
7. If the whole wall becomes blue, do not fix by changing colors. Check:
   - sun orientation;
   - facade rotation;
   - whether the surface normal faces the sun;
   - whether the analysis geometry is self-blocking;
   - whether the selected HOY has the expected altitude/azimuth;
   - whether the Rhino model is north-facing when the design intent is south.

## Backtrack rule

When analysis output gets worse after edits:

1. restore original Ladybug wiring first;
2. remove experimental context geometry;
3. clear temporary legend overrides;
4. restore annual or intended analysis period;
5. only then try orientation or slider changes again.

This prevents cosmetic "fixes" from hiding the actual problem.

## Deadline branch

When there is not enough time to fully debug the solar model:

1. Stop rewiring the Ladybug graph.
2. Keep the best honest viewport state.
3. Animate the real Number Sliders.
4. Export raw frames.
5. Stitch the frames into GIF/MP4.
6. Document caveats separately.

The delivery is still useful because it shows the parametric relationship
between depth, width, height, and shading. It should not be presented as a
validated final simulation if the solar setup is still unresolved.

## Manual Grasshopper slider animation

Grasshopper has a built-in slider animation path:

1. Frame the Rhino viewport exactly as the final animation should look.
2. In Grasshopper, right-click the Number Slider.
3. Choose `Animate...`.
4. Set a short frame count for deadline work: 8 to 12 frames is often enough.
5. Use a clear filename template, for example:
   - `FINAL_01_DEPTH_{0:000}.bmp`
   - `FINAL_02_WIDTH_{0:000}.bmp`
   - `FINAL_03_HEIGHT_{0:000}.bmp`
6. Use 1280 x 720 for fast export or 1920 x 1080 if the graph solves quickly.
7. Let Grasshopper finish. It can appear frozen while it solves and captures.
8. If the filename was not changed and the sequence is called something like
   `GH_SLIDER_PRACTICE_10_000.bmp`, salvage it by mapping that sequence to the
   intended shot name.

## Stitching frames

Use `tools/grasshopper-deadline-kit/stitch_grasshopper_slider_sequences.sh`.

Example:

```bash
SRC="/path/to/new animation frames"
OUT="/path/to/exported-loop"
GH_STEMS="GH_SLIDER_PRACTICE_9 GH_SLIDER_PRACTICE_10 GH_SLIDER_PRACTICE_11" \
GH_LABELS="Depth range|Width adjustment|Height adjustment" \
GH_SKIP_COUNTS="1 0 0" \
tools/grasshopper-deadline-kit/stitch_grasshopper_slider_sequences.sh "$SRC" "$OUT"
```

The script creates:

- clean looping MP4;
- clean looping GIF;
- labeled looping MP4;
- labeled looping GIF;
- contact sheet for fast review.

## After Effects path

After Effects is useful for presentation polish, but it is not the fastest
deadline path. Prefer ffmpeg for first delivery. Use After Effects only after a
valid MP4/GIF exists.

If using AE:

1. Import the first frame of each BMP sequence as an image sequence.
2. Interpret footage at the desired frame rate.
3. Place each sequence on the timeline in order.
4. Add text labels if needed.
5. Export an MP4.
6. Make GIFs from the MP4 with ffmpeg.

## Parti product lessons

This session is a good Parti fixture candidate because it exposed real user
needs that a Grasshopper agent should support:

- inspect selected/colored canvas groups;
- map Number Slider GUIDs, ranges, current values, positions, and recipients;
- detect unit mismatch risks like degree sliders feeding radian inputs;
- snapshot and restore Ladybug wiring;
- batch-export slider animation frames;
- maintain a live progress tracker;
- choose between "analysis debugging" and "deadline delivery" modes;
- preserve analysis honesty by refusing cosmetic color lies.

