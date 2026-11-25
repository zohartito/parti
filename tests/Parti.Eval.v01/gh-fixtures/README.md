# GH fixtures (Parti Eval v0.2, planned)

Planned folder for real `.gh` fixtures against a live Rhino 8 session.
There is currently no live `.gh` runner and no CI schema-validation job. Complement to `../fixtures/`, which holds the hermetic
JSON fixtures used by v0.1.

## Quickstart

To add a fixture:

1. Copy `000-example/` to a new `<NNN>-<slug>/` folder. Pick the next free id.
2. Replace `before.gh`, `after.gh`, and `fixture.json` with the real artefacts
   (see [`docs/GH-FIXTURE-SPEC.md`](../../../docs/GH-FIXTURE-SPEC.md) for the recording procedure).
3. Manually validate `fixture.json` against `schema.json` and open both `.gh`
   files in Rhino to inspect them. Building the repository does neither of these
   checks; automated validation and a live runner remain planned.

The full schema, predicate vocabulary, difficulty rubric, and anti-pattern list
live in `docs/GH-FIXTURE-SPEC.md`. Do not duplicate those here — edit one place.

## Current count

0 fixtures. Target for v0.7 is 50.

The folder is seeded from the author's Rhino environment. The example fixture
(`000-example/`) is a template only — its `.gh` files are intentionally absent
because real captures have to come out of a live Rhino session.

## Why .gh fixtures are deferred

v0.5 shipped the hermetic JSON fixtures under `../fixtures/`: they test the
conversation loop, tool dispatcher, and provider decode path without touching
Rhino. That was deliberate — it let the loop land before the Rhino integration
was stable. Real `.gh` fixtures require a live Rhino 8 session and the mutation
tools (`gh_connect`, `gh_set_input`, `gh_delete`) that are v0.6 scope. Recording
them before those land would waste the recordings.

Until then, reported numbers come from the v0.1 JSON fixtures, with `.gh`
fixtures declared as v0.7 work. This folder — schema, example, and README —
states that plan concretely.
