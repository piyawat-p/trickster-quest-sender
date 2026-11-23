import Foundation
import CoreGraphics
import ApplicationServices

/// Posts real mouse clicks / key presses. Does not touch the game itself.
enum Clicker {
    /// Current mouse position in global screen coordinates (top-left origin).
    static var currentLocation: CGPoint {
        CGEvent(source: nil)?.location ?? .zero
    }

    static func click(at point: CGPoint) async {
        let source = CGEventSource(stateID: .hidSystemState)
        CGEvent(mouseEventSource: source, mouseType: .mouseMoved,
                mouseCursorPosition: point, mouseButton: .left)?.post(tap: .cghidEventTap)
        try? await Task.sleep(nanoseconds: 40_000_000)
        CGEvent(mouseEventSource: source, mouseType: .leftMouseDown,
                mouseCursorPosition: point, mouseButton: .left)?.post(tap: .cghidEventTap)
        try? await Task.sleep(nanoseconds: 60_000_000)
        CGEvent(mouseEventSource: source, mouseType: .leftMouseUp,
                mouseCursorPosition: point, mouseButton: .left)?.post(tap: .cghidEventTap)
    }

    static func press(keyCode: CGKeyCode) async {
        let source = CGEventSource(stateID: .hidSystemState)
        CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true)?.post(tap: .cghidEventTap)
        try? await Task.sleep(nanoseconds: 50_000_000)
        CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false)?.post(tap: .cghidEventTap)
    }

    static var isTrusted: Bool { AXIsProcessTrusted() }

    static func requestTrust() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }
}
