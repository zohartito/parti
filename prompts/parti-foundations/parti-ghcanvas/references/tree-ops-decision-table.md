# Grasshopper Tree Operations - Decision Table

Grasshopper data trees are a hierarchy of branches, each branch holding a list of items. A single slider outputs one list at path `{0}`; a curve divided into points outputs a tree at `{0;0}`, `{0;1}`, and so on. Most "why is my component giving weird output" moments in Parti-generated canvases are not component-choice issues but tree-shape issues. This reference is the decision table Parti consults before wiring any chain that includes a tree operation. The source of truth in code is `src/Parti.Core/Design/TreeHygieneAnalyzer.cs`.

## The 9 operations

| Op | What it does | When to use | When NOT to use |
|----|--------------|-------------|-----------------|
| Graft | Wraps each item in its own branch. `{0}` holding `[a, b, c]` becomes `{0;0}`, `{0;1}`, `{0;2}`. | You need each item treated as its own list so the next component operates per-item rather than per-list. | As a "fix" for a red-wire error. If Graft silences a mismatch, verify the tree shape was the intent. Grafting to patch over a List Length difference usually bites later. |
| Entwine | Weaves multiple incoming streams into one output tree with explicit branch structure. | You have 2+ streams and want them combined into a single tree where each source becomes its own branch. | For simple list concatenation with no branch semantics - use Merge instead. Entwine's branch structure is load-bearing. |
| Flatten | Collapses all branches into a single flat list. | You genuinely need a flat list downstream (e.g. a single Bake target, a single Brep input to a boolean op). | As a default "to silence the error". Flatten is the most overused op in novice graphs. Destroyed branch structure cannot be reconstructed downstream. |
| PathMapper | Rewrites paths via text expressions. `{A;B}->{B;A}` flips, `{A;B;C}->{A;C}` drops a level, `{A;B}(i)->{A}(B*N+i)` reshapes. | Any non-trivial tree transform. A single Path Mapper replaces 5+ component chains. This is the default for multi-op tree work. | For a one-op transform that a dedicated component (Graft, Flatten, Shift) expresses more clearly. |
| Trim | Removes N levels from the outermost path. `{0;1;2}` with depth 1 becomes `{0;1}`. | You need to lift a tree one or two levels closer to the root without changing item order. | When Shift is the right answer. Trim removes levels; Shift rotates indices. |
| Explode | Splits a tree into N separate list outputs, one per top-level branch. | You need to route branch 0, branch 1, branch 2 to different downstream components. | When a single component can accept the whole tree. Explode forces you to re-Entwine later, which is usually a smell. |
| FlipMatrix | Swaps branches and items in a 2D tree. Rows become columns. | Surface-UV pivots: `{u;v}` to `{v;u}`. Also useful for transposing a Divide Domain2 output when the solver wants columns-first. | For higher-dimensional trees - FlipMatrix only handles the last two levels cleanly. Use Path Mapper for 3D+. |
| RelativeItem | Indexes items by offset relative to their branch neighbors. "The panel next door at (+1, 0)" without chained List Item calls. | Neighbor-relationship work: adjacency checks, running differences, edge tests on a quad grid. | For random access by absolute index - use List Item. RelativeItem shines when the "neighbor" concept is load-bearing. |
| Shift | Offsets path indices by a fixed amount. `{0}` becomes `{5}` with offset 5. | Aligning two trees that are path-shifted relative to each other. Common after Graft/Ungraft mismatches. | For removing levels - that is Trim's job. Shift preserves depth. |

## Hygiene rules

The TreeHygieneAnalyzer applies six rules after Parti drafts a tool plan and before the components are committed. Each rule carries a severity level (`Info`, `Warn`, or `Error`) and an optional suggested replacement chain.

### TH001 - collapse to Path Mapper

**Severity.** Warn.

**Detected when.** The drafted chain has 3+ consecutive tree operations, and the run is not already a single Path Mapper.

**Why it fires.** Any chain of 3+ tree ops (Graft -> FlipMatrix -> Shift, say) can be expressed as a single Path Mapper with an explicit mapping expression. Keeping them separate costs three components and three wires, and makes it harder to read the transform intent.

**Fix.** Replace the run with one Path Mapper whose expression captures the combined transform.

### TH002 - graft then flatten

**Severity.** Error.

**Detected when.** A Graft component is immediately followed by a Flatten component.

**Why it fires.** Graft wraps each item in its own branch; Flatten collapses all branches back to one list. The pair cancels out and leaves the original list untouched, while consuming two components and two wires.

**Fix.** Remove both components. Wire the upstream source directly to the downstream target.

### TH003 - flatten then graft

**Severity.** Warn.

**Detected when.** A Flatten component is immediately followed by a Graft component.

**Why it fires.** Flatten discards all branch structure; Graft then re-wraps each item in a fresh branch. The result is a tree of per-item branches, but the original branch structure is gone. If the goal was a per-item tree from the original branching, Graft alone on the upstream would have done it.

**Fix.** If the intent was per-item branches from the original source, use a single Graft on the upstream instead. If the intent truly was "flatten everything then re-branch", write a comment on the canvas explaining why.

### TH004 - double Flatten

**Severity.** Info.

**Detected when.** Two Flatten components appear in sequence.

**Why it fires.** A flat list flattened again is still the same flat list. Redundant.

**Fix.** Remove one of the Flatten components.

### TH005 - double Flip Matrix

**Severity.** Info.

**Detected when.** Two Flip Matrix components appear in sequence.

**Why it fires.** Flip Matrix swaps the last two levels of a tree. Applying it twice returns the original tree.

**Fix.** Remove both components.

### TH006 - double Shift

**Severity.** Info.

**Detected when.** Two Shift components appear in sequence.

**Why it fires.** Consecutive Shift components can be combined into a single Shift whose offset is the sum of the two original offsets. Fewer components, same behavior.

**Fix.** Replace the pair with one Shift whose offset equals the sum.

## Using this table

Parti consults the decision table at two points. First, when parti-ghcanvas is drafting the tool calls for a turn, any chain with 3+ tree ops triggers a rewrite to Path Mapper before the first `gh_add_component` call. Second, at commit time, the TreeHygieneAnalyzer re-runs the six rules on the final chain and surfaces findings in the session log. `Error`-severity findings halt the turn; `Warn` findings proceed with a surfaced note; `Info` findings are logged but not surfaced unless the user asks.

When in doubt about an existing chain on the canvas, call `gh_describe` on the relevant parameters and read the branch structure before adding any new tree op. Reading is always cheaper than guessing.

