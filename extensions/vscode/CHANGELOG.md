# Changelog

## 1.2.0

- **Overview: apps, not ports.** The Ports view now shows apps by default: a dev server with its workers is one line, named the way you'd say it (*Vite dev server*, *Django dev server*, *npm run dev*, *PostgreSQL*), with memory and CPU for the whole process family, uptime, and the agent that started it. Each project shows its totals, and **Needs attention** says what's wrong in plain words. Switch to *All ports* from the view's title bar (`portpilot.view`).
- **Stop App** stops an app's whole process family: workers first, then the process that started them.
- **Lighter.** One long-lived engine process streams scans while the window is focused, instead of a new process every few seconds, and it exits when the window loses focus.

## 1.1.0

- **Start Again.** When a dev server in this workspace stops, a toast offers to start it again: same command, same folder, in a terminal. Stopped servers are listed under *Recently Stopped*.
- **Runtime at a glance.** Each port is coloured and labelled by runtime (Node.js, Python, Go, Java, Ruby, PHP, .NET, database, Docker) and shows how long it has been up.
- **Less noise.** Editors, language servers and build daemons are hidden by default (`portpilot.showTooling`), as are system ports in the *all ports* view (`portpilot.hideSystemPorts`). `portpilot.ignorePorts` hides any port you like. The protocol is shown only for UDP.
- **Claude Code guard and status line.** *Set Up Claude Code Integration* now also stops Claude from starting a second copy of a dev server that is already running for the project, and adds a status line with the project's servers.
- Optional toast when a server starts (`portpilot.notifyOnStart`).

## 1.0.0

- Ports view: this workspace's ports (or every port), grouped by git project and branch, tagged with the coding agent that started them and anything an ended session left behind.
- Status bar item with the workspace's ports and a leftover count.
- Open in browser, show output, check health, copy URL or PID, stop a process, clean up leftovers.
- "address already in use" in the terminal links to *Who is on port…*.
- *Find a Free Port*, verified by binding.
- PortPilot's MCP server is offered to agents in VS Code.
- Native `portpilot` engine bundled for Linux (x64, arm64), macOS (Apple silicon, Intel) and Windows (x64).
