# Plugin Intent Router - keyword map

Parti is useful most of the time with native Grasshopper components, but several problem shapes genuinely want a third-party plugin. The Plugin Intent Router scans the user's prompt for plugin-shaped keywords, maps them to canonical plugin names, and reports both the confidence level and whether the plugin is actually installed on the user's machine. The router is stateless and pure; the installed-status check is done by a paired `GhPluginScanner` pass. The source of truth is `src/Parti.Core/Design/PluginIntentRouter.cs`.

Parti uses the router at two points: before parti-ghcanvas commits components (to warn the user if an implied plugin is missing) and inside parti-optimize (to check solver availability).

## Keyword to plugin mapping

Keywords are matched case-insensitively as substrings of the lowercased prompt. Direct plugin names get confidence 1.0; capability phrases get 0.5 to 0.9 depending on how specific they are.

### Direct plugin names (confidence 1.0)

| Keyword | Canonical plugin |
|---------|------------------|
| kangaroo | kangaroo |
| kangaroo2 | kangaroo |
| ladybug | ladybug |
| honeybee | honeybee |
| butterfly | butterfly |
| dragonfly | dragonfly |
| lunchbox | lunchbox |
| anemone | anemone |
| weaverbird | weaverbird |
| pufferfish | pufferfish |
| dendro | dendro |
| elefront | elefront |
| elk | elk |
| galapagos | galapagos |
| octopus | octopus |
| wallacei | wallacei |

When a direct name matches, the router emits confidence 1.0 and assumes the user is explicit about tool choice.

### Capability phrases (medium confidence)

These phrases imply a plugin by describing what the user wants to do, rather than naming the plugin directly.

| Keyword | Canonical plugin | Confidence |
|---------|------------------|------------|
| daylight | ladybug | 0.6 |
| sun hours | ladybug | 0.7 |
| solar radiation | ladybug | 0.7 |
| energy model | honeybee | 0.7 |
| cfd | butterfly | 0.6 |
| urban heat | dragonfly | 0.6 |
| tension | kangaroo | 0.7 |
| form finding | kangaroo | 0.8 |
| catenary | kangaroo | 0.8 |
| relaxation | kangaroo | 0.7 |
| physics | kangaroo | 0.6 |
| voxel | dendro | 0.7 |
| sdf | dendro | 0.7 |
| loop | anemone | 0.5 |
| iterative | anemone | 0.5 |
| subdivision | weaverbird | 0.7 |
| catmull | weaverbird | 0.8 |
| catmull-clark | weaverbird | 0.9 |
| fillet | pufferfish | 0.5 |
| blend | pufferfish | 0.5 |
| openstreetmap | elk | 0.8 |
| osm | elk | 0.8 |
| block instance | elefront | 0.7 |
| bake | elefront | 0.5 |

### Solver keywords (medium confidence)

These imply a specific Grasshopper optimizer rather than a geometry plugin.

| Keyword | Canonical solver | Confidence |
|---------|------------------|------------|
| genetic | galapagos | 0.6 |
| multi-objective | octopus | 0.7 |
| pareto | octopus | 0.7 |
| nsga | wallacei | 0.8 |
| evolutionary | wallacei | 0.6 |

Note: Galapagos ships with Rhino and is always available. Octopus and Wallacei are third-party and must be preflight-checked.

## Flow

1. The user's message arrives at the orchestrator.
2. `PluginIntentRouter.Route(message, installedPlugins)` runs the keyword scan, aggregates matches per canonical plugin (so "daylight" and "sun hours" both pointing at ladybug count as one intent with `MaxConfidence=0.7` and both terms in `MatchedTerms`), and returns a list of `PluginIntent` records.
3. Each `PluginIntent` carries `IsInstalled`: `true` when the `GhPluginScanner` found the plugin, `false` when it did not, `null` when no scanner result was supplied (pure-intent mode).
4. parti-ghcanvas consumes the list. For every implied-but-missing plugin, it either:
   - tells the user which plugin the approach needs and falls back to the closest native chain; OR
   - pauses the turn via parti-clarify and asks the user whether to install, fall back, or abort.
5. When multiple plugins match (e.g. "daylight energy model" implicates both ladybug and honeybee), parti-ghcanvas picks the best-fit based on the Principle and mentions the alternate in the final reply.

## Rules of thumb

- Direct plugin names (confidence 1.0) are treated as user commitment. Do not override unless the plugin is uninstalled and the user has no way to install it in this session.
- Capability phrases at confidence 0.7+ are strong signals. Respect them unless a native chain is genuinely simpler.
- Confidence 0.5 matches are weak - prefer native components unless the rest of the Decomposition pushes toward the plugin.
- Solvers chosen by parti-optimize have their own decision logic (see `../SKILL.md` for parti-optimize). The router helps, it does not decide.
- The router is keyword-based and lossy. If the user's prompt uses unusual phrasing, the router may miss. parti-ghcanvas should still read the Principle and judge for itself whether a plugin is implied.

## Example traces

**Prompt:** "Use Kangaroo2 to simulate tension in a catenary."
Router returns: one intent, plugin=`kangaroo`, confidence=1.0, matchedTerms=`["kangaroo2", "tension", "catenary"]`. parti-ghcanvas preflights kangaroo and places Kangaroo2 components; if missing, pauses.

**Prompt:** "Do a daylight study on the south facade."
Router returns: one intent, plugin=`ladybug`, confidence=0.7, matchedTerms=`["daylight"]`. parti-ghcanvas preflights ladybug; if missing, either falls back to a geometric sun-angle approximation (native) and notes the limitation, or asks to install.

**Prompt:** "Optimize the facade for daylight and energy."
Router returns: two intents, plugin=`ladybug` (conf 0.7) and plugin=`honeybee` (conf 0.7). parti-ghcanvas picks ladybug for the daylight half and honeybee for the energy half, or pauses if only one is installed.

