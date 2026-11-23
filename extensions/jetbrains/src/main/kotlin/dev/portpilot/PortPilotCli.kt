package dev.portpilot

import com.google.gson.Gson
import com.intellij.openapi.project.Project
import java.io.File
import java.util.concurrent.TimeUnit

/** One endpoint, as printed by `portpilot list --json`. */
data class PortEntry(
    val port: Int = 0,
    val protocol: String = "",
    val address: String = "",
    val state: String = "",
    val pid: Int = 0,
    val process: String = "",
    val executable: String? = null,
    val commandLine: String? = null,
    val started: String? = null,
    val service: String? = null,
    val workingDirectory: String? = null,
    val project: ProjectRef? = null,
    val agent: AgentRef? = null,
    val orphaned: Boolean = false,
) {
    val isHttp: Boolean
        get() = protocol.startsWith("tcp") && state == "Listening" &&
            !Regex("PostgreSQL|MySQL|Redis|MongoDB|RabbitMQ|Kafka|SQL Server|Memcached|Elasticsearch", RegexOption.IGNORE_CASE).containsMatchIn(service ?: "")

    val url: String
        get() = "${if (port == 443 || (service ?: "").contains("HTTPS", true)) "https" else "http"}://localhost:$port"

    val agentText: String?
        get() = agent?.let { a -> shortAgent(a.name) + if (a.running) "" else " · ended" }

    val status: String
        get() = when {
            orphaned -> "Left behind"
            state == "Listening" -> "Listening"
            state == "Bound" -> "Bound"
            state == "Established" -> "Connected"
            else -> state
        }

    /** Whether this endpoint belongs to the IDE project, by repository root or working directory. */
    fun belongsTo(project: Project): Boolean {
        val base = project.basePath?.let(::normalise) ?: return false
        return listOfNotNull(this.project?.root, workingDirectory).map(::normalise).any { p ->
            p == base || p.startsWith("$base/") || base.startsWith("$p/")
        }
    }

    private fun normalise(path: String): String {
        val n = path.replace('\\', '/').trimEnd('/')
        return if (PortPilotCli.isWindows || PortPilotCli.isMac) n.lowercase() else n
    }

    private fun shortAgent(name: String) = when (name) {
        "Claude Code" -> "Claude"
        "Gemini CLI" -> "Gemini"
        "Cursor Agent" -> "Cursor"
        else -> name
    }
}

/** An app, as printed by `portpilot apps --json`: a dev server with its workers, a database, … */
data class AppEntry(
    val key: String = "",
    val name: String = "",
    val kind: String? = null,
    val pid: Int = 0,
    val pids: List<Int> = emptyList(),
    val processes: Int = 0,
    val cpu: Double = 0.0,
    val memory: Long = 0,
    val ports: List<Int> = emptyList(),
    val started: String? = null,
    val commandLine: String? = null,
    val workingDirectory: String? = null,
    val project: String? = null,
    val branch: String? = null,
    val agent: String? = null,
    val agentRunning: Boolean? = null,
    val leftBehind: Boolean = false,
    val tooling: Boolean = false,
    val system: Boolean = false,
) {
    val portsText: String get() = if (ports.isEmpty()) "" else ":${ports[0]}" + if (ports.size > 1) " +${ports.size - 1}" else ""
    val memoryText: String get() = when {
        memory <= 0 -> "—"
        memory < 1L shl 30 -> "${memory shr 20} MB"
        else -> "%.1f GB".format(memory / (1L shl 30).toDouble())
    }
    val cpuText: String get() = if (memory <= 0) "—" else "%.1f%%".format(cpu)
    val status: String get() = if (leftBehind) "Left behind" else "Running"
    val agentText: String get() = agent?.let { (if (it == "Claude Code") "Claude" else it) + if (agentRunning == false) " · ended" else "" } ?: ""

    fun belongsTo(project: com.intellij.openapi.project.Project): Boolean {
        val base = project.basePath?.replace('\\', '/')?.trimEnd('/') ?: return false
        val dir = workingDirectory?.replace('\\', '/')?.trimEnd('/') ?: return false
        return dir == base || dir.startsWith("$base/") || base.startsWith("$dir/")
    }
}

data class ProjectRef(val name: String = "", val branch: String? = null, val root: String = "", val worktree: Boolean = false)

data class AgentRef(val name: String = "", val pid: Int = 0, val running: Boolean = true)

/**
 * The `portpilot` terminal app does the work: the same engine as the desktop apps, on Linux, macOS and Windows.
 */
object PortPilotCli {
    private val gson = Gson()
    private val os = System.getProperty("os.name").lowercase()
    val isWindows = os.contains("win")
    val isMac = os.contains("mac")

    /** The configured path, then the usual install locations, then PATH. */
    fun resolve(): String? {
        PortPilotSettings.instance.state.cliPath.takeIf { it.isNotBlank() && File(it).canExecute() }?.let { return it }
        val exe = if (isWindows) "portpilot.exe" else "portpilot"
        val home = System.getProperty("user.home")
        val candidates = buildList {
            System.getenv("PATH")?.split(File.pathSeparator)?.forEach { add(File(it, exe)) }
            if (isWindows) System.getenv("LOCALAPPDATA")?.let { add(File(it, "Programs/PortPilot/cli/$exe")) }
            else {
                add(File("/opt/homebrew/bin", exe))
                add(File("/usr/local/bin", exe))
                add(File(home, ".local/bin/$exe"))
            }
        }
        return candidates.firstOrNull { it.isFile && it.canExecute() }?.path
    }

    fun list(): List<PortEntry> {
        val json = run("list", "--json") ?: return emptyList()
        return gson.fromJson(json, Array<PortEntry>::class.java)?.toList() ?: emptyList()
    }

    /** Apps with CPU and memory; the CLI takes two samples 300 ms apart. */
    fun apps(): List<AppEntry> {
        val json = run("apps", "--json") ?: return emptyList()
        return gson.fromJson(json, Array<AppEntry>::class.java)?.toList() ?: emptyList()
    }

    fun stop(pid: Int): String = run("kill", "pid:$pid", "-y")?.trim().orEmpty()

    fun logs(pid: Int): String = run("logs", "pid:$pid", "200").orEmpty()

    fun health(port: Int): String = run("health", port.toString())?.trim()?.replaceFirst(Regex("^\\S+\\s+"), "") .orEmpty()

    fun setupClaude(): String = run("setup", "claude", timeoutSeconds = 60)?.trim().orEmpty()

    /** Runs the CLI and returns stdout (also for exit code 1, which means "nothing there"), or null if it can't run. */
    fun run(vararg args: String, timeoutSeconds: Long = 15): String? {
        val cli = resolve() ?: return null
        return try {
            val process = ProcessBuilder(listOf(cli) + args).redirectErrorStream(false).start()
            val output = process.inputStream.bufferedReader().readText()
            if (!process.waitFor(timeoutSeconds, TimeUnit.SECONDS)) {
                process.destroy()
            }
            output
        } catch (e: Exception) {
            null
        }
    }
}
