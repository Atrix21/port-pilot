import * as vscode from "vscode";
import { execFile } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";
import { ago, RecentServer, runAgain, ServerWatch } from "./history";
import { runtimeIcon, runtimeOf, uptime } from "./runtime";

/** One endpoint, as printed by `portpilot list --json`. */
interface PortEntry {
  port: number;
  protocol: string;
  address: string;
  state: string;
  pid: number;
  process: string;
  executable?: string | null;
  commandLine?: string | null;
  started?: string;
  access: string;
  service?: string | null;
  workingDirectory?: string | null;
  project?: { name: string; branch?: string | null; root: string; worktree: boolean };
  agent?: { name: string; pid: number; running: boolean };
  orphaned: boolean;
  /** An editor, language server or build daemon rather than something started on purpose. */
  tooling?: boolean;
}

const NO_PROJECT = "\u0000other";

let cli: string | undefined;
let entries: PortEntry[] = [];
let output: vscode.OutputChannel;
let watch: ServerWatch;

// ═════════════ The terminal app ═════════════

/** The bundled binary (platform-specific VSIX), then the setting, then PATH. */
function resolveCli(context: vscode.ExtensionContext): string | undefined {
  const configured = vscode.workspace.getConfiguration("portpilot").get<string>("cliPath");
  if (configured) {
    return configured;
  }

  const exe = process.platform === "win32" ? "portpilot.exe" : "portpilot";
  const bundled = path.join(context.extensionPath, "bin", exe);
  if (fs.existsSync(bundled)) {
    return bundled;
  }

  const dirs = (process.env.PATH ?? "").split(path.delimiter);
  if (process.platform === "win32" && process.env.LOCALAPPDATA) {
    dirs.push(path.join(process.env.LOCALAPPDATA, "Programs", "PortPilot", "cli"));
  }
  if (process.platform !== "win32") {
    dirs.push("/opt/homebrew/bin", "/usr/local/bin", path.join(process.env.HOME ?? "", ".local", "bin"));
  }

  return dirs.map((d) => path.join(d, exe)).find((p) => fs.existsSync(p));
}

function run(args: string[], timeoutMs = 15000): Promise<string> {
  return new Promise((resolve, reject) => {
    if (!cli) {
      reject(new Error("The PortPilot terminal app wasn't found. Install PortPilot or set portpilot.cliPath."));
      return;
    }

    execFile(cli, args, { timeout: timeoutMs, maxBuffer: 16 * 1024 * 1024, windowsHide: true }, (error, stdout, stderr) => {
      // `who`, `logs` and `kill` exit 1 for "nothing there"; their output is still the answer.
      if (error && !stdout) {
        reject(new Error(stderr.trim() || error.message));
      } else {
        resolve(stdout);
      }
    });
  });
}

async function scan(): Promise<void> {
  if (!cli) {
    return;
  }

  try {
    entries = JSON.parse(await run(["list", "--json"])) as PortEntry[];
  } catch (error) {
    output.appendLine(`Scan failed: ${(error as Error).message}`);
  }
}

// ═════════════ Projects in this window ═════════════

/** The projects open in this window, matched to entries by repository root or working directory. */
function belongsToWorkspace(entry: PortEntry): boolean {
  const folders = vscode.workspace.workspaceFolders ?? [];
  const candidates = [entry.project?.root, entry.workingDirectory].filter((p): p is string => !!p);
  return folders.some((folder) => {
    const root = normalise(folder.uri.fsPath);
    return candidates.some((c) => {
      const p = normalise(c);
      return p === root || p.startsWith(root + "/") || root.startsWith(p + "/");
    });
  });
}

function normalise(p: string): string {
  const n = p.replace(/\\/g, "/").replace(/\/+$/, "");
  return process.platform === "win32" || process.platform === "darwin" ? n.toLowerCase() : n;
}

/** The user's port filters: ignored ports always, system ports (< 1024) unless turned off. */
function visibleByFilters(entry: PortEntry, scope: string | undefined): boolean {
  const config = vscode.workspace.getConfiguration("portpilot");
  if ((config.get<number[]>("ignorePorts") ?? []).includes(entry.port)) {
    return false;
  }
  if (entry.tooling && !config.get<boolean>("showTooling")) {
    return false;
  }
  return !(scope === "all" && config.get<boolean>("hideSystemPorts") && entry.port < 1024);
}

function isHttp(entry: PortEntry): boolean {
  return entry.protocol.startsWith("tcp") && entry.state === "Listening" && !/PostgreSQL|MySQL|Redis|MongoDB|RabbitMQ|Kafka|SQL Server|Memcached|Elasticsearch/i.test(entry.service ?? "");
}

function url(entry: PortEntry): string {
  return `${entry.port === 443 || /HTTPS/i.test(entry.service ?? "") ? "https" : "http"}://localhost:${entry.port}`;
}

function agentShort(name: string): string {
  return name === "Claude Code" ? "Claude" : name === "Gemini CLI" ? "Gemini" : name === "Cursor Agent" ? "Cursor" : name;
}

// ═════════════ Tree ═════════════

type Node = { kind: "project"; key: string; label: string; items: PortEntry[] } | { kind: "port"; entry: PortEntry };

class PortsProvider implements vscode.TreeDataProvider<Node> {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;

  refresh(): void {
    this.changed.fire();
  }

  getChildren(node?: Node): Node[] {
    if (node?.kind === "project") {
      return node.items.map((entry) => ({ kind: "port", entry }));
    }

    if (node) {
      return [];
    }

    const scope = vscode.workspace.getConfiguration("portpilot").get<string>("scope");
    const visible = (scope === "all" ? entries : entries.filter(belongsToWorkspace)).filter((e) => visibleByFilters(e, scope));
    const groups = new Map<string, PortEntry[]>();
    for (const entry of visible) {
      const key = entry.project?.name ?? NO_PROJECT;
      groups.set(key, [...(groups.get(key) ?? []), entry]);
    }

    // One project: no need for a group level.
    if (groups.size === 1 && !groups.has(NO_PROJECT)) {
      return [...groups.values()][0].map((entry) => ({ kind: "port", entry }));
    }

    return [...groups.entries()]
      .sort(([a, x], [b, y]) => Number(y.some((e) => e.orphaned)) - Number(x.some((e) => e.orphaned)) || (a === NO_PROJECT ? 1 : b === NO_PROJECT ? -1 : a.localeCompare(b)))
      .map(([key, items]) => ({ kind: "project", key, label: key === NO_PROJECT ? "Other" : key, items }));
  }

  getTreeItem(node: Node): vscode.TreeItem {
    if (node.kind === "project") {
      const branches = [...new Set(node.items.map((e) => e.project?.branch).filter(Boolean))].join(", ");
      const leftovers = node.items.filter((e) => e.orphaned).length;
      const item = new vscode.TreeItem(node.label, vscode.TreeItemCollapsibleState.Expanded);
      item.description = [branches, `${node.items.length} ${node.items.length === 1 ? "port" : "ports"}`, leftovers ? `${leftovers} left behind` : ""].filter(Boolean).join(" · ");
      item.iconPath = new vscode.ThemeIcon(node.key === NO_PROJECT ? "server" : "repo");
      item.contextValue = leftovers ? "project leftovers" : "project";
      return item;
    }

    const e = node.entry;
    const item = new vscode.TreeItem(`:${e.port}  ${e.process}`, vscode.TreeItemCollapsibleState.None);
    const runtime = runtimeOf(e.process, e.service, e.commandLine);
    const tags = [
      e.service ?? runtime?.label,
      e.protocol.startsWith("udp") ? "UDP" : undefined,
      e.agent ? `${agentShort(e.agent.name)}${e.agent.running ? "" : " · ended"}` : undefined,
      e.orphaned ? "left behind" : undefined,
      uptime(e.started),
    ].filter(Boolean);
    item.description = tags.join(" · ");
    item.iconPath = runtimeIcon(runtime, e.orphaned, e.state === "Listening" || e.state === "Bound");
    item.contextValue = `port${isHttp(e) ? " http" : ""}`;
    item.tooltip = tooltip(e);
    if (isHttp(e)) {
      item.command = { command: "portpilot.open", title: "Open", arguments: [node] };
    }
    return item;
  }
}

/** Dev servers that ran in this workspace and stopped, newest first, with Start again. */
class RecentProvider implements vscode.TreeDataProvider<RecentServer> {
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChangeTreeData = this.changed.event;

  refresh(): void {
    this.changed.fire();
  }

  getChildren(node?: RecentServer): RecentServer[] {
    return node ? [] : watch.recent;
  }

  getTreeItem(r: RecentServer): vscode.TreeItem {
    const item = new vscode.TreeItem(`:${r.port}  ${r.process}`, vscode.TreeItemCollapsibleState.None);
    item.description = [r.project, r.branch, ago(r.stopped)].filter(Boolean).join(" · ");
    item.iconPath = new vscode.ThemeIcon("history");
    item.contextValue = "recent";
    const md = new vscode.MarkdownString(undefined, true);
    md.appendMarkdown(`**:${r.port}** ${r.process}${r.service ? ` · ${r.service}` : ""} · stopped ${ago(r.stopped)}\n\n`);
    md.appendCodeblock(r.commandLine.length > 300 ? r.commandLine.slice(0, 300) + "…" : r.commandLine, "sh");
    md.appendMarkdown(`in \`${r.workingDirectory}\`${r.agent ? `\n\nStarted by ${r.agent}` : ""}\n\nClick to start it again in a terminal.`);
    item.tooltip = md;
    item.command = { command: "portpilot.runAgain", title: "Start Again", arguments: [r] };
    return item;
  }
}

function tooltip(e: PortEntry): vscode.MarkdownString {
  const md = new vscode.MarkdownString(undefined, true);
  md.appendMarkdown(`**:${e.port}** ${e.protocol} on \`${e.address}\`${e.service ? ` · ${e.service}` : ""}\n\n`);
  const runtime = runtimeOf(e.process, e.service, e.commandLine);
  md.appendMarkdown(`**${e.process}**${runtime ? ` (${runtime.label})` : ""} · PID ${e.pid}${e.started ? ` · up ${uptime(e.started)}, since ${new Date(e.started).toLocaleString()}` : ""}\n\n`);
  if (e.commandLine) {
    md.appendCodeblock(e.commandLine.length > 300 ? e.commandLine.slice(0, 300) + "…" : e.commandLine, "sh");
  }
  if (e.project) {
    md.appendMarkdown(`$(repo) ${e.project.name}${e.project.branch ? ` · $(git-branch) ${e.project.branch}` : ""}${e.project.worktree ? " (worktree)" : ""}\n\n`);
  }
  if (e.agent) {
    md.appendMarkdown(e.agent.running ? `$(hubot) Started by ${e.agent.name} (PID ${e.agent.pid})` : `$(warning) Left behind by a ${e.agent.name} session that has ended`);
  }
  return md;
}

// ═════════════ Actions ═════════════

function entryOf(arg: unknown): PortEntry | undefined {
  if (arg && typeof arg === "object" && "entry" in arg) {
    return (arg as { entry: PortEntry }).entry;
  }
  return arg as PortEntry | undefined;
}

async function stop(entry: PortEntry, refresh: () => Promise<void>): Promise<void> {
  const answer = await vscode.window.showWarningMessage(
    `Stop ${entry.process} (PID ${entry.pid})?`,
    { modal: true, detail: `It is listening on :${entry.port}.${entry.commandLine ? `\n\n${entry.commandLine}` : ""}` },
    "Stop",
  );
  if (answer !== "Stop") {
    return;
  }

  watch.expectStop(entry.pid);
  const result = await run(["kill", `pid:${entry.pid}`, "-y"]);
  vscode.window.showInformationMessage(result.trim() || `Stopped ${entry.process}.`);
  await refresh();
}

async function showLogs(entry: PortEntry): Promise<void> {
  const text = await run(["logs", `pid:${entry.pid}`, "200"]);
  const channel = vscode.window.createOutputChannel(`PortPilot :${entry.port} ${entry.process}`, "log");
  channel.replace(text);
  channel.show(true);
}

async function health(entry: PortEntry): Promise<void> {
  const text = (await run(["health", String(entry.port)])).trim();
  vscode.window.showInformationMessage(text.replace(/^\S+\s+/, `:${entry.port} `));
}

/** `portpilot who`, as a quick pick of things to do about the port. */
async function whoIsOn(port: number | undefined, refresh: () => Promise<void>): Promise<void> {
  if (port === undefined) {
    const value = await vscode.window.showInputBox({ prompt: "Port", placeHolder: "3000", validateInput: (v) => (/^\d{1,5}$/.test(v.trim()) ? undefined : "A port number") });
    if (!value) {
      return;
    }
    port = Number(value.trim());
  }

  await scan();
  const holders = entries.filter((e) => e.port === port);
  if (holders.length === 0) {
    vscode.window.showInformationMessage(`Nothing is listening on port ${port}.`);
    return;
  }

  const e = holders[0];
  type Pick = vscode.QuickPickItem & { action: () => Promise<void> };
  const picks: Pick[] = [
    { label: `$(debug-stop) Stop ${e.process} (PID ${e.pid})`, detail: e.commandLine ?? undefined, action: () => stop(e, refresh) },
    ...(isHttp(e) ? [{ label: `$(link-external) Open ${url(e)}`, action: async () => void vscode.env.openExternal(vscode.Uri.parse(url(e))) }] : []),
    { label: "$(output) Show its output", action: () => showLogs(e) },
    { label: "$(pulse) Check health", action: () => health(e) },
    { label: "$(search) Find a free port instead", action: () => freePort(port) },
  ];
  const owner = [e.project?.name, e.agent ? `${e.agent.running ? "started by" : "left behind by"} ${e.agent.name}` : undefined].filter(Boolean).join(" · ");
  const heldBy = `:${port} is held by ${e.process}${owner ? ` (${owner})` : ""}`;
  const choice = await vscode.window.showQuickPick(picks, { title: heldBy, placeHolder: `${heldBy}. What should happen?` });
  await choice?.action();
}

async function freePort(near?: number): Promise<void> {
  // Ask the MCP-backed CLI logic via a one-shot JSON-RPC exchange, so the answer is verified by binding.
  const request = [
    JSON.stringify({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "vscode", version: "1" } } }),
    JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "find_free_port", arguments: { preferred: near ?? 3000, count: 5 } } }),
  ].join("\n");
  const free = await new Promise<number[]>((resolve, reject) => {
    const child = execFile(cli!, ["mcp"], { timeout: 15000 }, (error, stdout) => {
      if (error && !stdout) {
        reject(error);
        return;
      }
      const reply = stdout.split("\n").filter(Boolean).map((l) => JSON.parse(l)).find((m) => m.id === 2);
      resolve(JSON.parse(reply.result.content[0].text).free as number[]);
    });
    child.stdin?.end(request + "\n");
  });

  const choice = await vscode.window.showQuickPick(free.map(String), { title: "Free ports (verified by binding them)", placeHolder: "Pick one to copy it" });
  if (choice) {
    await vscode.env.clipboard.writeText(choice);
    vscode.window.showInformationMessage(`Copied ${choice}.`);
  }
}

async function cleanup(refresh: () => Promise<void>, project?: string): Promise<void> {
  await scan();
  const leftovers = entries.filter((e) => e.orphaned && (!project || e.project?.name === project));
  const pids = [...new Set(leftovers.map((e) => e.pid))];
  if (pids.length === 0) {
    vscode.window.showInformationMessage("Nothing was left behind by an ended agent session.");
    return;
  }

  const list = leftovers.map((e) => `:${e.port} ${e.process} (${e.pid})${e.project ? ` · ${e.project.name}` : ""}`).join("\n");
  const answer = await vscode.window.showWarningMessage(`Stop ${pids.length} leftover ${pids.length === 1 ? "process" : "processes"}?`, { modal: true, detail: list }, "Stop");
  if (answer !== "Stop") {
    return;
  }

  for (const pid of pids) {
    watch.expectStop(pid);
    await run(["kill", `pid:${pid}`, "-y"]).catch(() => undefined);
  }
  vscode.window.showInformationMessage(`Stopped ${pids.length} leftover ${pids.length === 1 ? "process" : "processes"}.`);
  await refresh();
}

// ═════════════ "address already in use" in the terminal ═════════════

const IN_USE = /(?:EADDRINUSE|address already in use|port\s+(\d{2,5})\s+is\s+(?:already\s+)?in\s+use|Address already in use)[^\n]*?(?::(\d{2,5})\b|port\s+(\d{2,5}))?/i;

class PortInUseLinks implements vscode.TerminalLinkProvider<vscode.TerminalLink & { port: number }> {
  provideTerminalLinks(context: vscode.TerminalLinkContext): (vscode.TerminalLink & { port: number })[] {
    const match = IN_USE.exec(context.line);
    const port = Number(match?.[1] ?? match?.[2] ?? match?.[3]);
    if (!match || !port) {
      return [];
    }
    return [{ startIndex: match.index, length: match[0].length, tooltip: `PortPilot: who is on :${port}?`, port }];
  }

  handleTerminalLink(link: vscode.TerminalLink & { port: number }): void {
    void vscode.commands.executeCommand("portpilot.whoIsOn", link.port);
  }
}

// ═════════════ Activation ═════════════

/** What tests (and other extensions) can read. */
export interface PortPilotApi {
  cliPath(): string | undefined;
  entries(): readonly PortEntry[];
  workspaceEntries(): readonly PortEntry[];
  recent(): readonly RecentServer[];
  refresh(): Promise<void>;
}

export function activate(context: vscode.ExtensionContext): PortPilotApi {
  output = vscode.window.createOutputChannel("PortPilot");
  cli = resolveCli(context);
  void vscode.commands.executeCommand("setContext", "portpilot.cliMissing", !cli);

  watch = new ServerWatch(context.workspaceState);
  const provider = new PortsProvider();
  const recent = new RecentProvider();
  const recentView = vscode.window.createTreeView("portpilot.recent", { treeDataProvider: recent });
  watch.onDidChange(() => recent.refresh());
  const view = vscode.window.createTreeView("portpilot.ports", { treeDataProvider: provider, showCollapseAll: true });
  const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.command = "portpilot.ports.focus";

  const refresh = async () => {
    await scan();
    provider.refresh();
    const mine = entries.filter(belongsToWorkspace);
    const leftovers = entries.filter((e) => e.orphaned);
    void announce(await watch.update(mine.filter((e) => visibleByFilters(e, "workspace"))));
    void vscode.commands.executeCommand("setContext", "portpilot.hasLeftovers", leftovers.length > 0);
    view.badge = leftovers.length ? { value: leftovers.length, tooltip: `${leftovers.length} left behind by ended agent sessions` } : undefined;

    if (!vscode.workspace.getConfiguration("portpilot").get<boolean>("statusBar") || !cli) {
      status.hide();
      return;
    }
    const ports = [...new Set(mine.map((e) => e.port))].sort((a, b) => a - b);
    status.text = `$(radio-tower) ${ports.length ? ports.slice(0, 3).map((p) => ":" + p).join(" ") + (ports.length > 3 ? " …" : "") : "no ports"}${leftovers.length ? `  $(warning) ${leftovers.length}` : ""}`;
    status.tooltip = `PortPilot: ${mine.length} ${mine.length === 1 ? "endpoint" : "endpoints"} in this workspace${leftovers.length ? `, ${leftovers.length} left behind by ended agent sessions` : ""}`;
    status.show();
  };

  // Scan only while someone can see the result.
  let timer: NodeJS.Timeout | undefined;
  const schedule = () => {
    clearInterval(timer);
    const seconds = Math.max(1, vscode.workspace.getConfiguration("portpilot").get<number>("refreshSeconds") ?? 3);
    timer = setInterval(() => {
      if (vscode.window.state.focused && (view.visible || status.text)) {
        void refresh();
      }
    }, seconds * 1000);
  };
  schedule();
  void refresh();

  context.subscriptions.push(
    output,
    view,
    recentView,
    vscode.commands.registerCommand("portpilot.runAgain", (r: RecentServer) => r && runAgain(r)),
    vscode.commands.registerCommand("portpilot.forgetRecent", (r: RecentServer) => r && watch.forget(r.key)),
    vscode.commands.registerCommand("portpilot.clearRecent", () => watch.clear()),
    status,
    { dispose: () => clearInterval(timer) },
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("portpilot")) {
        cli = resolveCli(context);
        void vscode.commands.executeCommand("setContext", "portpilot.cliMissing", !cli);
        schedule();
        void refresh();
      }
    }),
    vscode.window.onDidChangeWindowState((s) => s.focused && void refresh()),
    vscode.window.registerTerminalLinkProvider(new PortInUseLinks()),
    vscode.commands.registerCommand("portpilot.refresh", refresh),
    vscode.commands.registerCommand("portpilot.toggleScope", async () => {
      const config = vscode.workspace.getConfiguration("portpilot");
      await config.update("scope", config.get("scope") === "all" ? "workspace" : "all", vscode.ConfigurationTarget.Global);
    }),
    vscode.commands.registerCommand("portpilot.cleanup", (node?: Node) => cleanup(refresh, node?.kind === "project" ? node.label : undefined)),
    vscode.commands.registerCommand("portpilot.whoIsOn", (port?: number) => whoIsOn(typeof port === "number" ? port : undefined, refresh)),
    vscode.commands.registerCommand("portpilot.freePort", () => freePort()),
    vscode.commands.registerCommand("portpilot.open", (arg) => {
      const e = entryOf(arg);
      return e && vscode.env.openExternal(vscode.Uri.parse(url(e)));
    }),
    vscode.commands.registerCommand("portpilot.logs", (arg) => entryOf(arg) && showLogs(entryOf(arg)!)),
    vscode.commands.registerCommand("portpilot.health", (arg) => entryOf(arg) && health(entryOf(arg)!)),
    vscode.commands.registerCommand("portpilot.stop", (arg) => entryOf(arg) && stop(entryOf(arg)!, refresh)),
    vscode.commands.registerCommand("portpilot.copyUrl", (arg) => entryOf(arg) && vscode.env.clipboard.writeText(url(entryOf(arg)!))),
    vscode.commands.registerCommand("portpilot.copyPid", (arg) => entryOf(arg) && vscode.env.clipboard.writeText(String(entryOf(arg)!.pid))),
    vscode.commands.registerCommand("portpilot.setupClaude", async () => {
      const text = await run(["setup", "claude"], 60000).catch((e: Error) => e.message);
      vscode.window.showInformationMessage(text.trim().split("\n").join(" "));
    }),
  );

  // Offer the MCP server to agents in VS Code (Copilot agent mode and other MCP clients).
  if (vscode.workspace.getConfiguration("portpilot").get<boolean>("registerMcpServer") && "registerMcpServerDefinitionProvider" in vscode.lm) {
    context.subscriptions.push(
      vscode.lm.registerMcpServerDefinitionProvider("portpilot.mcp", {
        provideMcpServerDefinitions: () => (cli ? [new vscode.McpStdioServerDefinition("PortPilot", cli, ["mcp"])] : []),
      }),
    );
  }

  return {
    cliPath: () => cli,
    entries: () => entries,
    workspaceEntries: () => entries.filter(belongsToWorkspace),
    recent: () => watch.recent,
    refresh,
  };
}

/** Toasts for this workspace's servers stopping (with Start again) and, if asked for, starting. */
async function announce(changes: { stopped: RecentServer[]; started: { port: number; process: string; project?: { name: string } }[] }): Promise<void> {
  const config = vscode.workspace.getConfiguration("portpilot");
  if (config.get<boolean>("notifyOnStop")) {
    for (const r of changes.stopped) {
      const where = r.project ? `${r.project}: ` : "";
      const choice = await vscode.window.showWarningMessage(`${where}${r.service ?? r.process} on :${r.port} stopped.`, "Start Again", "Show Recent");
      if (choice === "Start Again") {
        runAgain(r);
      } else if (choice === "Show Recent") {
        void vscode.commands.executeCommand("portpilot.recent.focus");
      }
    }
  }
  if (config.get<boolean>("notifyOnStart")) {
    for (const e of changes.started) {
      const choice = await vscode.window.showInformationMessage(`${e.project ? `${e.project.name}: ` : ""}${e.process} started on :${e.port}.`, "Open");
      if (choice === "Open") {
        void vscode.env.openExternal(vscode.Uri.parse(`http://localhost:${e.port}`));
      }
    }
  }
}

export function deactivate(): void {}
