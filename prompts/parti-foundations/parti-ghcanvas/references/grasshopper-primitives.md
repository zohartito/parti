# Grasshopper Primitives

Parti-generated canvases use a small vocabulary of Grasshopper primitives as the backbone of every graph. This reference is the cheat sheet parti-ghcanvas consults when deciding which primitive expresses a given Input, Rule, or intermediate computation. Primitives listed here are either native Grasshopper or belong to ubiquitous tagging plugins (MetaHopper, Elefront) that Parti assumes are installed; anything heavier goes through `./plugin-intent-router.md`.

## Number Slider

Native. The workhorse for numeric Inputs.

**What it is.** A single scalar value with a min, a max, a current value, and an optional step (rounding). The slider has rounding modes: Float (continuous), Integer (whole numbers), Even, Odd.

**When to use.** For any numeric Input with a bounded domain. `panel_depth_mm` with domain `100..400` becomes a Number Slider with min=100, max=400, a sensible default (250 - the domain midpoint is usually fine), and rounding set to match the unit's granularity.

**Naming convention.** The nickname should match the Input's `name` from the Decomposition verbatim. parti-optimize looks up sliders by nickname to build genomes.

**Default value rule of thumb.** A slider's default should be visually interesting at canvas placement - set it to the domain midpoint or the Input's stated default. A slider sitting at min=0 often produces zero-size geometry and looks like the canvas is broken.

## Panel

Native. Text passthrough and canvas-visible documentation.

**When to use.**

- Displaying a value downstream of a computation so the user can see it live. Wiring a result into a Panel is the fastest "print debug".
- Inline documentation. A Panel whose text reads "Rule: panels must not overlap" sits next to the overlap-check component and tells any reviewer what the check enforces.
- Qualitative Inputs. An Input whose domain is free text ("site name", "material description") becomes a Panel the user types into.
- Embedding the Principle on the canvas as a visible reminder.

**When NOT to use.** Do not use a Panel as a data source for numeric work - use a Slider. Panels can feed numbers downstream, but Parti treats Sliders as the canonical numeric source so parti-optimize can find them.

## Expression

Native. Single-line formulas.

**When to use.** Quick scalar math that would otherwise be three or four dedicated components. `A * 0.001` (mm to m conversion), `(A + B) / 2` (midpoint), `if(A > B, 1, 0)` (conditional), `A % B` (modulo for pattern logic).

**When NOT to use.**

- For anything longer than one line - use a C# Script or Python component instead.
- For geometry operations - Expression is scalar only. A Move vector goes through Construct Vector or similar.
- For Rule checks. A Rule that compares two booleans is clearer as an explicit AND/OR gate than as an Expression.

**Input vocabulary.** `x`, `y`, `z` or `A`, `B`, `C`, `D` for the expression inputs. Functions: `sin`, `cos`, `tan`, `log`, `exp`, `abs`, `min`, `max`, `if`. Constants: `pi`, `e`.

## Group

Native. Visual organization only.

**When to use.** After parti-ghcanvas has placed a logical chunk of the graph (all inputs, or the rule-check cluster, or the evaluation section), wrap the chunk in a Group with a descriptive label: "Inputs", "Principle: staggered rainscreen", "Feedback: sun hours". Groups survive Ctrl+Z and let the user fold sections.

**When NOT to use.** Do not rely on Groups for data flow - they are purely visual. Do not use a Group as a substitute for naming individual components.

**Color convention.** Parti uses three colors loosely: light blue for Inputs, pale yellow for Principle/geometry, pale green for Feedback/evaluation. Consistent coloring helps the user's eye track the dataflow.

## MetaHopper (tagging)

Third-party but assumed present in most Parti-leaning Rhino installs. MetaHopper provides component metadata and bulk selection tools.

**When to use.** Parti uses MetaHopper's tagging to mark components it placed in a turn. A tag like `parti:turn=42` lets the orchestrator find and undo its own components without touching user-placed ones.

**When NOT to use.** If MetaHopper is not installed, fall back to nickname conventions (`parti-sliders`, `parti-evaluate`) and warn the user that undo granularity will be coarser.

## Elefront (baking and block instances)

Third-party. Elefront is the Parti-preferred path for block-instance work and for controlled baking with layer and attribute assignments.

**When to use.**

- Creating Rhino block instances from Grasshopper (repetitive geometry where blocks are cheaper than individual Breps).
- Baking with controlled layer assignment so the user's Rhino document stays organized.
- Tagging baked geometry with attributes parti-ghcanvas can reference later.

**When NOT to use.** For a one-off Bake during exploration, native Bake is enough. Pull in Elefront only when the user's Principle implies repetitive blocks or when baked attribute data matters.

## Choosing between primitives

When drafting a graph, parti-ghcanvas walks the Decomposition in this order:

1. Every Input becomes a Slider (numeric) or Panel (qualitative/text).
2. Every Rule either becomes a Panel note (cannot be live-checked) or a boolean-output component group with an accompanying Panel explaining the check.
3. The Principle drives the geometry backbone. Native components first; plugins only when the router surfaces a match and the plugin is installed.
4. Evaluations become tagged Panels (to display the metric value) and, if parti-optimize was invoked, slider-genomes wired into a solver component.
5. Everything gets grouped with a descriptive label.
6. MetaHopper tags applied last so the undo scope is correct.

The canvas reads left to right: Inputs at left, geometry in the middle, Evaluations at right. Parti's `gh_layout` call enforces this after components are placed.

