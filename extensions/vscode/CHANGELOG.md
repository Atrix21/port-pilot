# Changelog

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
