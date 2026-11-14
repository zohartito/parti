# prompts/

Versioned system prompts for Parti, published as a skill bundle.

## What lives here

`parti-foundations/` is the product prompt. `PartiComposition` builds it per request with
`SessionPromptBuilder`:

- **Layer 0** — `parti-foundations/SKILL.md`, always loaded. Identity, safety rules (including the
  untrusted-data rule), the tool catalogue, and the workflow. Must stay under 16 KB
  (`Layer0SizeGateTests`).
- **Layer 1** — `parti-foundations/parti-*/SKILL.md`, loaded when `KeywordSkillRouter` matches the
  user's message.
- **Layer 2** — `references/` and `templates/` under each sub-skill, read on demand.

The plugin embeds the bundle as resources (`Parti.Prompts.parti-foundations/...`). If the bundle
cannot be read, Parti falls back to a one-paragraph prompt that keeps the untrusted-data rule.

The old monolithic `system_v0.6.md` was removed in the 2026-09-27 product-wiring wave; it is in git
history.

## Tool catalogue parity

The Layer 0 tool catalogue must list exactly the tools the product registers, with the argument
names and required flags from their schemas. `ToolCataloguePromptParityTests` enforces this, and
also fails if any prompt file names an unregistered tool or a retired SimpleId handle.

## Proposing a change

1. Open a PR that touches only files under `prompts/` (plus optionally `docs/PROMPTS.md`).
2. Include the expected delta from the eval suite — run the relevant fixtures against the old prompt
   and the new prompt, report both numbers in the PR body.
3. Bump the `version` field in the frontmatter of every SKILL.md you change.

## See also

- [docs/PROMPTS.md](../docs/PROMPTS.md) — full governance, loading mechanism, override policy, eval gate.
