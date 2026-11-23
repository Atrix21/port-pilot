# Homebrew cask, kept current by the release workflow.
#   brew tap atrix21/port-pilot https://github.com/Atrix21/port-pilot
#   brew install --cask portpilot
cask "portpilot" do
  arch arm: "arm64", intel: "x64"

  version "1.0.0"
  sha256 arm:   "0000000000000000000000000000000000000000000000000000000000000000",
         intel: "0000000000000000000000000000000000000000000000000000000000000000"

  url "https://github.com/Atrix21/port-pilot/releases/download/v#{version}/PortPilot-osx-#{arch}.zip"
  name "PortPilot"
  desc "See what's listening on your ports, which project and agent it belongs to, and stop it"
  homepage "https://github.com/Atrix21/port-pilot"

  depends_on macos: ">= :monterey"

  app "PortPilot.app"
  binary "#{appdir}/PortPilot.app/Contents/MacOS/portpilot"

  # The app is ad-hoc signed, not notarised.
  postflight do
    system_command "/usr/bin/xattr", args: ["-dr", "com.apple.quarantine", "#{appdir}/PortPilot.app"]
  end

  zap trash: [
    "~/.config/PortPilot",
    "~/.local/share/PortPilot",
    "~/Library/LaunchAgents/com.portpilot.desktop.plist",
    "~/Library/LaunchAgents/com.portpilot.watch.plist",
  ]
end
