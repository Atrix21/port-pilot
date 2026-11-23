// Smoke test: a dev server started in the workspace shows up in the extension, and the commands run.
const assert = require("node:assert");
const { spawn } = require("node:child_process");
const vscode = require("vscode");

exports.run = async function run() {
  await vscode.workspace.getConfiguration("portpilot").update("cliPath", process.env.PORTPILOT_TEST_CLI, vscode.ConfigurationTarget.Global);
  const folder = vscode.workspace.workspaceFolders[0].uri.fsPath;
  const server = spawn("python3", ["-m", "http.server", "5179", "--bind", "127.0.0.1"], { cwd: folder, stdio: "ignore" });
  try {
    const extension = vscode.extensions.getExtension("Atrix21.portpilot-dev");
    const api = await extension.activate();
    assert.ok(api.cliPath(), "the CLI is resolved");

    let mine = [];
    for (let i = 0; i < 40 && !mine.some((e) => e.port === 5179); i++) {
      await new Promise((r) => setTimeout(r, 250));
      await api.refresh();
      mine = api.workspaceEntries();
    }

    const entry = mine.find((e) => e.port === 5179);
    assert.ok(entry, "the workspace's server is listed");
    assert.strictEqual(entry.process.startsWith("python"), true);
    assert.ok(entry.project, "it is attributed to the workspace's git project");

    const commands = await vscode.commands.getCommands(true);
    for (const id of ["portpilot.refresh", "portpilot.whoIsOn", "portpilot.freePort", "portpilot.cleanup", "portpilot.stop", "portpilot.logs"]) {
      assert.ok(commands.includes(id), `${id} is registered`);
    }

    await vscode.commands.executeCommand("portpilot.refresh");
    console.log(`PortPilot extension OK: ${api.entries().length} endpoints, ${mine.length} in the workspace, :5179 → ${entry.process} (${entry.project.name} · ${entry.project.branch})`);
  } finally {
    server.kill();
  }
};
