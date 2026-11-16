# Parti System Prompt Governance

*Last updated 2026-09-27. Applies from v0.8.5 onward. For v0.6 through v0.8.4 history, see `system_v0.6.md` and the v0.6 revision of this document in git history.*

---

## Why we publish the system prompt

Parti is the first AI copilot for Grasshopper that commits its canonical system prompt to the repository. This is a deliberate trust choice.

Three reasons we publish:

1. **Trust.** Users can read exactly how the AI is instructed before they install the plugin. No hidden steering, no surprise persona, no undisclosed guardrails.
2. **Community transparency.** Contributors can propose prompt changes the same way they propose code changes, through a PR with visible diffs and reviewable rationale.
3. **Reproducibility for research.** Eval-suite numbers are tied to specific skill-bundle files. Anyone replicating the results can pin the exact versions.

As of April 2026 we found no other Grasshopper AI copilot that publishes its system prompt as a first-class, versioned file.

---

## What changed in v0.8.5

Through v0.8.4 the system prompt was a single monolithic Markdown file at `prompts/system_vX.Y.md` (most recently `system_v0.6.md`). Every request shipped the full prompt regardless of whether the user needed it.

Starting in v0.8.5 the prompt is split into a three-layer skill bundle under `prompts/parti-foundations/`. Layer 0 is included on every entry call. Layer 1 is selected per message; Layer 2 has a loader API but is not wired into the product chat.

The old monolithic prompt was moved to `prompts/deprecated/system_v0.6.md` in v0.8.5 and removed in the 2026-09-27 product-wiring wave. It survives only in git history.

---

## The three-layer architecture

### Layer 0: Foundations (always in context)

File: `prompts/parti-foundations/SKILL.md`.

This is the only prompt file that ships with every request. It contains:

- The identity line (who Parti is, who the users are).
- The global safety rules and reply-style rules.
- A short index of Layer 1 sub-skills with one-line triggers so the model knows when to ask the router to load one.
- The tool catalogue at headline level (names and one-line descriptions of each `gh_*` tool).

Keep Layer 0 small. Target: under 3 KB of rendered Markdown after the loader strips the YAML frontmatter. Anything that applies only sometimes belongs in Layer 1, not here.

### Layer 1: Sub-skills (lazy-loaded on router match)

Path: `prompts/parti-foundations/<sub-skill-name>/SKILL.md`.

Each sub-skill is a directory that owns one Parti capability area. The `SKILL.md` at its root contains the detailed guidance the model needs when that capability is active. Today's sub-skills include:

- `parti-ghcanvas` — canvas read, write, and layout operations.
- `parti-formalize` — turning a sketched intent into a concrete definition.
- `parti-abstract` — extracting reusable patterns from an existing definition.
- `parti-feedback` — critiquing a definition against stated goals.
- `parti-optimize` — setting up Galapagos, Wallacei, or Octopus runs.
- `parti-fabricate` — preparing a definition for CNC, laser, or 3D print output.

A Layer 1 SKILL.md loads when `KeywordSkillRouter` matches the current message against the sub-skill manifest tags. Each session entry call rebuilds the system prompt; previously selected skills are not retained unless the new message selects them again.

### Layer 2: References and templates (loader API only)

Paths:
- `prompts/parti-foundations/<sub-skill-name>/references/*.md`
- `prompts/parti-foundations/<sub-skill-name>/templates/*.json`

`SkillLoader.LoadLayer2` can read these files, but no product caller or registered tool invokes it. The chat model cannot fetch references or templates on demand. Shipped reference files are available for contributors to read; they do not enter the product prompt.

---

## Directory layout

```
prompts/
  README.md                        Filename conventions and PR rules.
  parti-foundations/
    SKILL.md                       Layer 0. Always in context.
    parti-ghcanvas/
      SKILL.md                     Layer 1. Loaded when router matches.
      references/
        tree-hygiene.md            Layer 2. Loaded on demand.
        id-conventions.md          Layer 2.
      templates/
        range.json                 Layer 2. On-demand JSON skeleton.
        slider.json                Layer 2.
    parti-formalize/
      SKILL.md
      references/
        ...
    parti-abstract/
      SKILL.md
    parti-feedback/
      SKILL.md
    parti-optimize/
      SKILL.md
      references/
        galapagos-config.md
      templates/
        galapagos-default.json
    parti-fabricate/
      SKILL.md
```

Sub-skill directory names start with `parti-` by convention. The directory name is what the router matches against, so pick something short and descriptive.

---

## Loader and router

### SkillLoader

`Parti.Core.Prompts.SkillLoader` resolves a skill path (for example `parti-ghcanvas/SKILL.md` or `parti-optimize/templates/galapagos-default.json`) to the file's text content.

At build time, the `Parti.Components` csproj embeds every file under `prompts/parti-foundations/` as a .NET embedded resource. The `LogicalName` uses this convention:

```
Parti.Prompts.parti-foundations/<relative-path-with-forward-slashes>
```

Worked examples:

| File | LogicalName |
|---|---|
| `prompts/parti-foundations/SKILL.md` | `Parti.Prompts.parti-foundations/SKILL.md` |
| `prompts/parti-foundations/parti-ghcanvas/SKILL.md` | `Parti.Prompts.parti-foundations/parti-ghcanvas/SKILL.md` |
| `prompts/parti-foundations/parti-ghcanvas/references/tree-hygiene.md` | `Parti.Prompts.parti-foundations/parti-ghcanvas/references/tree-hygiene.md` |
| `prompts/parti-foundations/parti-optimize/templates/galapagos-default.json` | `Parti.Prompts.parti-foundations/parti-optimize/templates/galapagos-default.json` |

`SkillLoader` reads the resource stream and returns the text. It also parses and strips any YAML frontmatter between `---` fences before handing the content to the model.

File-system fallback is optional. If the loader cannot find a resource and the running plugin directory has a sibling `prompts/parti-foundations/` folder, it may read from disk instead. This is intended for development builds and for advanced users who want to iterate on a skill without rebuilding the plugin.

### SkillRouter

`Parti.Core.Prompts.SkillRouter` decides which Layer 1 sub-skills are active for a given turn.

The product uses `KeywordSkillRouter`, which discovers sub-skill manifests and matches their inline `tags` against the current message. The Layer 0 index is guidance for the model, not router configuration. `SessionPromptBuilder` combines Layer 0 with the selected Layer 1 bodies on each entry call; it does not accumulate skills across messages.

Layer 2 is not routed or retrieved by the product. `SkillLoader.LoadLayer2` is currently an API exercised only by tests.

### Product wiring

`PartiComposition` builds the product prompt with `SessionPromptBuilder(new SkillLoader(source), new KeywordSkillRouter(source))`. In Rhino the source is `EmbeddedResourceSkillSource` over the plugin assembly; in tests it is `FileSystemSkillSource` over `prompts` (the folder containing `parti-foundations`). The builder runs once per session entry call (a new message, a follow-up, or a resume after `ask_user`). No LLM router is used.

If the bundle cannot be read, the session uses `PartiComposition.FallbackSystemPrompt`, a one-paragraph prompt that keeps the untrusted-data rule. An explicit system prompt passed to the composition replaces the bundle.

The Layer 0 tool catalogue lists exactly the tools the product registers, in the form ``- `name` (read|write|session) — one line. Args: `a*`, `b`.``, where `*` marks a required argument. `ToolCataloguePromptParityTests` checks the catalogue against the registered schemas and scans every prompt file for unregistered tool names and retired SimpleId handles.

---

## Adding a new sub-skill

To add a new sub-skill called `parti-example`:

1. **Create the directory.** `prompts/parti-foundations/parti-example/`.
2. **Write `SKILL.md`.** At the root of the new directory. Include a YAML frontmatter block with required `title`, a `version`, and an inline `tags` list. Multi-line trigger lists are not supported. Use this copyable example:

```markdown
---
title: parti-example
version: 0.1.0
tags: [exampleworkflow]
---
# Example workflow
Help the user explain their example workflow before proposing canvas edits.
```

The body is the guidance the model reads when the sub-skill is active. It is appended to Layer 0 in the same chat loop, so it must not tell the model to reply only in JSON or that its output is hidden from the user: replies are plain text and pillar results are stored with `parti_record_decomposition` (enforced by `ProductPrompt_RoutedSubSkillsHaveNoJsonOnlyDirective`).
3. **Add references and templates if needed.** Under `references/` and `templates/` in the sub-skill directory. Markdown for references, JSON for templates. Keep them small and single-purpose. These files are not currently retrieved by the product chat.
4. **Update Layer 0 triggers.** Add a one-line entry for `parti-example` to the Layer 1 sub-skill index in `prompts/parti-foundations/SKILL.md`. The entry documents the skill for the model; the router reads `tags` from the sub-skill manifest.
5. **Verify the router sees it.** Run the router fixture tests under `tests/Parti.Core.Tests/Prompts/` with a message that should trigger the new sub-skill. Confirm `KeywordSkillRouter` selects `parti-example` and `SkillLoader` loads its body. `DocumentedSkillTests.AddingSubSkill_DocumentedManifestLoadsAndRoutes` exercises the example above with `FileSystemSkillSource` rooted at a folder containing `parti-foundations`.
6. **Rebuild.** `dotnet build src/Parti.Components/Parti.Components.csproj`. The MSBuild glob picks up new files automatically, no csproj edit needed.
7. **Verify the resource landed.** Inspect `Parti.dll` (or its `Parti.gha` copy) manifest resource names and confirm `Parti.Prompts.parti-foundations/parti-example/SKILL.md` is present.

No code change is required to register a new sub-skill beyond the content files above. The router and loader discover sub-skills by walking the embedded resource namespace, not by reading a static registry.

---

## Versioning policy

The old `system_vMAJOR.MINOR.md` filename convention applied to a single file. With the split bundle, versions now apply at the bundle level.

- **Bundle MINOR bump** — additive: new sub-skill directory, new Layer 2 reference, or a clarification inside an existing SKILL.md that does not break fixture tests.
- **Bundle MAJOR bump** — breaking: identity line rewrite in Layer 0, removal of a sub-skill, or any change that breaks one or more existing fixture tests.

Each individual SKILL.md file carries its own version in YAML frontmatter for traceability. The bundle version is recorded in `prompts/parti-foundations/SKILL.md` (Layer 0) as the authoritative number.

Worked examples under the new scheme:

| From | To | Change | Bump |
|---|---|---|---|
| 0.8.5 | 0.8.6 | Add `parti-collaborate` sub-skill | MINOR |
| 0.8.6 | 0.8.7 | Tighten clarifying-question guidance in `parti-foundations/SKILL.md` | MINOR |
| 0.8.7 | 0.9.0 | Remove `parti-fabricate` sub-skill, fold into `parti-ghcanvas` | MAJOR |
| 0.9.0 | 0.9.1 | Add Galapagos-default template under `parti-optimize/templates/` | MINOR |

When in doubt, run the eval suite against both old and new bundles and let the numbers decide.

---

## Override mechanism

Advanced users can bypass the published bundle by passing a custom string to `SessionOptions.SystemPrompt` when constructing a `ConversationSession`. If `SystemPrompt` is non-null and non-empty, the published Layer 0 file is not loaded for that session, and `SkillRouter` is disabled; the session runs with exactly the string the caller supplied.

This is intended for:

- Researchers running ablations against the canonical bundle.
- Enterprise deployments with additional compliance instructions to append.
- Internal A/B testing of proposed bundle changes before PR.

If you override the prompt, the eval-suite numbers published in the Parti README no longer apply to your session. Report your override text alongside any published results.

Partial overrides (swap a single sub-skill) are not yet supported. Open an issue if you need it.

---

## Eval gate

Any PR that changes a file under `prompts/parti-foundations/` must include eval-suite numbers before and after.

The rules:

- **Improvement or flat** lands after code review.
- **Drop of up to 5%** lands if the PR author explains why the drop is acceptable (for example, a tradeoff that pays off in a harder fixture set).
- **Drop of 5% to 10%** requires maintainer discussion in the PR. Usually held.
- **Drop greater than 10%** requires explicit maintainer sign-off with rationale written into the PR body.

The eval suite lives at `tests/Parti.Core.Evals/` and runs in CI. For the exact fixture format and scoring methodology, see [EVAL-v0.1.md](EVAL-v0.1.md).

New sub-skills should ship with at least three fixture tests that trigger the router to load them. A sub-skill with no fixtures is a PR blocker.

---

## Security note

Prompts are not a security boundary.

Do not rely on system-prompt instructions, whether Layer 0, Layer 1, or Layer 2, to enforce access control, rate limits, or destructive-tool gating. A motivated user can prompt-inject, jailbreak, or simply override the bundle via `SessionOptions.SystemPrompt`.

Real gating happens in the `ToolRegistry` and the dispatcher:

- Tools that mutate the canvas are wrapped in a `GH_UndoRecord` batch, so the user can always revert.
- Tools declare an `IsReversible` flag that surfaces in the UI before execution.
- Destructive operations go through an approval hook that the plugin, not the LLM, controls.

Treat every skill file as editorial guidance to the model, not as a lock.

---

## Deprecation note: the old `system_v0.6.md`

Through v0.8.4, the canonical prompt was `prompts/system_v0.6.md`, a single file of around 20 lines covering identity, tool catalogue, reply style, and safety in one block.

In v0.8.5 it was moved to `prompts/deprecated/system_v0.6.md` with a deprecation header prepended and kept as an embedded resource for one release cycle.

It was removed on 2026-09-27, together with its embedded resource and `SystemPromptLoader`. The product reads only the 3-layer bundle. The old file is available in git history.

---

## Links

- [prompts/parti-foundations/SKILL.md](../prompts/parti-foundations/SKILL.md) — Layer 0, the always-in-context prompt.
- [prompts/README.md](../prompts/README.md) — filename convention and PR rules.
- [docs/EVAL-v0.1.md](EVAL-v0.1.md) — eval-suite methodology referenced by the eval gate.

Parti publishes its prompts, including the layered skill bundle, so that anyone can read, pin, and reproduce them.
