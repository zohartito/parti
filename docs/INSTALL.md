# Installing Parti

Parti is a Grasshopper plugin for **Rhino 8** (Windows or Mac, 8.35 or later). It is a
.NET 7 AnyCPU build, so the same files work on both platforms. It is pre-release
software (v0.9.x): expect rough edges and save your definitions often.

## Option A: Yak package (`parti-<version>-rh8_35-any.yak`)

1. Drag the `.yak` file onto an open Rhino 8 window. Rhino installs it into its
   package folder.
2. Restart Rhino, then open Grasshopper.

The package is not on the public Yak server yet, so searching `_PackageManager`
for "parti" will not find it. To uninstall, use `_PackageManager`.

## Option B: Zip (`Parti-<version>.zip`)

1. **Windows only:** right-click the zip, choose Properties, tick **Unblock**, OK.
   Do this before extracting, or Grasshopper may refuse to load the DLLs.
2. In Grasshopper: File > Special Folders > Components Folder. This opens the
   `Libraries` folder.
3. Extract the zip there. You should end up with `Libraries/Parti/Parti.gha` next to
   the `Parti.*.dll` files. Keep the whole folder together: `Parti.gha` alone
   cannot load.
4. Restart Rhino.

Use A or B, not both: two copies of the same plugin conflict at load time.

## Windows: .NET runtime

Parti targets .NET 7+, not .NET Framework. Rhino 8 on Windows uses .NET Core by
default. If you switched it to .NET Framework, run `SetDotNetRuntime` in Rhino, pick
NETCore, and restart. On Mac, Rhino 8 always uses .NET Core.

## Check that it loaded

In Grasshopper, the **Parti** tab has one component, **Parti Chat** (group AI).
Grasshopper's list of loaded libraries should show Parti with version 0.9.1.

## Using the chat window

Drop **Parti Chat** on the canvas; the chat window opens. Double-click the component to bring it back if you close it.
Click **Settings** to choose a provider, model, and endpoint:

The window selects Claude subscription first when a `claude` executable is found,
without checking whether it is signed in. Otherwise it looks for a local Ollama
model. Choose a different provider in Settings if needed.

- **Ollama (local).** Install [Ollama](https://ollama.com) on this machine or another one you can reach,
  pull a tool-calling model (verified: `qwen3.6:27b`), and keep it running. Set **Endpoint** to the server
  (default `http://localhost:11434`; for example `http://ollama-host:11434` for a machine on your network) and click **List models**.
  Parti warms the model in the background; the first reply after a cold load can take about a minute.
- **Anthropic, OpenAI, OpenRouter, or OpenAI-compatible (custom URL).** Needs an API key: the
  `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `OPENROUTER_API_KEY` environment variable of the Rhino process (on Mac,
  Rhino started from the Dock does not see variables exported in your shell profile), or a key typed into Settings.
  A typed key is kept in memory only. Keys are never sent over plain `http` to a non-local host.
- **Subscription (Claude CLI) or Subscription (Codex CLI).** Uses your own signed-in `claude` (Claude Pro/Max) or
  `codex` (ChatGPT) command-line tool instead of an API key. Install the CLI and sign in once in a terminal
  (`claude`, then `/login`; `codex login`). Pick the model (`claude-opus-5-5`, `claude-fable-5-1`, `claude-sonnet-5`;
  `gpt-6-astra`, `gpt-6-sol`) and the effort (default `medium`). Parti finds the CLI on `PATH` or in the usual install
  folders (`/opt/homebrew/bin`, `/usr/local/bin`, `~/.local/bin`, `~/.npm-global/bin`, `~/.claude/local`,
  `%APPDATA%\npm`); set **CLI executable** if it lives elsewhere. On Windows Parti only runs a native `.exe`: an npm
  `.cmd` launcher is skipped, so point **CLI executable** at the real binary. **Subscription only:** Parti forces the
  CLI's subscription login (Claude `forceLoginMethod: claudeai`, Codex `forced_login_method = "chatgpt"`) and removes
  `ANTHROPIC_*`, `OPENAI_*`, `AZURE_OPENAI_*`, `CLAUDE_CODE_USE_*`, `CODEX_API_KEY` and `CLAUDE_CODE_EFFORT_LEVEL` from
  the CLI's environment, so a turn never bills an API key or a cloud account. Each model turn is one CLI call: Parti starts the real
  executable (never through your shell, so aliases that add flags don't apply) with all of the CLI's own tools, MCP
  servers, settings and plugins turned off, in an empty temp folder it deletes afterwards. The prompt and conversation
  go through a file and standard input; the reply is schema-checked JSON, and every tool call in it still goes through
  Parti's guards and the Approve plan dialog. A call is limited to 180 s. Parti itself makes no network calls here; the
  CLI talks to its vendor with your login, and no key or token passes through Parti. Replies are not streamed: the
  text appears when the CLI call finishes.

How a turn works:

1. Describe the design: what varies (inputs), what must hold (rules), and how you judge it (evaluations).
   Parti records this as a decomposition and shows it as a design-brief card. If something is missing, it asks.
2. Every canvas change opens **Approve plan**: the calls, the real Grasshopper component names, and the "Why" from the
   decomposition. Nothing is written until you approve.
3. One **Ctrl+Z** undoes a whole Parti turn. Locked components (ask Parti to lock them, unlock in the chat window) can't
   be changed, deleted, or rewired by Parti.

## Supported today

- Chat with a local Ollama model or a hosted provider from inside Grasshopper (verified live on Rhino 8.35, macOS).
- The model can find components by name, read the canvas (including runtime warnings), and add, connect, set inputs on,
  group, lay out, relayout, and delete components, always through the plan dialog.

## Not supported yet

- Solver runs (Galapagos, Octopus, Wallacei): their tools are not registered.
- Saving settings or API keys between sessions.
- Setting an Expression component's formula or a Panel's text through the chat.
- Other subscription CLIs (GitHub Copilot, Cursor, Grok, Kimi, Gemini): a spike on 2026-09-27 found no safe
  lockdown yet. Cursor's CLI tools cannot be removed, Grok's CLI imports Claude's MCP servers by default, and the
  Gemini CLI login available here is the free tier. Only `claude` and `codex` are supported.
- Rhino 7, Rhino 8 in .NET Framework mode, and Grasshopper 2. Windows Rhino has not been live-verified yet.
