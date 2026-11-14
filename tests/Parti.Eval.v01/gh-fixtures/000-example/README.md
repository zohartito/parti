# 000-example (template)

Reference fixture. Demonstrates every field in `fixture.json` in one place.

**This is a template, not a runnable fixture.** `before.gh` and `after.gh` are
intentionally missing. The author populates them from a Rhino environment when a
real fixture is authored from this template.

## To use

1. Copy the folder:
   `cp -r 000-example/ <NNN>-<slug>/` (pick the next free id).
2. Replace `fixture.json` fields with your real values. Keep `000-example`
   unchanged so it keeps working as a reference.
3. Save your `before.gh` and `after.gh` into the new folder.
4. Delete the new folder's copy of this README or replace it with fixture-
   specific notes.

The authoritative schema and recording procedure live in
`docs/GH-FIXTURE-SPEC.md`. CI validates `fixture.json` against
`../schema.json`; a missing required field will fail the build.
