// Runs the extension inside a downloaded VS Code: node test/run.js <workspace-folder> <cli-path>
const path = require("node:path");
const { runTests } = require("@vscode/test-electron");

runTests({
  extensionDevelopmentPath: path.resolve(__dirname, ".."),
  extensionTestsPath: path.resolve(__dirname, process.argv[4] === "screenshots" ? "screenshots.js" : "suite.js"),
  launchArgs: [process.argv[2], "--disable-extensions", "--skip-welcome", "--disable-workspace-trust"],
  extensionTestsEnv: { PORTPILOT_TEST_CLI: process.argv[3], PORTPILOT_SHOTS: process.argv[5] ?? "" },
}).catch((error) => {
  console.error(error);
  process.exit(1);
});
