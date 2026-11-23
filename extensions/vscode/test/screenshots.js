// Captures the Marketplace screenshots inside a real VS Code on a virtual display:
//   xvfb-run -s "-screen 0 1600x1000x24" node test/run.js <demo-workspace> <cli> screenshots <out-dir>
const { execFileSync } = require("node:child_process");
const path = require("node:path");
const vscode = require("vscode");

const wait = (ms) => new Promise((r) => setTimeout(r, ms));

function shot(name) {
  const out = path.join(process.env.PORTPILOT_SHOTS, `${name}.png`);
  // Below the window's title bar (which names the development host), left part of the window.
  execFileSync("import", ["-window", "root", "-crop", "1600x964+0+36", "+repage", out]);
  console.log("captured", out);
}

exports.run = async function run() {
  const config = vscode.workspace.getConfiguration("portpilot");
  await config.update("cliPath", process.env.PORTPILOT_TEST_CLI, vscode.ConfigurationTarget.Global);
  await config.update("notifyOnStop", true, vscode.ConfigurationTarget.Global);
  await config.update("view", "overview", vscode.ConfigurationTarget.Global);
  await vscode.workspace.getConfiguration("workbench").update("startupEditor", "none", vscode.ConfigurationTarget.Global);

  // A full-size window, the PortPilot sidebar, and a project file open beside it.
  try {
    execFileSync("xdotool", ["search", "--onlyvisible", "--class", "code", "windowsize", "%@", "1600", "1000", "windowmove", "%@", "0", "0"]);
  } catch {}
  const api = await vscode.extensions.getExtension("Atrix21.portpilot-dev").activate();
  await vscode.commands.executeCommand("workbench.action.closeAllEditors");
  const folder = vscode.workspace.workspaceFolders[0].uri;
  await vscode.window.showTextDocument(vscode.Uri.joinPath(folder, "atlas-web", "server.mjs"), { preview: false });
  await vscode.commands.executeCommand("workbench.view.extension.portpilot");
  await vscode.commands.executeCommand("workbench.action.closeAuxiliaryBar");
  // Widen the sidebar by dragging its edge.
  await wait(800);
  try {
    execFileSync("xdotool", ["mousemove", "349", "600", "sleep", "0.3", "mousedown", "1", "sleep", "0.2",
      "mousemove", "380", "600", "sleep", "0.1", "mousemove", "420", "600", "sleep", "0.1", "mousemove", "440", "600", "sleep", "0.1",
      "mousemove", "560", "600", "sleep", "0.1", "mousemove", "640", "600", "sleep", "0.3", "mouseup", "1", "mousemove", "1000", "700"]);
  } catch {}
  for (let i = 0; i < 6; i++) {
    await api.refresh();
    await wait(500);
  }
  await vscode.commands.executeCommand("notifications.clearAll");
  await wait(4000); // a few stream scans, so CPU % has something to compare
  shot("vscode-overview");

  await config.update("view", "ports", vscode.ConfigurationTarget.Global);
  await api.refresh();
  await wait(1500);
  shot("vscode-ports");

  // A dev server stops: the toast with Start Again, and the Recently Stopped list.
  const atlas = api.workspaceEntries().find((e) => e.port === 5173);
  process.kill(atlas.pid);
  for (let i = 0; i < 3; i++) {
    await api.refresh();
    await wait(400);
  }
  await vscode.commands.executeCommand("workbench.view.extension.portpilot");
  await wait(1500);
  shot("vscode-stopped");

  // Who is on a port: the quick pick (also what an "address already in use" terminal link opens).
  void vscode.commands.executeCommand("portpilot.whoIsOn", 8000);
  await wait(2500);
  shot("vscode-who");
  await vscode.commands.executeCommand("workbench.action.closeQuickOpen");
};
