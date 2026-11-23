package dev.portpilot

import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.openapi.wm.StatusBarWidgetFactory
import com.intellij.openapi.wm.ToolWindowManager
import com.intellij.util.Consumer
import com.intellij.util.concurrency.AppExecutorUtil
import java.awt.event.MouseEvent
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

class PortPilotStatusWidgetFactory : StatusBarWidgetFactory {
    override fun getId() = "dev.portpilot.status"
    override fun getDisplayName() = "PortPilot"
    override fun isAvailable(project: Project) = true
    override fun createWidget(project: Project): StatusBarWidget = PortPilotStatusWidget(project)
    override fun disposeWidget(widget: StatusBarWidget) = Disposer.dispose(widget)
    override fun canBeEnabledOn(statusBar: StatusBar) = true
}

/** This project's ports, and a warning when an ended agent session left something running. */
private class PortPilotStatusWidget(private val project: Project) : StatusBarWidget, StatusBarWidget.TextPresentation {
    private var statusBar: StatusBar? = null
    private var text = ""
    private var tooltip = "PortPilot"
    private var timer: ScheduledFuture<*>? = null

    override fun ID() = "dev.portpilot.status"
    override fun getPresentation() = this
    override fun getText() = text
    override fun getTooltipText() = tooltip
    override fun getAlignment() = 0f

    override fun getClickConsumer() = Consumer<MouseEvent> {
        ToolWindowManager.getInstance(project).getToolWindow("PortPilot")?.show()
    }

    override fun install(statusBar: StatusBar) {
        this.statusBar = statusBar
        val seconds = PortPilotSettings.instance.state.refreshSeconds.toLong().coerceAtLeast(2)
        timer = AppExecutorUtil.getAppScheduledExecutorService().scheduleWithFixedDelay(::update, 1, seconds * 2, TimeUnit.SECONDS)
    }

    private fun update() {
        if (PortPilotCli.resolve() == null) return
        val all = PortPilotCli.list()
        val mine = all.filter { it.belongsTo(project) }.map { it.port }.distinct().sorted()
        val leftovers = all.count { it.orphaned }
        text = (if (mine.isEmpty()) "No ports" else mine.take(3).joinToString(" ") { ":$it" } + if (mine.size > 3) " …" else "") +
            if (leftovers > 0) "  ⚠ $leftovers" else ""
        tooltip = "PortPilot: ${mine.size} port(s) in this project" + if (leftovers > 0) ", $leftovers left behind by ended agent sessions" else ""
        statusBar?.updateWidget(ID())
    }

    override fun dispose() {
        timer?.cancel(false)
    }
}
