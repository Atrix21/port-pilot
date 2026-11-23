using PortPilot.Core.Model;

namespace PortPilot.Core.Processes;

/// <summary>
/// Recognises developer tooling that listens on ports but isn't anything a person started on purpose:
/// editors and their helpers, language servers, build daemons. Callers use it to keep these out of the
/// way (status lines, the dev-server guard, summarised views), not to hide them entirely.
/// </summary>
public static class ToolingDetector
{
    private static readonly string[] Editors =
    [
        "code", "code-insiders", "codium", "cursor", "windsurf", "zed", "zeditor", "sublime_text", "idea", "idea64", "webstorm", "webstorm64",
        "pycharm", "pycharm64", "rider", "rider64", "goland", "goland64", "clion", "clion64", "rustrover", "phpstorm", "datagrip", "fleet",
        "studio", "studio64", "devenv", "xcode",
    ];

    private static readonly string[] Markers =
    [
        "GradleDaemon", "GradleWrapperMain", "KotlinCompileDaemon", "kotlin.daemon", "org.jetbrains.jps", "--type=utility", "--type=renderer",
        "language-server", "languageserver", "language_server", "tsserver", "typescript-language", "pylance", "pyright", "gopls", "rust-analyzer",
        "clangd", "omnisharp", "roslyn", "copilot", "jdtls", "eclipse.jdt.ls", "sorbet", "solargraph", "intelephense",
    ];

    public static bool IsTooling(ProcessDetails process)
    {
        if (Editors.Contains(process.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var cmd = process.CommandLine;
        return cmd is not null && Markers.Any(m => cmd.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
