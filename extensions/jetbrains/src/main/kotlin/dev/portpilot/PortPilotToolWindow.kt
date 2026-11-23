package dev.portpilot

import com.intellij.icons.AllIcons
import com.intellij.ide.BrowserUtil
import com.intellij.notification.NotificationGroupManager
import com.intellij.notification.NotificationType
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.actionSystem.ToggleAction
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.ide.CopyPasteManager
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.openapi.ui.SimpleToolWindowPanel
import com.intellij.openapi.wm.ToolWindow
import com.intellij.openapi.wm.ToolWindowFactory
import com.intellij.ui.PopupHandler
import com.intellij.ui.ScrollPaneFactory
import com.intellij.ui.content.ContentFactory
import com.intellij.ui.table.JBTable
import com.intellij.util.concurrency.AppExecutorUtil
import java.awt.datatransfer.StringSelection
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import javax.swing.table.AbstractTableModel

class PortPilotToolWindowFactory : ToolWindowFactory, DumbAware {
    override fun createToolWindowContent(project: Project, toolWindow: ToolWindow) {
        val panel = PortsPanel(project, toolWindow)
        val content = ContentFactory.getInstance().createContent(panel, "", false)
        content.setDisposer(panel)
        toolWindow.contentManager.addContent(content)
    }
}

private class PortsModel : AbstractTableModel() {
    var rows: List<PortEntry> = emptyList()
        set(value) {
            field = value
            fireTableDataChanged()
        }

    private val columns = arrayOf("Port", "Process", "PID", "Project", "Agent", "Protocol", "Address", "Status")

    override fun getRowCount() = rows.size
    override fun getColumnCount() = columns.size
    override fun getColumnName(column: Int) = columns[column]
    override fun getColumnClass(column: Int): Class<*> = if (column == 0 || column == 2) Integer::class.java else String::class.java

    override fun getValueAt(row: Int, column: Int): Any? {
        val e = rows[row]
        return when (column) {
            0 -> e.port
            1 -> e.process + (e.service?.let { " · $it" } ?: "")
            2 -> e.pid
            3 -> e.project?.let { p -> p.name + (p.branch?.let { " · $it" } ?: "") } ?: ""
            4 -> e.agentText ?: ""
            5 -> e.protocol
            6 -> e.address
            else -> e.status
        }
    }
}

private class PortsPanel(private val project: Project, private val toolWindow: ToolWindow) : SimpleToolWindowPanel(true, true), com.intellij.openapi.Disposable {
    private val model = PortsModel()
    private val table = JBTable(model).apply {
        autoCreateRowSorter = true
        setShowGrid(false)
        emptyText.text = "Scanning…"
    }
    private var timer: ScheduledFuture<*>? = null

    init {
        setContent(ScrollPaneFactory.createScrollPane(table))
        toolbar = ActionManager.getInstance().createActionToolbar("PortPilot", toolbarActions(), true).also { it.targetComponent = this }.component
        PopupHandler.installPopupMenu(table, rowActions(), "PortPilotRow")
        table.addMouseListener(object : MouseAdapter() {
            override fun mouseClicked(e: MouseEvent) {
                if (e.clickCount == 2) selected()?.takeIf { it.isHttp }?.let { BrowserUtil.browse(it.url) }
            }
        })
        schedule()
    }

    private fun schedule() {
        timer?.cancel(false)
        val seconds = PortPilotSettings.instance.state.refreshSeconds.toLong().coerceAtLeast(1)
        timer = AppExecutorUtil.getAppScheduledExecutorService().scheduleWithFixedDelay({
            if (toolWindow.isVisible) refreshNow()
        }, 0, seconds, TimeUnit.SECONDS)
    }

    fun refreshNow() {
        if (PortPilotCli.resolve() == null) {
            ApplicationManager.getApplication().invokeLater {
                table.emptyText.text = "The portpilot terminal app wasn't found. Install PortPilot, or set its path in Settings → Tools → PortPilot."
                model.rows = emptyList()
            }
            return
        }

        val all = PortPilotCli.list()
        val visible = if (PortPilotSettings.instance.state.showAllPorts) all else all.filter { it.belongsTo(project) }
        ApplicationManager.getApplication().invokeLater {
            val keep = selected()
            table.emptyText.text = if (PortPilotSettings.instance.state.showAllPorts) "Nothing is listening." else "Nothing in this project is listening. Toggle the filter to see every port."
            model.rows = visible.sortedWith(compareByDescending<PortEntry> { it.orphaned }.thenBy { it.port })
            keep?.let { k -> model.rows.indexOfFirst { it.pid == k.pid && it.port == k.port }.takeIf { it >= 0 }?.let { i -> table.selectionModel.setSelectionInterval(table.convertRowIndexToView(i), table.convertRowIndexToView(i)) } }
            toolWindow.setTitle(if (all.any { it.orphaned }) "${all.count { it.orphaned }} left behind" else "")
        }
    }

    private fun selected(): PortEntry? = table.selectedRow.takeIf { it >= 0 }?.let { model.rows.getOrNull(table.convertRowIndexToModel(it)) }

    private fun background(task: () -> Unit) = ApplicationManager.getApplication().executeOnPooledThread(task)

    private fun notify(text: String, type: NotificationType = NotificationType.INFORMATION) =
        NotificationGroupManager.getInstance().getNotificationGroup("PortPilot").createNotification(text, type).notify(project)

    private fun toolbarActions() = DefaultActionGroup().apply {
        add(action("Refresh", AllIcons.Actions.Refresh) { background(::refreshNow) })
        add(object : ToggleAction("Show Every Port", "Every listening port on the machine, not just this project's", AllIcons.General.Filter), DumbAware {
            override fun isSelected(e: AnActionEvent) = PortPilotSettings.instance.state.showAllPorts
            override fun setSelected(e: AnActionEvent, state: Boolean) {
                PortPilotSettings.instance.state.showAllPorts = state
                background(::refreshNow)
            }
            override fun getActionUpdateThread() = ActionUpdateThread.BGT
        })
        add(action("Clean Up What Agents Left Behind", AllIcons.Actions.GC) { cleanup() })
        addSeparator()
        add(action("Set Up Claude Code Integration", AllIcons.General.Settings) {
            background { notify(PortPilotCli.setupClaude().ifBlank { "Couldn't run portpilot setup claude." }) }
        })
    }

    private fun rowActions() = DefaultActionGroup().apply {
        add(action("Open in Browser", AllIcons.General.Web) { selected()?.takeIf { it.isHttp }?.let { BrowserUtil.browse(it.url) } })
        add(action("Show Output", AllIcons.Debugger.Console) {
            val e = selected() ?: return@action
            background {
                val text = PortPilotCli.logs(e.pid)
                ApplicationManager.getApplication().invokeLater { Messages.showInfoMessage(project, text.takeLast(8000), ":${e.port} ${e.process}") }
            }
        })
        add(action("Check Health", AllIcons.Actions.Lightning) {
            val e = selected() ?: return@action
            background { notify(":${e.port} ${PortPilotCli.health(e.port)}") }
        })
        add(action("Copy URL", AllIcons.Actions.Copy) { selected()?.let { CopyPasteManager.getInstance().setContents(StringSelection(it.url)) } })
        add(action("Copy PID", null) { selected()?.let { CopyPasteManager.getInstance().setContents(StringSelection(it.pid.toString())) } })
        addSeparator()
        add(action("Stop Process…", AllIcons.Actions.Suspend) {
            val e = selected() ?: return@action
            val detail = listOfNotNull(e.commandLine, e.agent?.let { if (it.running) "Started by ${it.name}" else "Left behind by an ended ${it.name} session" }).joinToString("\n\n")
            if (Messages.showYesNoDialog(project, "Stop ${e.process} (PID ${e.pid}) on :${e.port}?\n\n$detail", "Stop Process", "Stop", "Cancel", Messages.getWarningIcon()) == Messages.YES) {
                background {
                    notify(PortPilotCli.stop(e.pid).ifBlank { "Stopped ${e.process}." })
                    refreshNow()
                }
            }
        })
    }

    private fun cleanup() {
        background {
            val leftovers = PortPilotCli.list().filter { it.orphaned }.distinctBy { it.pid }
            ApplicationManager.getApplication().invokeLater {
                if (leftovers.isEmpty()) {
                    notify("Nothing was left behind by an ended agent session.")
                    return@invokeLater
                }
                val list = leftovers.joinToString("\n") { ":${it.port} ${it.process} (${it.pid})${it.project?.let { p -> " · ${p.name}" } ?: ""}" }
                if (Messages.showYesNoDialog(project, "Stop ${leftovers.size} leftover process(es)?\n\n$list", "Clean Up", "Stop", "Cancel", Messages.getWarningIcon()) == Messages.YES) {
                    background {
                        leftovers.forEach { PortPilotCli.stop(it.pid) }
                        notify("Stopped ${leftovers.size} leftover process(es).")
                        refreshNow()
                    }
                }
            }
        }
    }

    private fun action(text: String, icon: javax.swing.Icon?, run: () -> Unit) = object : AnAction(text, null, icon), DumbAware {
        override fun actionPerformed(e: AnActionEvent) = run()
        override fun getActionUpdateThread() = ActionUpdateThread.BGT
    }

    override fun dispose() {
        timer?.cancel(false)
    }
}
