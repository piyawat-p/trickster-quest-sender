import AppKit

/// Listens for key presses both when the app is focused and when the browser is focused.
/// Global monitoring needs Accessibility permission.
final class HotkeyMonitor {
    static let f8: UInt16 = 100
    static let escape: UInt16 = 53

    private var monitors: [Any] = []

    init(handler: @escaping (UInt16) -> Void) {
        if let global = NSEvent.addGlobalMonitorForEvents(matching: .keyDown, handler: { event in
            handler(event.keyCode)
        }) {
            monitors.append(global)
        }
        if let local = NSEvent.addLocalMonitorForEvents(matching: .keyDown, handler: { event in
            handler(event.keyCode)
            return event
        }) {
            monitors.append(local)
        }
    }

    deinit {
        monitors.forEach { NSEvent.removeMonitor($0) }
    }
}
