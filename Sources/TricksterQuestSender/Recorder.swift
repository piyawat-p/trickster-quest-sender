import AppKit

/// One thing the user did while recording, with the text that was on screen right before it.
struct RecordedEvent {
    enum Kind {
        case click(CGPoint)
        case key(String)
    }

    let kind: Kind
    let time: Date
    /// OCR (+ page image for key presses) of the frame captured just before the action.
    let ocr: Task<FrameResult, Never>?
}

struct FrameResult {
    var hits: [TextHit] = []
    /// Fingerprint of the dialog page the key was pressed on.
    var pageHash: Data?
}

/// Watches the user play one round: mouse clicks and option keys in other apps (the browser).
/// Clicks on this app's own window are not seen by global monitors, so the Stop button isn't recorded.
@MainActor
final class ActionRecorder {
    private(set) var events: [RecordedEvent] = []
    var onChange: ((Int) -> Void)?

    private var monitors: [Any] = []
    private var captureTask: Task<Void, Never>?
    /// Last few screenshots (newest last). An action is matched against frames from *before* it.
    private var frames: [(image: CGImage, bounds: CGRect)] = []

    func start(near point: CGPoint) {
        events = []
        captureTask = Task { [weak self] in
            guard let target = try? await ScreenReader.captureTarget(near: point) else { return }
            while !Task.isCancelled {
                if let frame = try? await ScreenReader.captureFrame(target), let self {
                    self.frames.append(frame)
                    if self.frames.count > 3 { self.frames.removeFirst() }
                }
                try? await Task.sleep(nanoseconds: 150_000_000)
            }
        }
        if let m = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .keyDown], handler: { [weak self] event in
            MainActor.assumeIsolated { self?.handle(event) }
        }) {
            monitors.append(m)
        }
        debugLog("recording started")
    }

    func stop() -> [RecordedEvent] {
        monitors.forEach { NSEvent.removeMonitor($0) }
        monitors = []
        captureTask?.cancel()
        captureTask = nil
        frames = []
        debugLog("recording stopped: \(events.count) events")
        return events
    }

    private func handle(_ event: NSEvent) {
        switch event.type {
        case .leftMouseDown:
            let p = event.cgEvent?.location ?? Clicker.currentLocation
            // Ignore the second click of a double-click.
            if let last = events.last, case .click(let lp) = last.kind,
               Date().timeIntervalSince(last.time) < 0.35, hypot(p.x - lp.x, p.y - lp.y) < 6 {
                return
            }
            append(.click(p))
        case .keyDown:
            // Only dialog keys (1–0, Enter, Space). F8/Esc and everything else are ignored.
            guard !event.isARepeat, let name = KeyMap.name(for: event.keyCode) else { return }
            append(.key(name))
        default:
            break
        }
    }

    private func append(_ kind: RecordedEvent.Kind) {
        var ocr: Task<FrameResult, Never>?
        let candidates = Array(frames.reversed()) // newest first
        if !candidates.isEmpty {
            let dialogKey: String? = {
                if case .key(let k) = kind { return k }
                return nil
            }()
            ocr = Task.detached(priority: .userInitiated) {
                // For a key press, use the newest frame that actually shows that option line
                // (the very latest frame may already show the next page), and remember how that
                // page looks.
                var newest: [TextHit]?
                for frame in candidates {
                    let hits = (try? await ScreenReader.recognizeText(in: frame.image, bounds: frame.bounds)) ?? []
                    if newest == nil { newest = hits }
                    guard let key = dialogKey else { return FrameResult(hits: hits) }
                    if let option = ScreenReader.findDialogOption(key: key, in: hits) {
                        let shot = ScreenText(hits: hits, image: frame.image, area: frame.bounds)
                        let crop = shot.pixelRect(for: ScreenReader.pageRect(around: option.rect))
                        return FrameResult(hits: hits, pageHash: ImageFingerprint.make(frame.image, crop: crop))
                    }
                }
                return FrameResult(hits: newest ?? [])
            }
        }
        events.append(RecordedEvent(kind: kind, time: Date(), ocr: ocr))
        debugLog("recorded \(kind)")
        onChange?(events.count)
    }
}

/// Turns a recording into: find NPC → press options until the last page → click empty space.
enum RecordingAnalyzer {
    static func makeSteps(from events: [RecordedEvent]) async -> [QuestStep] {
        var ocr: [[TextHit]] = []
        var pageHashOf: [Data?] = []
        for event in events {
            let result = await event.ocr?.value ?? FrameResult()
            ocr.append(result.hits)
            pageHashOf.append(result.pageHash)
        }

        var steps: [QuestStep] = []
        let firstKey = events.firstIndex { if case .key = $0.kind { return true } else { return false } }
        let lastKey = events.lastIndex { if case .key = $0.kind { return true } else { return false } }

        // 1) Start: the last click before the dialog = clicking the NPC.
        let npcClickIndex = events[..<(firstKey ?? events.count)].lastIndex {
            if case .click = $0.kind { return true } else { return false }
        }
        if let i = npcClickIndex, case .click(let p) = events[i].kind {
            if let label = npcLabel(below: p, in: ocr[i]) {
                // Search by name: the usual NPC names plus the label read here (if different).
                var names = QuestStep.defaultNPCNames.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
                if !names.contains(where: { ScreenReader.compact($0) == ScreenReader.compact(label.text) }) {
                    names.append(label.text)
                }
                var step = QuestStep(kind: .clickNPC, name: "หา NPC ด้วยชื่อแล้วคลิก",
                                     text: names.joined(separator: ", "), timeoutMs: 3000, delayAfterMs: 300)
                step.npcOffsetY = (p.y - label.center.y).rounded()
                steps.append(step)
            } else {
                // Couldn't read a name label: fall back to the usual NPC names.
                var step = QuestStep.npcStep(name: "หาแล้วคลิก NPC", delayAfterMs: 300)
                step.npcOffsetY = -35
                steps.append(step)
                debugLog("analyzer: no NPC label found under click \(p)")
            }
        }

        // 2) Middle: one "press until the end" step that knows every page and the last one.
        if let first = firstKey, let last = lastKey {
            var pages: [String] = []
            var hashes: [String] = []
            var key = "1"
            for i in first...last {
                guard case .key(let k) = events[i].kind else { continue }
                key = k
                if let option = ScreenReader.findDialogOption(key: k, in: ocr[i]) {
                    pages.append(option.text)
                    hashes.append(pageHashOf[i]?.base64EncodedString() ?? "")
                } else {
                    debugLog("analyzer: no option line found for key \(k) at event \(i)")
                }
            }
            var dialog = QuestStep(kind: .dialogUntilEnd, name: "กด \(key) จนจบบทสนทนา", key: key,
                                   timeoutMs: 3000, delayAfterMs: 300)
            dialog.pages = pages.joined(separator: "\n")
            dialog.pageHashes = hashes.joined(separator: "\n")
            dialog.endText = pages.last ?? ""
            dialog.maxPresses = max(15, pages.count * 2)
            steps.append(dialog)
            debugLog("analyzer: pages=\(pages) end=\(dialog.endText) images=\(hashes.filter { !$0.isEmpty }.count)/\(pages.count)")
        }

        // 3) End: the click after the last option = click empty space to close the quest window.
        if let last = lastKey,
           let closeIndex = events[(last + 1)...].firstIndex(where: { if case .click = $0.kind { return true } else { return false } }),
           case .click(let p) = events[closeIndex].kind {
            steps.append(QuestStep(kind: .clickPoint, name: "คลิกพื้นที่ว่างเพื่อปิดเควส",
                                   x: p.x.rounded(), y: p.y.rounded(), delayAfterMs: 500))
        }

        return steps
    }

    /// NPC name labels sit just below the sprite: a short Latin-letter line under the click.
    private static func npcLabel(below p: CGPoint, in hits: [TextHit]) -> TextHit? {
        hits
            .filter { hit in
                let dy = hit.center.y - p.y
                let letters = ScreenReader.compact(hit.text)
                return dy > 5 && dy < 130
                    && abs(hit.center.x - p.x) < 90
                    && (3...24).contains(letters.count)
                    && hit.text.contains(where: { $0.isASCII && $0.isLetter })
            }
            .min { hypot($0.center.x - p.x, $0.center.y - p.y) < hypot($1.center.x - p.x, $1.center.y - p.y) }
    }
}
