package dev.portpilot

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.options.Configurable
import com.intellij.ui.components.JBCheckBox
import com.intellij.ui.components.JBTextField
import com.intellij.util.ui.FormBuilder
import javax.swing.JComponent

@State(name = "PortPilotSettings", storages = [Storage("portpilot.xml")])
class PortPilotSettings : PersistentStateComponent<PortPilotSettings.Data> {
    data class Data(var cliPath: String = "", var showAllPorts: Boolean = false, var refreshSeconds: Int = 3, var overview: Boolean = true)

    private var data = Data()

    override fun getState(): Data = data

    override fun loadState(state: Data) {
        data = state
    }

    companion object {
        val instance: PortPilotSettings get() = ApplicationManager.getApplication().getService(PortPilotSettings::class.java)
    }
}

class PortPilotConfigurable : Configurable {
    private val path = JBTextField()
    private val all = JBCheckBox("Show every port on the machine, not just this project's")
    private val seconds = JBTextField()

    override fun getDisplayName() = "PortPilot"

    override fun createComponent(): JComponent = FormBuilder.createFormBuilder()
        .addLabeledComponent("portpilot binary (empty: find it on PATH):", path)
        .addLabeledComponent("Refresh every (seconds):", seconds)
        .addComponent(all)
        .addComponentFillVertically(javax.swing.JPanel(), 0)
        .panel

    override fun isModified(): Boolean {
        val s = PortPilotSettings.instance.state
        return path.text != s.cliPath || all.isSelected != s.showAllPorts || seconds.text != s.refreshSeconds.toString()
    }

    override fun apply() {
        val s = PortPilotSettings.instance.state
        s.cliPath = path.text.trim()
        s.showAllPorts = all.isSelected
        s.refreshSeconds = seconds.text.trim().toIntOrNull()?.coerceIn(1, 60) ?: 3
    }

    override fun reset() {
        val s = PortPilotSettings.instance.state
        path.text = s.cliPath
        all.isSelected = s.showAllPorts
        seconds.text = s.refreshSeconds.toString()
    }
}
