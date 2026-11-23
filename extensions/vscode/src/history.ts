import * as vscode from "vscode";

/** A dev server that ran in this workspace and has stopped: enough to start it again. */
export interface RecentServer {
  key: string;
  port: number;
  process: string;
  commandLine: string;
  workingDirectory: string;
  project?: string;
  branch?: string | null;
  service?: string | null;
  agent?: string;
  started?: string;
  stopped: string;
}

interface Watched {
  port: number;
  pid: number;
  process: string;
  commandLine?: string | null;
  workingDirectory?: string | null;
  project?: { name: string; branch?: string | null };
  service?: string | null;
  agent?: { name: string };
  started?: string;
  protocol: string;
  state: string;
}

const STORE = "portpilot.recentServers";
const LIMIT = 25;

/**
 * Notices when this workspace's TCP servers stop, remembers how they were started, and offers to run them
 * again. A server must be missing from two scans in a row before it counts as stopped, so a watcher's
 * quick restart doesn't raise an alert.
 */
export class ServerWatch {
  private previous = new Map<string, Watched>();
  private missing = new Map<string, number>();
  private readonly expected = new Set<number>();
  private primed = false;
  private readonly changed = new vscode.EventEmitter<void>();
  readonly onDidChange = this.changed.event;

  constructor(private readonly state: vscode.Memento) {}

  /** Stops PortPilot itself performs are expected, not alerts. */
  expectStop(pid: number): void {
    this.expected.add(pid);
  }

  get recent(): RecentServer[] {
    return this.state.get<RecentServer[]>(STORE, []);
  }

  async forget(key: string): Promise<void> {
    await this.state.update(STORE, this.recent.filter((r) => r.key !== key));
    this.changed.fire();
  }

  async clear(): Promise<void> {
    await this.state.update(STORE, []);
    this.changed.fire();
  }

  /** Feed each scan of the workspace's endpoints; returns servers that just stopped and servers that just started. */
  async update(current: Watched[]): Promise<{ stopped: RecentServer[]; started: Watched[] }> {
    const servers = current.filter((e) => e.protocol.startsWith("tcp") && e.state === "Listening" && e.commandLine && e.workingDirectory);
    const now = new Map(servers.map((e) => [keyOf(e), e]));
    const stopped: RecentServer[] = [];
    const started: Watched[] = [];

    if (this.primed) {
      for (const [key, e] of now) {
        if (!this.previous.has(key)) {
          started.push(e);
        }
      }
    }

    for (const [key, e] of this.previous) {
      if (now.has(key)) {
        this.missing.delete(key);
        continue;
      }
      const misses = (this.missing.get(key) ?? 0) + 1;
      if (misses < 2) {
        this.missing.set(key, misses);
        now.set(key, e); // keep watching it for one more scan
        continue;
      }
      this.missing.delete(key);
      const expected = this.expected.delete(e.pid);
      const record = toRecent(e);
      await this.remember(record);
      if (!expected) {
        stopped.push(record);
      }
    }

    // Anything running again is no longer "recent".
    const running = new Set([...now.values()].map((e) => recentKey(e)));
    if (this.recent.some((r) => running.has(r.key))) {
      await this.state.update(STORE, this.recent.filter((r) => !running.has(r.key)));
      this.changed.fire();
    }

    this.previous = now;
    this.primed = true;
    return { stopped, started };
  }

  private async remember(record: RecentServer): Promise<void> {
    const list = [record, ...this.recent.filter((r) => r.key !== record.key)].slice(0, LIMIT);
    await this.state.update(STORE, list);
    this.changed.fire();
  }
}

function keyOf(e: Watched): string {
  return `${e.pid}:${e.port}`;
}

/** Same command in the same folder on the same port is the same server, whatever its PID. */
function recentKey(e: { port: number; commandLine?: string | null; workingDirectory?: string | null }): string {
  return `${e.port}|${e.workingDirectory}|${e.commandLine}`;
}

function toRecent(e: Watched): RecentServer {
  return {
    key: recentKey(e),
    port: e.port,
    process: e.process,
    commandLine: e.commandLine!,
    workingDirectory: e.workingDirectory!,
    project: e.project?.name,
    branch: e.project?.branch,
    service: e.service,
    agent: e.agent?.name,
    started: e.started,
    stopped: new Date().toISOString(),
  };
}

/** Starts a recorded server again in a terminal, in the folder it ran from. */
export function runAgain(server: RecentServer): void {
  const terminal = vscode.window.createTerminal({
    name: `${server.process} :${server.port}`,
    cwd: server.workingDirectory,
    iconPath: new vscode.ThemeIcon("debug-restart"),
  });
  terminal.show(true);
  terminal.sendText(server.commandLine, true);
}

export function ago(iso: string): string {
  const minutes = Math.max(0, (Date.now() - new Date(iso).getTime()) / 60000);
  return minutes < 1 ? "just now" : minutes < 60 ? `${Math.floor(minutes)}m ago` : minutes < 2880 ? `${Math.floor(minutes / 60)}h ago` : `${Math.floor(minutes / 1440)}d ago`;
}
