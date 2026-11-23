plugins {
    id("org.jetbrains.kotlin.jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.2.1"
}

group = "dev.portpilot"
version = providers.gradleProperty("pluginVersion").getOrElse("1.0.0")

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        // Built against 2024.1 (Java 17); runs on every IntelliJ-based IDE from 2024.1 on.
        intellijIdeaCommunity("2024.1.7")
        pluginVerifier()
    }
}

kotlin {
    jvmToolchain(17)
}

intellijPlatform {
    instrumentCode = false
    pluginConfiguration {
        id = "dev.portpilot"
        name = "PortPilot"
        version = project.version.toString()
        ideaVersion {
            sinceBuild = "241"
            untilBuild = provider { null }
        }
    }
    pluginVerification {
        ides {
            ide(org.jetbrains.intellij.platform.gradle.IntelliJPlatformType.IntellijIdeaCommunity, "2024.1.7")
        }
    }
    publishing {
        token = providers.environmentVariable("JETBRAINS_TOKEN")
    }
}
