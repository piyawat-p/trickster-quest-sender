import SwiftUI

@main
struct TricksterQuestSenderApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    /// Created exactly once. (A `@State` initial value is re-evaluated every time SwiftUI
    /// re-creates the App struct, which would build extra runners with their own key monitors.)
    private let runner = AppModel.runner

    var body: some Scene {
        WindowGroup("Trickster Quest Sender") {
            ContentView().environment(runner)
        }
        .windowResizability(.contentMinSize)
        .commands { TabCommands(runner: runner) }
    }
}

@MainActor
enum AppModel {
    static let runner = QuestRunner()
}

/// Browser-like keyboard shortcuts for tabs.
struct TabCommands: Commands {
    let runner: QuestRunner

    var body: some Commands {
        CommandMenu("แท็บ") {
            Button("แท็บใหม่") { runner.addTab() }
                .keyboardShortcut("t")
            Button("เริ่มบันทึกการเล่นใหม่ (แท็บนี้)") { runner.startRecording() }
                .keyboardShortcut("r", modifiers: [.command, .shift])
            Button("เปิดแท็บที่ปิดล่าสุด") { runner.reopenClosedTab() }
                .keyboardShortcut("t", modifiers: [.command, .shift])
                .disabled(!runner.canReopenTab)
            Button("ปิดแท็บนี้") { runner.closeActiveTab() }
                .disabled(runner.tabs.count <= 1)
            Divider()
            Button("แท็บถัดไป") { runner.selectAdjacentTab(1) }
                .keyboardShortcut("]", modifiers: [.command, .shift])
            Button("แท็บก่อนหน้า") { runner.selectAdjacentTab(-1) }
                .keyboardShortcut("[", modifiers: [.command, .shift])
            Divider()
            ForEach(0..<9, id: \.self) { i in
                Button("ไปแท็บที่ \(i + 1)") { runner.selectTab(at: i) }
                    .keyboardShortcut(KeyEquivalent(Character("\(i + 1)")), modifiers: .command)
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        debugLog("launched; windows=\(NSApp.windows.count)")
        // Event monitors must be installed only now, after NSApplication is fully up.
        AppModel.runner.startSystemHooks()
        // Log every mouse click the app receives, to see if clicks reach the window.
        NSEvent.addLocalMonitorForEvents(matching: .leftMouseDown) { event in
            debugLog("mouseDown in window=\(event.window?.title ?? "nil") key=\(event.window?.isKeyWindow ?? false) at \(event.locationInWindow)")
            return event
        }
        // Needed when launched via `swift run` (no .app bundle).
        NSApp.setActivationPolicy(.regular)
        // macOS 14+ ignores activate(ignoringOtherApps:); ask to become the active app
        // and make our window key so it accepts clicks and typing.
        DispatchQueue.main.async {
            NSApp.activate()
            NSApp.windows.first { $0.canBecomeKey }?.makeKeyAndOrderFront(nil)
            debugLog("activated: isActive=\(NSApp.isActive) keyWindow=\(NSApp.keyWindow?.title ?? "nil")")
            for w in NSApp.windows {
                debugLog("window \(type(of: w)) title='\(w.title)' visible=\(w.isVisible) level=\(w.level.rawValue) frame=\(w.frame) ignoresMouse=\(w.ignoresMouseEvents) alpha=\(w.alphaValue)")
            }
        }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        true
    }
}
