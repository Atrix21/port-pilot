// Smoke test: a dev server started in the workspace shows up in the extension, and the commands run.
const assert = require("node:assert");
const { spawn } = require("node:child_process");
const vscode = require("vscode");

exports.run = async function run() {
  await vscode.workspace.getConfiguration("portpilot").update("cliPath", process.env.PORTPILOT_TEST_CLI, vscode.ConfigurationTarget.Global);
  await vscode.workspace.getConfiguration("portpilot").update("view", "overview", vscode.ConfigurationTarget.Global);
  await vscode.workspace.getConfiguration("portpilot").update("scope", "workspace", vscode.ConfigurationTarget.Global);
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

    // The stream groups it into an app and measures it.
    let app;
    for (let i = 0; i < 40 && !app; i++) {
      await new Promise((r) => setTimeout(r, 250));
      app = api.apps().find((a) => a.ports.includes(5179));
    }
    assert.ok(app, "the stream reports the server as an app");
    assert.strictEqual(app.name, "Static file server");
    assert.ok(app.memory > 1024 * 1024, `memory measured (${app.memory})`);
    console.log(`App OK: ${app.name}, ${Math.round(app.memory / 1048576)} MB, ${app.cpu}% CPU, project ${app.project}`);

    const commands = await vscode.commands.getCommands(true);
    for (const id of ["portpilot.refresh", "portpilot.whoIsOn", "portpilot.freePort", "portpilot.cleanup", "portpilot.stop", "portpilot.logs"]) {
      assert.ok(commands.includes(id), `${id} is registered`);
    }

    await vscode.commands.executeCommand("portpilot.refresh");

    // A server that stops is remembered, and Start Again brings it back from a terminal.
    await vscode.workspace.getConfiguration("portpilot").update("notifyOnStop", false, vscode.ConfigurationTarget.Global);
    server.kill();
    for (let i = 0; i < 3; i++) {
      await api.refresh();
    }
    const recent = api.recent().find((r) => r.port === 5179);
    assert.ok(recent, "the stopped server is in Recently Stopped");
    assert.ok(recent.commandLine.includes("http.server 5179"), recent.commandLine);
    await vscode.commands.executeCommand("portpilot.runAgain", recent);
    let back;
    for (let i = 0; i < 40 && !back; i++) {
      await new Promise((r) => setTimeout(r, 250));
      await api.refresh();
      back = api.workspaceEntries().find((e) => e.port === 5179);
    }
    assert.ok(back, "Start Again brought the server back");
    assert.ok(!api.recent().some((r) => r.port === 5179), "a running server leaves Recently Stopped");
    await new Promise((resolve) => require("node:child_process").execFile(process.env.PORTPILOT_TEST_CLI, ["kill", "pid:" + back.pid, "-y"], resolve));
    console.log("Recently Stopped and Start Again OK");
    console.log(`PortPilot extension OK: ${api.entries().length} endpoints, ${mine.length} in the workspace, :5179 → ${entry.process} (${entry.project.name} · ${entry.project.branch})`);
  } finally {
    server.kill();
  }
};
