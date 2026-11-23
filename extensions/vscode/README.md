# PortPilot for VS Code

**Something is already running on port 3000.** This tells you what, which of your projects it belongs to, which coding agent started it, whether it is healthy, and lets you stop it, all from the editor.

Works in VS Code, Cursor, Windsurf and VSCodium, on Linux, macOS and Windows. Each platform build ships the native `portpilot` engine (about 3 MB), so there is nothing else to install.

## Features

- **Ports view.** The ports of the projects open in this window, grouped by git project and branch, with the agent that started each one (Claude Code, Codex, Gemini CLI, Aider, …) and anything an ended session *left behind*. Use the filter button to see every port on the machine.
- **Status bar.** This workspace's ports at a glance, plus a warning count when an agent session has left something running.
- **`EADDRINUSE`, solved where you see it.** "address already in use :3000" in the terminal becomes a link. Click it to see who holds the port and stop it, open it, read its output, or pick a free port instead.
- **Per port:** open in browser, show output (when the process writes to a file or the systemd journal), check health (HTTP status and latency), copy the URL or PID, and stop it (it asks first).
- **Clean up** everything ended agent sessions left running, for one project or all of them.
- **MCP server for agents in VS Code.** Copilot agent mode and other MCP clients get PortPilot's tools (`project_status`, `find_free_port`, `check_health`, `read_logs`, …), so they reuse a running dev server instead of starting a second one.
- **Commands:** *PortPilot: Who Is On Port…*, *Find a Free Port*, *Clean Up What Agents Left Behind*, *Set Up Claude Code Integration*.

## Settings

| Setting | Default | |
| --- | --- | --- |
| `portpilot.scope` | `workspace` | `all` shows every listening port. |
| `portpilot.refreshSeconds` | `3` | Rescans only while the window is focused. |
| `portpilot.statusBar` | `true` | |
| `portpilot.registerMcpServer` | `true` | |
| `portpilot.cliPath` | | Use a specific `portpilot` binary. |

More at [github.com/Atrix21/port-pilot](https://github.com/Atrix21/port-pilot).
