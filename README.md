# Parti

A Grasshopper plugin for Rhino 8 that puts an AI model in a chat window next to your canvas. Before it changes
anything, the model writes down the design logic (inputs, rules, evaluations). It then proposes canvas edits in
batches, and you approve or reject each batch.

## Status: early alpha

Parti is unfinished, pre-release software (version 0.9.1, no public release yet). It has been run live in Rhino 8.35 on
macOS with a local model through Ollama. The Claude and Codex subscription CLIs have been run through the live eval
harness, which uses the real chat loop on a simulated canvas rather than Rhino (see
[docs/EVAL-LIVE.md](docs/EVAL-LIVE.md)). It has **not** been run live on Windows Rhino. Expect rough edges and save
your definitions often.

What works today:

- **Chat inside Grasshopper.** The Parti Chat component opens a chat window. Providers: Ollama (the default), Anthropic,
  OpenAI, OpenRouter, any OpenAI-compatible server, or your own Claude or ChatGPT subscription through the official
  `claude` / `codex` CLI.
- **Design logic first.** Before any canvas write, the model must record a decomposition: inputs, rules, and
  evaluations. Code requires at least one item labelled `user_stated` by the model; it does not verify
  that the item actually came from your message. You still approve each proposed write.
- **You approve every write.** Each batch of edits opens a plan dialog that lists every call, the real Grasshopper
  component names, and the reason taken from the decomposition. Nothing is written until you click Approve.
- **Canvas tools.** The model can find components by name, read the canvas (including runtime warnings), and add,
  connect, set inputs on, group, lay out, and delete components. You can lock components so Parti cannot change,
  delete, or rewire them.
- **One undo per turn.** A whole Parti turn reverts with a single Ctrl+Z.
- **Published prompts.** The system prompt ships in [`prompts/parti-foundations/`](prompts/parti-foundations/).

## Requirements

- **Rhino 8.35 or later** with **Grasshopper 1**, on macOS or Windows. On Windows, Rhino must run on .NET Core (the
  default; run `SetDotNetRuntime` if you switched it to .NET Framework). Rhino 7 and Grasshopper 2 are not supported.
- **A model**, one of:
  - **Ollama** (local, free, no key): install [Ollama](https://ollama.com), pull a tool-calling model (tested:
    `qwen3.6:27b`), and set the endpoint in Parti's Settings (default `http://localhost:11434`). The server can be on
    another machine you can reach.
  - **Hosted API** (Anthropic, OpenAI, OpenRouter, or a custom OpenAI-compatible URL): needs your own API key.
  - **Subscription CLI**: the `claude` (Claude Pro/Max) or `codex` (ChatGPT) CLI, installed and signed in once in a
    terminal. Parti forces the subscription login and strips API-key variables from the CLI's environment, so these
    turns never bill an API key.
- **To build from source:** a .NET SDK. The projects target `net7.0`; a newer SDK works if you set
  `DOTNET_ROLL_FORWARD=Major` (checked with SDK 10.0.400).

### API keys

Parti reads keys from environment variables of the **Rhino process**:

| Provider | Variable |
|---|---|
| Anthropic | `ANTHROPIC_API_KEY` |
| OpenAI | `OPENAI_API_KEY` |
| OpenRouter | `OPENROUTER_API_KEY` |

You can also type a key into Settings; a typed key is kept in memory only and is not saved. Parti never sends a key
over plain `http` to a non-local host. Never commit keys to this repository (`.env` and `*.key` are gitignored).

On macOS, Rhino started from the Dock does not see variables exported in your shell profile. Start Rhino from a
terminal that has the variable set, or type the key into Settings.

## Build

```bash
dotnet build Parti.sln -c Release
dotnet test Parti.sln -c Release
```

With only a newer .NET installed, prefix both commands with `DOTNET_ROLL_FORWARD=Major`. The plugin is built to
`src/Parti.Components/bin/Release/net7.0/Parti.gha`.

To make an installable package:

```bash
bash packaging/build_package.sh
```

This writes a `.zip` and, if Rhino's `yak` tool is found, a `.yak` package to `packaging/out/`. On macOS the script
looks for `yak` inside `/Applications/Rhino 8.app`; set `YAK=/path/to/yak` to override it. Without `yak` you get the zip
only.

## Install

Full steps are in [docs/INSTALL.md](docs/INSTALL.md). In short:

- **`.yak` package:** drag it onto an open Rhino 8 window, then restart Rhino.
- **Zip:** in Grasshopper choose File > Special Folders > Components Folder and extract the zip there, so that you get
  `Libraries/Parti/Parti.gha` next to the `Parti.*.dll` files. On Windows, unblock the zip (Properties > Unblock)
  before extracting. Restart Rhino.

Use one method, not both. In Grasshopper, the **Parti** tab then shows one component, **Parti Chat**. Drop it on the
canvas to open the chat window, and click **Settings** to choose the provider, model, and endpoint.

## Limits

- No solver runs yet: Galapagos, Octopus, and Wallacei tools exist in the code but are not registered.
- Settings and API keys are not saved between sessions.
- The chat cannot set an Expression component's formula or a Panel's text.
- Only the `claude` and `codex` subscription CLIs are supported.
- Subscription CLI replies arrive whole, not streamed, and each call is limited to 180 seconds.
- The MCP companion (`src/Parti.Mcp`, `mcp/parti_mcp`) is a stub.
- Not live-tested on Windows Rhino.
- Live-model evals and live-provider tests are opt-in and need a model. The default `dotnet test` run uses fakes and
  does not start Rhino. See [docs/EVAL-LIVE.md](docs/EVAL-LIVE.md).

## Repository layout

```
src/Parti.Core               session loop, tools, policies, prompts, decomposition
src/Parti.Core.Grasshopper   Grasshopper tools, catalog, undo, chat controller
src/Parti.Components         the .gha: Parti Chat component, chat window, dialogs
src/Parti.Providers.*        Anthropic, Ollama, OpenAI-compatible, subscription CLI
src/Parti.Mcp                MCP companion (stub)
prompts/parti-foundations    the published skill prompt
tests/                       Core, Integration, Mcp, and Eval suites
docs/                        architecture, install, eval, prompt and security notes
packaging/                   Yak and zip packaging
```

## License

LGPL-3.0-or-later. See [LICENSE](LICENSE) and [COPYING.LESSER](COPYING.LESSER). Code adapted from
[SmartHopper](https://github.com/architects-toolkit/SmartHopper) (also LGPL-3.0-or-later) is listed in
[ATTRIBUTION.md](ATTRIBUTION.md) and [docs/NOTICES.md](docs/NOTICES.md).

## Author

Zohar Tito — USC School of Architecture.
