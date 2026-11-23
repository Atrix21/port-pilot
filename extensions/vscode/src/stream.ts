import { ChildProcessWithoutNullStreams, spawn } from "node:child_process";

/**
 * One long-lived `portpilot stream` process that prints a JSON line per scan, instead of spawning the CLI
 * every few seconds. It keeps the engine's caches warm (so scans stay ~1 ms) and makes CPU % possible.
 * Started while the window is focused, stopped when it isn't; closing its stdin makes it exit, so a
 * crashed editor never leaves one behind.
 */
export class ScanStream {
  private child: ChildProcessWithoutNullStreams | undefined;
  private buffer = "";
  private key = "";

  constructor(private readonly onScan: (data: unknown) => void, private readonly onError: (message: string) => void) {}

  get running(): boolean {
    return this.child !== undefined;
  }

  /** Starts (or restarts, if the options changed) the stream. */
  start(cli: string, seconds: number, apps: boolean): void {
    const key = `${cli}|${seconds}|${apps}`;
    if (this.child && key === this.key) {
      return;
    }

    this.stop();
    this.key = key;
    const args = ["stream", String(seconds), ...(apps ? ["--apps"] : [])];
    const child = spawn(cli, args, { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    this.child = child;
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk: string) => {
      this.buffer += chunk;
      let newline: number;
      while ((newline = this.buffer.indexOf("\n")) >= 0) {
        const line = this.buffer.slice(0, newline).trim();
        this.buffer = this.buffer.slice(newline + 1);
        if (line) {
          try {
            this.onScan(JSON.parse(line));
          } catch (error) {
            this.onError(`Unreadable scan: ${(error as Error).message}`);
          }
        }
      }
    });
    child.stderr.on("data", (chunk: Buffer) => this.onError(chunk.toString().trim()));
    child.on("exit", () => {
      if (this.child === child) {
        this.child = undefined;
      }
    });
    child.on("error", (error) => this.onError(error.message));
  }

  stop(): void {
    const child = this.child;
    this.child = undefined;
    this.buffer = "";
    if (child) {
      child.stdin.end(); // the stream exits when its stdin closes
      setTimeout(() => child.exitCode === null && child.kill(), 1000);
    }
  }
}
