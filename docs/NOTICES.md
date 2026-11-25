# Third-Party Notices

Parti is licensed under LGPL-3.0-or-later (see `LICENSE` and `COPYING.LESSER`).
This file acknowledges third-party work Parti builds on, ports from, or references
as prior art. See `ATTRIBUTION.md` for the long-form attribution narrative.

## Prior art — architectural patterns

The following projects informed Parti's architecture. **No code from these projects
has been ported into Parti at the time of this release.** They are listed here because
their documented patterns shaped Parti's design decisions. If/when code is actually
ported, it will be relicensed under LGPL-3.0-or-later (compatible licenses only) and
this file will be updated to credit the original authors on a per-file basis.

### Amanbh997 — Claude-skills-for-Computational-Designers

- Upstream: <https://github.com/Amanbh997/Claude-skills-for-Computational-Designers>
- License: MIT (see the upstream repository)
- Status: **Architectural reference only — no code ported**
- What Parti borrowed conceptually:
  - YAML frontmatter on `SKILL.md` files (title, description, tags, auto_activate, invocation)
  - Tag-driven generated router derived from manifests
  - Layer-0 size budget discipline for progressive disclosure

## Upstream notices and licenses ported with code

### SmartHopper — LGPL-3.0-or-later

Parti includes code ported or adapted from
[SmartHopper](https://github.com/architects-toolkit/SmartHopper), including provider
contracts, interaction types, the Anthropic provider and conversation-session
code. The per-file source mapping is maintained in
[ATTRIBUTION.md](../ATTRIBUTION.md#smarthopper-lgpl-30-or-later); adapted files retain
SPDX and port-attribution headers. See [LICENSE](../LICENSE) and
[COPYING.LESSER](../COPYING.LESSER) for the repository license texts.

## Fixture license

The fixtures and predicate code are distributed under the repository's
**LGPL-3.0-or-later** license. No separate MIT or MIT-equivalent fixture grant is
provided in this repository. Any different grant would require an explicit
licensing decision by the rights holders.

## Runtime dependencies

Parti's runtime NuGet dependencies keep their own licenses and notices; see each
package's metadata. The direct dependency set is tracked in the individual `.csproj`
files under `src/`.
