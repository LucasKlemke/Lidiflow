import AppKit

Settings.migrateLLMProvider()
let app = NSApplication.shared
let delegate = AppController()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
