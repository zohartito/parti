# GH Fixture Spec (Eval v0.2)

## Purpose

This spec defines the on-disk shape of a **.gh fixture** — the unit of evaluation
used by Parti's v0.7 eval harness. A .gh fixture is a triple: a real Grasshopper
definition captured in a known state, the expected Grasshopper definition after
Parti handles a user prompt, and a JSON sidecar describing the prompt and the
checks that should pass. We found no other Grasshopper AI plugin that publishes
fixtures (survey of public materials, April 2026). Parti's
aim is that fixtures, prompts, and
pass rates all ship in the open, so the work can be reproduced and the claims
audited. v0.5 shipped 10 hermetic JSON fixtures against a fake provider; v0.7 is
targeting 50 .gh fixtures against real Rhino state.

## Directory layout

```
tests/Parti.Eval.v01/gh-fixtures/
  <id>-<slug>/
    before.gh        # canvas state when the prompt is issued
    after.gh         # canvas state the fixture asserts against
    fixture.json     # prompt, expectations, metadata
```

One fixture per folder. The folder name is the fixture id: a zero-padded 3-digit
number, a hyphen, then a kebab-case slug. The number orders fixtures in reports;
the slug makes `git diff` legible. Example: `007-connect-slider-to-circle`.

## fixture.json schema

```json
{
  "id": "007-connect-slider-to-circle",
  "title": "Connect slider to circle radius",
  "description": "User has a disconnected slider and circle. Asks to wire them up.",
  "userPrompt": "connect the radius slider to the circle",
  "category": "connect",
  "difficulty": "easy",
  "toolExpectations": [
    { "tool": "gh_connect", "minCalls": 1, "maxCalls": 1 }
  ],
  "canvasAssertions": [
    "componentExists: Slider named 'radius'",
    "componentExists: Circle",
    "connectionExists: Slider radius → Circle R"
  ],
  "notes": "Seed case for the connect tool. Slider/Circle already exist in before.gh."
}
```

Field reference:

- `id` — must equal the folder name.
- `title` — short human label (under 60 chars). Shown in the report.
- `description` — one or two sentences describing what the fixture is testing.
- `userPrompt` — the verbatim string fed to the session. Write it the way a real
  user would type it. Do not pre-disambiguate.
- `category` — one of `add`, `edit`, `describe`, `connect`, `mixed`. Drives the
  coverage matrix in the eval report.
- `difficulty` — one of `easy`, `medium`, `hard`. See rubric below.
- `toolExpectations` — array of `{ tool, minCalls, maxCalls }`. Bounds the number
  of calls per tool; use `minCalls == maxCalls` for exact counts. Omit tools you
  don't care about.
- `canvasAssertions` — array of predicate strings evaluated against `after.gh`.
  The supported predicate forms as of v0.7:
  - `componentExists: <Type> named '<name>'` — component of the given type whose
    nickname or name matches.
  - `componentExists: <Type>` — any one component of the given type.
  - `componentCount: <Type> == <n>` — exact count.
  - `connectionExists: <SrcType> <srcName> → <DstType> <input>` — wire from the
    named source's output to the named input port on a component of DstType.
  - `inputValue: <Type> named '<name>' <input> == <value>` — exact input value.
- `notes` — free-form prose. Reviewer context, known gotchas, citations.

## Id + slug convention

- 3-digit zero-padded number, monotonically increasing. Reserve `000` for the
  template. Numbers do not have to be dense — leave gaps when grouping.
- Slug is lowercase, words separated by hyphens, no punctuation, max ~5 words.
- The same id appears as the folder name, the `id` field, and any reference
  from the eval report. They must match byte for byte.

## Recording a fixture from a live Rhino session

1. Pick a concrete scenario. Write the `userPrompt` first, in one short line.
2. Open Rhino 8. Build the starting canvas by hand. Keep it minimal — only the
   components the prompt actually references.
3. Save the canvas as `tests/Parti.Eval.v01/gh-fixtures/<id>-<slug>/before.gh`.
4. Run Parti against the prompt, or do the edit manually the way you'd want
   Parti to do it.
5. Save the resulting canvas as `after.gh` in the same folder.
6. Create `fixture.json` next to the two .gh files. Copy the `000-example`
   template and fill every field.
7. Hand-write `canvasAssertions`. Look at `after.gh` and list only the facts
   that define success — component existence, wire presence, input values.
   Avoid asserting layout or anything not tied to the prompt.
8. Verify: `before.gh` and `after.gh` both open in Rhino 8, `fixture.json`
   validates against `schema.json`, the fixture folder name equals `id`.

## Definition of done (per fixture)

A fixture is done when all of:

- `before.gh` opens in Rhino 8 without "missing component" warnings.
- `after.gh` opens in Rhino 8 without "missing component" warnings.
- `fixture.json` validates against `schema.json` (CI enforces this).
- Every predicate in `canvasAssertions` is expressible in the predicate
  vocabulary listed above. If you need a new predicate form, add it to this
  spec first, then use it.
- `userPrompt` reads like something a real user would type — no prompt
  engineering to force a particular tool sequence.

## Difficulty rubric

| Level | Condition |
|-------|-----------|
| easy | Exactly 1 tool call expected. No branching. |
| medium | 2 or 3 tool calls. Linear sequence, no conditionals. |
| hard | 4+ tool calls, or conditional logic (model must re-plan after a tool result), or requires name resolution across ambiguity. |

Difficulty is about the model's task, not the canvas size. Adding one component
to a 40-component canvas is still `easy` if only one tool call is needed.

## Anti-patterns

Fixtures that look plausible but teach the harness nothing, or worse, encode
fragile expectations:

- **Ambiguous prompt.** `"fix it"` with no referent. If a human can't tell what
  success means, the fixture can't either. Rewrite or drop.
- **Over-specified `after.gh`.** Asserting component positions, GUIDs, or any
  detail the prompt didn't mention. Keep `canvasAssertions` tight to what the
  user asked for.
- **Prompt that names Parti's tools.** `"call gh_add_component with type=Slider"`
  trains the harness on the tool layer, not the user layer. Use natural phrasing.
- **Assertions that only one implementation passes.** If `gh_add_component`
  then `gh_set_input` is equivalent to a single `gh_add_component` with the
  input set at creation time, assert the observable outcome, not the tool path
  — unless the point of the fixture is specifically to exercise a tool.
- **Fixture duplicates another fixture's coverage.** Before adding one, scan
  existing ids in the same category. The point of 50 fixtures is breadth.
- **`before.gh` or `after.gh` with unsaved Rhino geometry.** The .gh file is a
  Grasshopper definition. Rhino-side geometry does not round-trip and should
  not be part of the fixture.
