import * as vscode from "vscode";

/** What a process is, at a glance: an icon colour and a word, from its executable and command line. */
export interface Runtime {
  label: string;
  color: string;
}

const RUNTIMES: { label: string; color: string; match: RegExp }[] = [
  { label: "Database", color: "charts.yellow", match: /^(postgres|mysqld?|mariadbd?|mongod|redis-server|valkey-server|memcached|clickhouse|cockroach|sqlservr|influxd|etcd|cassandra|elasticsearch|opensearch|rabbitmq|beam\.smp|kafka|nats-server|minio)/i },
  { label: "Docker", color: "charts.blue", match: /^(docker|com\.docker|dockerd|containerd|podman|rootlessport|vpnkit|gvproxy|colima|orbstack)/i },
  { label: "Node.js", color: "charts.green", match: /^(node|nodejs|npm|npx|pnpm|yarn|tsx|ts-node|electron)\b/i },
  { label: "Bun", color: "charts.green", match: /^bun\b/i },
  { label: "Deno", color: "charts.green", match: /^deno\b/i },
  { label: "Python", color: "charts.blue", match: /^(python[0-9.]*|uvicorn|gunicorn|hypercorn|flask|django|jupyter|uv|pythonw)\b/i },
  { label: ".NET", color: "charts.purple", match: /^(dotnet|.*\.(dll|exe)$)/i },
  { label: "Java", color: "charts.orange", match: /^(java|javaw|gradle|kotlin|mvn)\b/i },
  { label: "Go", color: "charts.blue", match: /^(go|air|.*-go)$/i },
  { label: "Ruby", color: "charts.red", match: /^(ruby|puma|rails|unicorn|bundle|jekyll)\b/i },
  { label: "PHP", color: "charts.purple", match: /^(php|php-fpm|frankenphp)/i },
  { label: "Rust", color: "charts.orange", match: /^(cargo|trunk)$/i },
  { label: "Hugo", color: "charts.red", match: /^hugo$/i },
];

const SERVICES: Record<string, string> = {
  PostgreSQL: "Database", MySQL: "Database", Redis: "Database", MongoDB: "Database", "SQL Server": "Database",
};

export function runtimeOf(process: string, service?: string | null, commandLine?: string | null): Runtime | undefined {
  const name = process.replace(/\.exe$/i, "");
  const hit = RUNTIMES.find((r) => r.match.test(name)) ?? (commandLine ? RUNTIMES.find((r) => r.match.test(commandLine.split(/[\\/ ]/).find((p) => p) ?? "")) : undefined);
  if (hit) {
    return { label: hit.label, color: hit.color };
  }
  if (service && SERVICES[service]) {
    return { label: SERVICES[service], color: "charts.yellow" };
  }
  return undefined;
}

export function runtimeIcon(runtime: Runtime | undefined, warning: boolean, listening: boolean): vscode.ThemeIcon {
  if (warning) {
    return new vscode.ThemeIcon("warning", new vscode.ThemeColor("list.warningForeground"));
  }
  return new vscode.ThemeIcon(listening ? "circle-filled" : "circle-outline", new vscode.ThemeColor(runtime?.color ?? "testing.iconPassed"));
}

/** "4m", "2h", "3d" since an ISO start time. */
export function uptime(started?: string): string | undefined {
  if (!started) {
    return undefined;
  }
  const seconds = Math.max(0, (Date.now() - new Date(started).getTime()) / 1000);
  return seconds < 60 ? `${Math.floor(seconds)}s` : seconds < 3600 ? `${Math.floor(seconds / 60)}m` : seconds < 172800 ? `${Math.floor(seconds / 3600)}h` : `${Math.floor(seconds / 86400)}d`;
}
