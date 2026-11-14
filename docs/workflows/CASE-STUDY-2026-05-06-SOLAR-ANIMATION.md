# Case Study: Ladybug Solar Animation Recovery, 2026-05-06

## Situation

A Rhino 8 and Grasshopper definition was open during a deadline review. The
user needed a visual demonstration of a punched facade opening whose depth,
width, and height were tuned around sun and shade behavior.

The relevant Grasshopper region was a large green grouped outline containing:

- Ladybug EPW download/import;
- Analysis Period;
- SunPath;
- Direct Sun Hours;
- referenced Rhino geometry;
- rotated/moved facade geometry;
- several blue-highlighted Number Sliders;
- downstream geometry operations for the punched box.

The immediate symptoms were:

- the Ladybug component had been red at times;
- the SunPath coloring changed unexpectedly;
- the false-color result became mostly blue;
- the inside bottom ledge was not showing the expected sun patch;
- a desired sun angle was described as around 71 deg;
- the user did not want custom colors or a fake yellow wall.

## Principles used

1. Do not lie with color.
   The user explicitly rejected palette hacking. The job was to show actual
   Ladybug output or make a separate non-analysis animation.

2. Research and inspect before editing.
   The graph had Rhino references and many connected components. Changing
   isolated sliders without understanding the wiring could make things worse.

3. Backtrack when visual output regresses.
   Restoring default Ladybug wiring was safer than continuing experimental
   component edits.

4. Switch modes when the deadline wins.
   Once the analysis could not be fully validated in time, the goal shifted to
   making a useful animation from real slider captures.

## Useful discoveries

### Direct Sun Hours wiring

The Direct Sun Hours component should receive the intended analysis geometry,
the intended time period, and an intentional context input. Temporary context
geometry and legend overrides can make the visual story confusing. During
debugging, clearing `legend_par_` and restoring the original `_geometry` source
helped return the result to a known state.

### Sun orientation

The facade appeared to need a 180-degree orientation change to behave like a
south-facing demo. However, standard Grasshopper `Rotate.Angle` inputs are
radians. A slider reading `180` can be suspicious if it is wired directly to
`Rotate.Angle` without a radians conversion component. This became a key
diagnostic note, but not something to keep pushing during the deadline.

### Blue result

The large blue wall was not fixed by changing the color palette. Possible real
causes included:

- facade not oriented toward the sun;
- surface normals or referenced surfaces facing the wrong way;
- context/self-blocking geometry;
- a selected HOY or analysis period with low direct exposure;
- a mismatch between design intent and Rhino north.

### Manual slider animation

Grasshopper's native `Number Slider > Animate...` worked and produced BMP
frames. The fragile part was naming and saving the sequences consistently. Some
exports landed as `GH_SLIDER_PRACTICE_9_000.bmp`, `10`, and `11` instead of the
planned names. This was salvageable by remapping stems after export.

### ffmpeg was faster than After Effects

After Effects was an acceptable finishing tool, but ffmpeg produced the needed
looping GIF/MP4 faster and with less UI risk. The fastest successful path was:

1. export BMP sequences from Grasshopper;
2. remove bad setup frames;
3. stitch the sequences;
4. make a clean GIF and a labeled GIF;
5. make a contact sheet to verify the frames.

## What worked

- Keep a text tracker of slider GUIDs, values, and component wiring.
- Use screenshots/contact sheets for fast visual sanity checks.
- Use native Grasshopper slider animation for real parametric movement.
- Use ffmpeg for GIF/MP4 assembly.
- Preserve default Ladybug color behavior unless there is a clear analytical
  reason to customize the legend.
- Create a "deadline branch" of the workflow so the project can be submitted
  even if the simulation is not perfectly debugged.

## What did not work well

- UI automation of right-click menus was brittle.
- Capturing the viewport while Rhino was framed on empty space created blank or
  nearly useless practice frames.
- Continuing to adjust Ladybug orientation and colors under time pressure risked
  making the output less trustworthy.
- Absolute local paths made quick scripts useful in the moment but not portable.

## Reusable artifacts created from this case

- `tools/grasshopper-deadline-kit/stitch_grasshopper_slider_sequences.sh`
  converts exported Grasshopper BMP sequences into clean and labeled MP4/GIF
  loops.
- `tools/grasshopper-deadline-kit/capture_slider_sequences.py` is a Rhino Python
  template for programmatic slider captures when manual animation is not enough.
- `tools/grasshopper-deadline-kit/rhino_view_capture.py` is a minimal active
  viewport capture smoke test.
- `docs/workflows/RHINO-GRASSHOPPER-LADYBUG-DELIVERY-WORKFLOW.md` is the
  repeatable checklist.

## Future Parti features suggested

- A `gh_find_sliders` tool that returns GUID, nickname, current value, range,
  canvas bounds, and downstream recipients.
- A `gh_trace_region` tool that summarizes everything inside a selected group.
- A `gh_detect_units_mismatch` pass for degree sliders wired into radian inputs.
- A `gh_snapshot_ladybug` and `gh_restore_ladybug` pair.
- A `gh_animate_slider` tool that exports frames without relying on right-click
  UI.
- A mode switch between `debug-analysis` and `deadline-deliverable`.

