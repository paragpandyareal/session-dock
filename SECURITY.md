# Security

Session Dock is a local-only desktop app. This page explains exactly what it does on your machine, so you can decide whether to trust it before installing.

## What it does

- **Reads hook events from Claude Code.** You opt in, either by saying yes when the app asks to connect, by running the installer, or with `SessionDock.exe --install-hooks`. That adds one hook entry per event to `~/.claude/settings.json`, after saving a timestamped backup next to it. No other setting is changed, though the file is rewritten as plain formatted JSON, so any comments in it are dropped (the backup keeps them). If the hooks are already in place, the file isn't touched at all. `--uninstall-hooks` (or `--uninstall`, which also removes the app) removes only those entries.
- **Installs per user, without admin.** `--install` copies the exe to `%LOCALAPPDATA%\Programs\SessionDock` and adds Start menu and Startup folder shortcuts. Turn off "Start with Windows" in the tray menu to remove the startup shortcut.
- **Runs a small hook script** (`hook/agent-dock-hook.js`, about 80 lines, no dependencies). The exe carries a copy and writes it to `%LOCALAPPDATA%\AgentDock`, where you can read it. Claude Code runs it with Node.js for each event. It forwards the event to the app on `127.0.0.1` and always exits within 1 second. It never writes to stdout, so it cannot inject anything into Claude's context. Large fields (tool output, file contents) are dropped before sending.
- **Listens on `127.0.0.1` only** on a random port, saved in `%LOCALAPPDATA%\AgentDock\config.json`. It is never reachable from your network.
- **Reads the first lines of session transcripts** to tell CLI sessions from desktop ones. It also reads `.git/HEAD` to show the branch. Both are read-only.
- **Opens sessions when you click a card.** Desktop sessions open with a `claude://` link built from a strictly validated session id. CLI sessions bring their existing terminal window to the front.

## What it does not do

- No network access beyond loopback. No telemetry, analytics, auto-update or cloud calls.
- No admin rights, services, drivers or registry changes. The one-line installer can install Node.js through winget if it's missing, and Windows may ask for admin for that.
- It never runs commands from event data, never writes to your projects, and never changes Claude Code settings unless you agree to connect it.
- Prompts and activity are kept in memory only, never written to disk. The log at `%LOCALAPPDATA%\AgentDock\agentdock.log` records errors only.
- So cards survive an app restart, `%LOCALAPPDATA%\AgentDock\sessions.json` keeps each open session's id, folder, process id, status and timestamps. It holds no prompts, replies or tool details, and sessions whose Claude Code process has exited are dropped.

## How the local listener is protected

- **Secret token.** A per-install random 256-bit token is required on every data request (`X-SessionDock-Token`). Other apps and web pages don't know it.
- **No browser requests.** Browsers can't add that custom header to cross-site requests without a CORS preflight, which the server never approves. Requests carrying an `Origin` header are rejected outright.
- **DNS rebinding blocked.** The `Host` header must be `127.0.0.1:<port>` or `localhost:<port>`.
- **Strict input handling.** Only JSON bodies are accepted. Bodies are capped at 256 KB, with at most 16 connections and 64 tracked sessions, and a 3-second timeout per request.
- **Validated before use.** Every field from an event is length-limited before display. The desktop session id must match `local_<uuid>` before it is used in a link. A process id is only focused if it is still a Claude Code process.

## Unsigned builds

Release builds are not code-signed, so Windows SmartScreen may warn the first time you run one. If you prefer, build from source with the .NET SDK (see the README). The release notes list SHA-256 checksums, and `install.ps1` refuses to install a download that doesn't match. The checksums are published in the same release as the exe, so they catch a corrupted or tampered download in transit, but not a compromised release. If that matters to you, build from source.

## Reporting a vulnerability

Please open a private security advisory on this repository (Security → Advisories → Report a vulnerability) rather than a public issue.
