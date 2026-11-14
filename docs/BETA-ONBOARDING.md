# Parti private beta — first hour

Parti is pre-release (v0.9.1). This page gets you from zero to a first useful turn, tells you what is rough, and how to
report problems. Install steps are in [INSTALL.md](INSTALL.md).

## Before you start (5 minutes)

- Rhino 8.35 or later with Grasshopper 1. Verified live on macOS; Windows is expected to work but has not been
  live-verified yet — tell us if it doesn't.
- A model. **Recommended: your own subscription.** Pick **Subscription (Claude CLI)** with `claude-opus-5-5` in
  Settings: it passed 30 of 30 live briefs in our eval, including every large build, in about 23 s per brief
  (`claude-fable-5-1` also 30/30; `gpt-6-astra` via **Subscription (Codex CLI)** 21/30, it stops to ask more often).
  Sign in to the `claude` or `codex` CLI once in a terminal first. Parti runs the CLI with its own tools off, in an
  empty temp folder, on your subscription login only; each model turn is one CLI call, so replies arrive whole
  rather than streamed.
- Or local and free: [Ollama](https://ollama.com) with a tool-calling model. `qwen3.6:27b` passed 27 of 30 (measured
  with an older, shorter prompt); `gpt-oss:20b` is not recommended. The Ollama server can run on another machine you
  can reach (set **Endpoint** in Settings). Hosted API providers need your own API key.

## Your first turn (10 minutes)

1. Drop **Parti Chat** (Parti tab) on an empty canvas. Open **Settings**, pick the provider, endpoint, and model, Save.
   Wait for "Ready" in the status line (a cold local model can take about a minute).
2. Give it a small brief with all three parts, for example:
   > Brief: a circular plaza. Input: the plaza radius, varying from 1 to 5 m. Rule: the plaza is a circle on the world
   > XY plane at the origin. Evaluation: keep the circumference at most 30 m. Build it: a Number Slider for the radius
   > (range 1 to 5) wired into a Circle component's Radius input.
3. Parti records the design brief (the card at the top), looks up components, and asks you to approve each change in
   **Approve plan**. Read the "Why" block; approve or cancel.
4. When it finishes, check its last message: it should tell you something about your evaluation (in our run it noted
   that radii above about 4.77 m break the 30 m bound).
5. Press **Ctrl+Z** once: the whole Parti turn disappears. **Ctrl+Shift+Z** brings it back.

## Rough edges you will hit

- **Slow, chatty turns on local models.** A small build can take several minutes and ask for approval once per step
  (7 approvals in our R9 run). Tool activity lines appear in the transcript while it works.
- **It asks before drawing.** "Add a slider" with no design goal gets a question back, by design: Parti won't write
  until at least one input, rule, or evaluation comes from you. Say what the slider controls and how you'll judge the
  result.
- **Large builds** (grids, many components) go in stages, one approval per stage: the components, then the wires,
  then the values. Parti makes at most 16 changes per approval and 64 per message; past that it builds a working
  first part, tells you what is left, and continues when you reply.
- **Not yet:** running Galapagos/Octopus/Wallacei, setting an Expression formula or Panel text through chat,
  remembering settings or keys between sessions.

## Reporting a problem (2 minutes)

Open an issue with the **Bug report** template: Rhino and Parti versions, provider and model, what you asked, the
relevant part of the transcript, and whether one Ctrl+Z restored the canvas. Never paste API keys.
